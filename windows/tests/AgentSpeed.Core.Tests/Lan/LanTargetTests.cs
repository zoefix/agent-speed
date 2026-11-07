using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentSpeed.Core.Lan;

namespace AgentSpeed.Core.Tests.Lan;

/// <summary>LAN 5 against loopback receivers: UDP datagrams with their signature and size rule, HTTP posts and their statuses.</summary>
public sealed class LanTargetTests
{
    private const string Instance = LanServerRig.Instance;
    private long _clock = 5_000_000;

    private long Now => Interlocked.Read(ref _clock);

    private LanTargets NewTargets(ILanResolver? resolver = null) => new(resolver ?? SystemLanResolver.Instance, "AgentSpeed/0.0.0 (Windows)", () => Now);

    private static LanTarget Target(LanTargetKind kind, string address, string? key = null, bool enabled = true) =>
        LanSettings.NewTarget(null, "Desk", kind, address, key, enabled)!;

    private static LanFrame Frame(LanFeed feed, long now, int sources = 1, double speed = 75.9)
    {
        var perSource = Enum.GetValues<SourceKind>().Take(sources)
            .ToDictionary(k => k, k => new LanLiveValues(1234.5 + (int)k, 0.123, 1, 1, 12));
        return feed.Tick(now, 1_794_042_000_250, Instance, "Test Box", new LanTickValues(new LanLiveValues(speed, 0.42, 1, 0, 1), perSource)).Frame;
    }

    [Fact]
    public async Task UdpDatagramsCarryTheSignature()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        using var targets = NewTargets();
        var target = Target(LanTargetKind.Udp, $"127.0.0.1:{port}", "s3cret-phrase");
        targets.Apply([target], networkEnabled: true);
        Assert.Equal(LanTargetStatusKind.Waiting, targets.StatusOf(target.Id).Kind);

