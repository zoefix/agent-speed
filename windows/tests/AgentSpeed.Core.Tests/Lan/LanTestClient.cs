using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentSpeed.Core.Lan;

namespace AgentSpeed.Core.Tests.Lan;

/// <summary>A raw loopback client for the server tests: HTTP, the WebSocket stream and the line protocol, with timeouts.</summary>
internal sealed class LanTestClient : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly byte[] _buffer = new byte[65536];
    private int _count;

    private LanTestClient(Socket socket)
    {
        Socket = socket;
    }

    public Socket Socket { get; }

    public static async Task<LanTestClient> ConnectAsync(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeout = new CancellationTokenSource(Timeout);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
        return new LanTestClient(socket);
    }

    /// <summary>One request: everything the server sends until it closes the connection.</summary>
    public static async Task<string> RequestAsync(int port, string request)
    {
        using var client = await ConnectAsync(port);
        await client.SendAsync(request);
        return Encoding.UTF8.GetString(await client.ReadToEndAsync());
    }

    public async Task SendAsync(string text) => await SendAsync(Encoding.UTF8.GetBytes(text));

    public async Task SendAsync(byte[] data)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        await Socket.SendAsync(data, SocketFlags.None, timeout.Token);
    }

    public async Task<byte[]> ReadToEndAsync()
    {
        while (await FillAsync())
        {
        }

        return Take(_count);
    }

    /// <summary>True while the connection is open: false once the server closed it (or reset it) within the timeout.</summary>
    public async Task<bool> IsOpenAsync(TimeSpan wait)
    {
        using var timeout = new CancellationTokenSource(wait);
        try
        {
            var read = await Socket.ReceiveAsync(_buffer.AsMemory(_count), SocketFlags.None, timeout.Token);
            _count += read;
            return read > 0;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async Task<string> ReadHeadAsync()
    {
        while (true)
        {
            var end = _buffer.AsSpan(0, _count).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                return Encoding.ASCII.GetString(Take(end + 4));
            }

            if (!await FillAsync())
            {
                throw new IOException("closed before the head");
            }
        }
    }

    public async Task<string> ReadLineAsync()
    {
        while (true)
        {
            var end = Array.IndexOf(_buffer, (byte)'\n', 0, _count);
            if (end >= 0)
            {
                return Encoding.UTF8.GetString(Take(end + 1)).TrimEnd('\n');
            }

            if (!await FillAsync())
            {
                throw new IOException("closed before a line");
            }
        }
    }

    /// <summary>The next server WebSocket frame (unmasked): its opcode and payload.</summary>
    public async Task<(int Opcode, byte[] Payload)> ReadFrameAsync()
    {
        while (true)
        {
            if (_count >= 2)
            {
                var size = _buffer[1] & 0x7F;
                var head = 2;
                long length = size;
                if (size == 126 && _count >= 4)
                {
                    length = BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(2));
                    head = 4;
                }
                else if (size == 127 && _count >= 10)
                {
                    length = (long)BinaryPrimitives.ReadUInt64BigEndian(_buffer.AsSpan(2));
                    head = 10;
                }

                if ((size < 126 || head > 2) && _count >= head + length)
                {
                    Assert.Equal(0, _buffer[1] & 0x80);
                    var opcode = _buffer[0] & 0x0F;
                    var frame = Take(head + (int)length);
                    return (opcode, frame[head..]);
                }
            }

            if (!await FillAsync())
            {
                throw new IOException("closed before a frame");
            }
        }
    }

    public static async Task<LanTestClient> UpgradeAsync(int port, string key, string? nonce = null)
    {
        var client = await ConnectAsync(port);
        await client.SendAsync($"GET /v1/stream HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + $"Sec-WebSocket-Key: {nonce ?? "dGhlIHNhbXBsZSBub25jZQ=="}\r\nSec-WebSocket-Version: 13\r\nAuthorization: Bearer {key}\r\n\r\n");
        return client;
    }

    /// <summary>A masked client frame.</summary>
    public static byte[] ClientFrame(int opcode, byte[] payload, bool masked = true)
    {
        byte[] mask = [0x37, 0xfa, 0x21, 0x3d];
        var head = new List<byte> { (byte)(0x80 | opcode), (byte)((masked ? 0x80 : 0) | payload.Length) };
        if (masked)
        {
            head.AddRange(mask);
            return [.. head, .. payload.Select((b, i) => (byte)(b ^ mask[i % 4]))];
        }

        return [.. head, .. payload];
    }

    public static int CloseCode(byte[] payload) => payload.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(payload) : -1;

    public void Dispose() => Socket.Dispose();

    private async Task<bool> FillAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            var read = await Socket.ReceiveAsync(_buffer.AsMemory(_count), SocketFlags.None, timeout.Token);
            _count += read;
            return read > 0;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private byte[] Take(int length)
    {
        var taken = _buffer.AsSpan(0, length).ToArray();
        Buffer.BlockCopy(_buffer, length, _buffer, 0, _count - length);
        _count -= length;
        return taken;
    }
}

/// <summary>A server on 127.0.0.1 with an ephemeral port and a hand-driven clock, fed by a real <see cref="LanFeed"/>.</summary>
internal sealed class LanServerRig : IDisposable
{
    public const string Key = "K7Q4M2X9PT3D";
    public const string Instance = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    private long _clock = 1_000_000;

    public LanServerRig(bool start = true)
    {
        Server = new LanServer(IPAddress.Loopback, () => Interlocked.Read(ref _clock));
        if (start)
        {
            Server.Start(0, Key);
        }
    }

    public LanServer Server { get; }

    public LanFeed Feed { get; } = new("9b1c4e7a2f6d8e03");

    public int Port => Server.BoundPort;

    public long Now => Interlocked.Read(ref _clock);

    public void Advance(long ms) => Interlocked.Add(ref _clock, ms);

    public LanFrame Publish(double outputTokS, int generating = 1)
    {
        var values = new LanTickValues(new LanLiveValues(outputTokS, 0, generating, 0, 1),
            new Dictionary<SourceKind, LanLiveValues> { [SourceKind.Codex] = new(outputTokS, 0, generating, 0, 1) });
        var (frame, changed) = Feed.Tick(Now, 1_794_042_000_250 + Now, Instance, "Test Box", values);
        Server.Publish(frame, changed, Now);
        return frame;
    }

    public void Dispose() => Server.Dispose();
}
