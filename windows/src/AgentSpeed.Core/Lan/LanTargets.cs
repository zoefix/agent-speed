using System.Net;
using System.Net.Sockets;

namespace AgentSpeed.Core.Lan;

/// <summary>
/// The targets of LAN 5: every enabled target sends the feed's frames by the push rules of LAN 4.7, as UDP datagrams (a name
/// resolved at most every 30 s and again after a send error) or HTTP <c>POST</c> requests (one at a time, resolved before each,
/// backing off after a failure), only to addresses that are local. Thread-safe; sends run on the thread pool.
/// </summary>
public sealed class LanTargets : IDisposable
{
    public const long UdpResolveMs = 30_000;

    private readonly object _gate = new();
    private readonly ILanResolver _resolver;
    private readonly Func<long> _clock;
    private readonly string _userAgent;
    private readonly Dictionary<string, Runner> _runners = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _life = new();
    private Socket? _udp4;
    private Socket? _udp6;
    private bool _network = true;
    private bool _disposed;

    public LanTargets(ILanResolver resolver, string userAgent, Func<long>? clock = null)
    {
        _resolver = resolver;
        _userAgent = userAgent;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>A status changed; raised on any thread.</summary>
    public event Action? Changed;

    /// <summary>Follows the settings: new, edited and removed targets, the switches and the network master switch.</summary>
    public void Apply(IReadOnlyList<LanTarget> targets, bool networkEnabled)
    {
        ArgumentNullException.ThrowIfNull(targets);
        lock (_gate)
        {
            var restart = networkEnabled && !_network;
            _network = networkEnabled;
            foreach (var id in _runners.Keys.Where(id => targets.All(t => t.Id != id)).ToList())
            {
                _runners.Remove(id);
            }

            foreach (var target in targets)
            {
                if (_runners.TryGetValue(target.Id, out var runner) && !restart && runner.Target.Kind == target.Kind && runner.Target.Address == target.Address
                    && runner.Target.Key == target.Key && (runner.Target.Enabled || !target.Enabled))
                {
                    runner.Target = target;
                    continue;
                }

                if (target.Parsed is { } parsed)
                {
                    _runners[target.Id] = new Runner(target, parsed);
                }
            }
        }

        RaiseChanged();
    }

    /// <summary>The tick's frame: each enabled target sends it when its push rules say so.</summary>
    public void Publish(LanFrame frame, bool changed, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var starts = new List<Runner>();
        lock (_gate)
        {
            if (!_network || _disposed)
            {
                return;
            }

            foreach (var runner in _runners.Values)
            {
                if (!runner.Target.Enabled)
                {
                    continue;
                }

                var due = runner.Schedule.Due(nowMs, changed);
                if (runner.Busy)
                {
                    continue;
                }

                var send = runner.Target.Kind == LanTargetKind.Http && runner.Failures > 0 ? nowMs >= runner.RetryAtMs : due;
                if (!send)
                {
                    continue;
                }

                runner.Schedule.Sent(nowMs);
                runner.Busy = true;
                starts.Add(runner);
            }
        }

        foreach (var runner in starts)
        {
            _ = runner.Target.Kind == LanTargetKind.Udp ? SendUdpAsync(runner, frame) : SendHttpAsync(runner, frame);
        }
    }

    /// <summary>LAN 5.5 for the rows: off, network off, or the latest attempt's outcome.</summary>
    public LanTargetStatus StatusOf(string id)
    {
        lock (_gate)
        {
            if (!_runners.TryGetValue(id, out var runner) || !runner.Target.Enabled)
            {
                return LanTargetStatus.Off;
            }

            return _network ? runner.Status : LanTargetStatus.NetworkOff;
        }
    }

    /// <summary><i>Send Test</i> (DESIGN 14): <paramref name="frame"/> once to an address that is not saved; the outcome.</summary>
    public async Task<LanTargetStatus> TestAsync(LanTargetKind kind, string address, string? key, LanFrame frame, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (LanTargetAddress.Parse(kind, address) is not { } parsed)
        {
            return new LanTargetStatus(LanTargetStatusKind.NotFound);
        }

        var udp = kind == LanTargetKind.Udp;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _life.Token);
        timeout.CancelAfter(LanHttpPost.TimeoutMs * 2);
        try
        {
            var (status, ip) = await LanTargetResolution.ResolveAsync(_resolver, parsed, udp, timeout.Token).ConfigureAwait(false);
            if (ip is null)
            {
                return new LanTargetStatus(status);
            }

            var endpoint = new IPEndPoint(ip, parsed.Port);
            if (udp)
            {
                return await SendDatagramAsync(endpoint, LanDatagram.Build(frame, key).Bytes, timeout.Token).ConfigureAwait(false)
                    ? new LanTargetStatus(LanTargetStatusKind.Ok, LastSuccess: DateTimeOffset.Now)
                    : new LanTargetStatus(LanTargetStatusKind.Failed);
            }

            return Outcome(await LanHttpPost.SendAsync(endpoint, LanHttpPost.Request(parsed, frame.Bytes, key, _userAgent), timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return new LanTargetStatus(udp ? LanTargetStatusKind.NotFound : LanTargetStatusKind.Unreachable);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _runners.Clear();
            _udp4?.Dispose();
            _udp6?.Dispose();
            _udp4 = null;
            _udp6 = null;
        }

        _life.Cancel();
        _life.Dispose();
    }

    private async Task SendUdpAsync(Runner runner, LanFrame frame)
    {
        LanTargetStatus status;
        try
        {
            var now = _clock();
            IPAddress? address;
            LanTargetStatusKind resolved;
            lock (_gate)
            {
                (resolved, address) = runner.Resolution is { } cached && now - runner.ResolvedAtMs < UdpResolveMs ? cached : (LanTargetStatusKind.Waiting, null);
            }

            if (resolved == LanTargetStatusKind.Waiting)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
                timeout.CancelAfter(LanHttpPost.TimeoutMs);
                try
                {
                    (resolved, address) = await LanTargetResolution.ResolveAsync(_resolver, runner.Address, udp: true, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_life.IsCancellationRequested)
                {
                    (resolved, address) = (LanTargetStatusKind.NotFound, null);
                }

                lock (_gate)
                {
                    runner.Resolution = (resolved, address);
                    runner.ResolvedAtMs = now;
                }
            }

            if (address is null)
            {
                status = new LanTargetStatus(resolved, LastSuccess: runner.Status.LastSuccess);
            }
            else if (await SendDatagramAsync(new IPEndPoint(address, runner.Address.Port), LanDatagram.Build(frame, runner.Target.Key).Bytes, _life.Token)
                .ConfigureAwait(false))
            {
                status = new LanTargetStatus(LanTargetStatusKind.Ok, LastSuccess: DateTimeOffset.Now);
            }
            else
            {
                lock (_gate)
                {
                    runner.Resolution = null;
                }

                status = new LanTargetStatus(LanTargetStatusKind.Failed, LastSuccess: runner.Status.LastSuccess);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        Finish(runner, status, failed: false);
    }

    private async Task SendHttpAsync(Runner runner, LanFrame frame)
    {
        LanTargetStatus status;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            timeout.CancelAfter(LanHttpPost.TimeoutMs);
            LanTargetStatusKind resolved;
            IPAddress? address;
            try
            {
                (resolved, address) = await LanTargetResolution.ResolveAsync(_resolver, runner.Address, udp: false, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_life.IsCancellationRequested)
            {
                (resolved, address) = (LanTargetStatusKind.NotFound, null);
            }

            if (address is null)
            {
                status = new LanTargetStatus(resolved, LastSuccess: runner.Status.LastSuccess);
            }
            else
            {
                var request = LanHttpPost.Request(runner.Address, frame.Bytes, runner.Target.Key, _userAgent);
                var outcome = Outcome(await LanHttpPost.SendAsync(new IPEndPoint(address, runner.Address.Port), request, _life.Token).ConfigureAwait(false));
                status = outcome.Kind == LanTargetStatusKind.Ok ? outcome : outcome with { LastSuccess = runner.Status.LastSuccess };
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        Finish(runner, status, failed: status.Kind != LanTargetStatusKind.Ok);
    }

    private static LanTargetStatus Outcome(int? code) => code switch
    {
        null => new LanTargetStatus(LanTargetStatusKind.Unreachable),
        >= 200 and < 300 => new LanTargetStatus(LanTargetStatusKind.Ok, LastSuccess: DateTimeOffset.Now),
        { } other => new LanTargetStatus(LanTargetStatusKind.HttpStatus, other),
    };

    /// <summary>The attempt's outcome; an HTTP failure waits 2, 4, 8, 16 then 30 s before the next request.</summary>
    private void Finish(Runner runner, LanTargetStatus status, bool failed)
    {
        bool notify;
        lock (_gate)
        {
            runner.Busy = false;
            if (failed)
            {
                runner.Failures++;
                status = status with { RetryAtMs = _clock() + LanBackoff.Ms(runner.Failures) };
            }
            else
            {
                runner.Failures = 0;
            }

            var before = runner.Status;
            runner.Status = status;
            notify = before.Kind != status.Kind || before.HttpCode != status.HttpCode || before.RetryAtMs != status.RetryAtMs
                || (before.LastSuccess is null) != (status.LastSuccess is null);
            if (_runners.GetValueOrDefault(runner.Target.Id) != runner)
            {
                notify = false;
            }
        }

        if (notify)
        {
            RaiseChanged();
        }
    }

    private async Task<bool> SendDatagramAsync(IPEndPoint endpoint, byte[] datagram, CancellationToken token)
    {
        Socket socket;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            socket = endpoint.AddressFamily == AddressFamily.InterNetwork
                ? _udp4 ??= new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true }
                : _udp6 ??= new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        }

        try
        {
            await socket.SendToAsync(datagram, SocketFlags.None, endpoint, token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException error)
        {
            LanLog.Throttled($"udp target send failed ({error.SocketErrorCode})");
            return false;
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private sealed class Runner(LanTarget target, LanTargetAddress address)
    {
        public LanTarget Target { get; set; } = target;

        public LanTargetAddress Address { get; } = address;

        public LanPushSchedule Schedule { get; } = new(target.Kind == LanTargetKind.Udp ? LanReceiverKind.Udp : LanReceiverKind.Http);

        public LanTargetStatus Status { get; set; } = LanTargetStatus.Waiting;

        public bool Busy { get; set; }

        public int Failures { get; set; }

        public long RetryAtMs => Status.RetryAtMs ?? 0;

        public (LanTargetStatusKind Status, IPAddress? Address)? Resolution { get; set; }

        public long ResolvedAtMs { get; set; }
    }
}