        var feed = new LanFeed();
        var frame = Frame(feed, Now);
        targets.Publish(frame, changed: true, Now);
        var datagram = await ReceiveAsync(receiver);
        var newline = Array.IndexOf(datagram, (byte)'\n');
        Assert.Equal(frame.Bytes, datagram[..newline]);
        Assert.Equal(LanSignature.Of("s3cret-phrase", frame.Bytes), Encoding.ASCII.GetString(datagram[(newline + 1)..]));
        await LanServerTests.WaitUntil(() => targets.StatusOf(target.Id).Kind == LanTargetStatusKind.Ok);
        Assert.NotNull(targets.StatusOf(target.Id).LastSuccess);
    }

    [Fact]
    public async Task ALargeUdpFrameLeavesOutHistoryAndSources()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        using var targets = NewTargets();
        targets.Apply([Target(LanTargetKind.Udp, $"127.0.0.1:{port}", "k")], networkEnabled: true);
        var feed = new LanFeed();
        LanFrame frame = null!;
        for (var i = 0; i < 61; i++)
        {
            frame = Frame(feed, Now + i * 1000, sources: 18);
        }

        Assert.True(frame.Bytes.Length > LanDatagram.MaxBytes);
        targets.Publish(frame, changed: true, Now);
        var datagram = await ReceiveAsync(receiver);
        Assert.True(datagram.Length <= LanDatagram.MaxBytes);
        var body = Encoding.UTF8.GetString(datagram[..Array.IndexOf(datagram, (byte)'\n')]);
        Assert.DoesNotContain("\"history\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sources\"", body, StringComparison.Ordinal);
        Assert.Equal(LanDatagram.Build(frame, "k").Bytes, datagram);
    }

    [Fact]
    public async Task HttpTargetsReportOkAndOtherStatuses()
    {
        using var ok = new HttpReceiver(204);
        using var missing = new HttpReceiver(404);
        using var targets = NewTargets();
        var good = Target(LanTargetKind.Http, $"http://127.0.0.1:{ok.Port}/hook?x=1", "s3cret-phrase");
        var bad = Target(LanTargetKind.Http, $"http://127.0.0.1:{missing.Port}");
        targets.Apply([good, bad], networkEnabled: true);
        var frame = Frame(new LanFeed(), Now);
        targets.Publish(frame, changed: true, Now);

        var request = await ok.RequestAsync();
        Assert.StartsWith("POST /hook?x=1 HTTP/1.1\r\n", request.Head, StringComparison.Ordinal);
        Assert.Contains($"Host: 127.0.0.1:{ok.Port}\r\n", request.Head, StringComparison.Ordinal);
        Assert.Contains("User-Agent: AgentSpeed/0.0.0 (Windows)\r\nContent-Type: application/json\r\n", request.Head, StringComparison.Ordinal);
        Assert.Contains($"X-AgentSpeed-Signature: {LanSignature.Of("s3cret-phrase", frame.Bytes)}\r\nConnection: close\r\n", request.Head, StringComparison.Ordinal);
        Assert.Equal(frame.Bytes, request.Body);
        Assert.DoesNotContain("X-AgentSpeed-Signature", (await missing.RequestAsync()).Head, StringComparison.Ordinal);

        await LanServerTests.WaitUntil(() => targets.StatusOf(good.Id).Kind == LanTargetStatusKind.Ok);
        await LanServerTests.WaitUntil(() => targets.StatusOf(bad.Id).Kind == LanTargetStatusKind.HttpStatus);
        var failing = targets.StatusOf(bad.Id);
        Assert.Equal(404, failing.HttpCode);
        Assert.Equal(Now + 2000, failing.RetryAtMs);
    }

    [Fact]
    public async Task AClosedPortIsUnreachableAndBacksOff()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        using var targets = NewTargets();
        var target = Target(LanTargetKind.Http, $"http://127.0.0.1:{port}/x");
        targets.Apply([target], networkEnabled: true);
        var feed = new LanFeed();
        targets.Publish(Frame(feed, Now), changed: true, Now);
        await LanServerTests.WaitUntil(() => targets.StatusOf(target.Id).Kind == LanTargetStatusKind.Unreachable);
        Assert.Equal(Now + 2000, targets.StatusOf(target.Id).RetryAtMs);

        Interlocked.Add(ref _clock, 1000);
        targets.Publish(Frame(feed, Now, speed: 1), changed: true, Now);
        await Task.Delay(200);
        Assert.Equal(Now + 1000, targets.StatusOf(target.Id).RetryAtMs);

        Interlocked.Add(ref _clock, 1000);
        targets.Publish(Frame(feed, Now, speed: 2), changed: true, Now);
        await LanServerTests.WaitUntil(() => targets.StatusOf(target.Id).RetryAtMs == Now + 4000);
    }

    [Fact]
    public async Task ANameThatResolvesOutsideTheNetworkIsNotLocal()
    {
        using var targets = NewTargets(new FixedResolver(IPAddress.Parse("192.168.1.50"), IPAddress.Parse("52.1.2.3")));
        var udp = Target(LanTargetKind.Udp, "desk-display.local:7247");
        var http = Target(LanTargetKind.Http, "http://8.8.8.8/x");
        targets.Apply([udp, http], networkEnabled: true);
        targets.Publish(Frame(new LanFeed(), Now), changed: true, Now);
        await LanServerTests.WaitUntil(() => targets.StatusOf(udp.Id).Kind == LanTargetStatusKind.NotLocal);
        await LanServerTests.WaitUntil(() => targets.StatusOf(http.Id).Kind == LanTargetStatusKind.NotLocal);
        Assert.Equal(LanTargetStatusKind.NotFound,
            (await NewTargets(new FixedResolver()).TestAsync(LanTargetKind.Udp, "nowhere.local:1", null, Frame(new LanFeed(), Now), default)).Kind);
    }

    [Fact]
    public void SwitchesAndTheMasterSwitchSetTheStatus()
    {
        using var targets = NewTargets();
        var on = Target(LanTargetKind.Udp, "127.0.0.1:9");
        var off = Target(LanTargetKind.Udp, "127.0.0.1:9", enabled: false);
        targets.Apply([on, off], networkEnabled: true);
        Assert.Equal(LanTargetStatusKind.Waiting, targets.StatusOf(on.Id).Kind);
        Assert.Equal(LanTargetStatusKind.Off, targets.StatusOf(off.Id).Kind);
        targets.Apply([on, off], networkEnabled: false);
        Assert.Equal(LanTargetStatusKind.NetworkOff, targets.StatusOf(on.Id).Kind);
        Assert.Equal(LanTargetStatusKind.Off, targets.StatusOf(off.Id).Kind);
    }

    private static async Task<byte[]> ReceiveAsync(UdpClient receiver)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return (await receiver.ReceiveAsync(timeout.Token)).Buffer;
    }

    private sealed class FixedResolver(params IPAddress[] addresses) : ILanResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken token) => Task.FromResult<IReadOnlyList<IPAddress>>(addresses);
    }

    /// <summary>A one-request HTTP device on loopback that answers with a fixed status.</summary>
    private sealed class HttpReceiver : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task<(string Head, byte[] Body)> _request;

        public HttpReceiver(int status)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _request = ServeAsync(status);
        }

        public int Port { get; }

        public Task<(string Head, byte[] Body)> RequestAsync() => _request.WaitAsync(TimeSpan.FromSeconds(5));

        public void Dispose() => _listener.Stop();

        private async Task<(string, byte[])> ServeAsync(int status)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var data = new List<byte>();
            var buffer = new byte[4096];
            int end;
            while ((end = IndexOf(data, "\r\n\r\n"u8.ToArray())) < 0)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                data.AddRange(buffer[..read]);
            }

            var head = Encoding.ASCII.GetString(data.ToArray(), 0, end + 4);
            var length = int.Parse(head.Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.Ordinal))[15..].Trim(),
                System.Globalization.CultureInfo.InvariantCulture);
            while (data.Count < end + 4 + length)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                data.AddRange(buffer[..read]);
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Whatever\r\nContent-Length: 0\r\n\r\n"));
            return (head, data.Skip(end + 4).Take(length).ToArray());
        }

        private static int IndexOf(List<byte> data, byte[] pattern)
        {
            for (var i = 0; i + pattern.Length <= data.Count; i++)
            {
                if (pattern.Select((b, j) => data[i + j] == b).All(x => x))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
