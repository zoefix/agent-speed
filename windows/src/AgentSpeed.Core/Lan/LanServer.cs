using System.Net;
using System.Net.Sockets;

namespace AgentSpeed.Core.Lan;

/// <summary>
/// The server of LAN 4: one TCP port on every interface (dual-stack, IPv4 alone where IPv6 is missing), local peers only, at
/// most 16 connections, HTTP, the WebSocket stream and the line protocol on the same port. A taken port is tried again every
/// 30 s. Thread-safe: the owner starts, stops and publishes from its thread; connections run on the thread pool.
/// </summary>
public sealed class LanServer : IDisposable
{
    public const int MaxConnections = 16;
    public const long RetryMs = 30_000;
    public const long DeviceWindowMs = 10_000;

    private readonly object _gate = new();
    private readonly IPAddress? _bindAddress;
    private readonly Func<long> _clock;
    private readonly List<LanConnection> _connections = [];
    private readonly Dictionary<string, long> _authorized = new(StringComparer.Ordinal);
    private readonly LanLockout _lockout = new();
    private Socket? _listener;
    private CancellationTokenSource? _accepting;
    private Timer? _retry;
    private bool _wanted;
    private int _port;
    private string _key = string.Empty;
    private LanFrame? _latest;
    private (byte[] WebSocket, byte[] Line)? _payloads;
    private LanServerStatusKind _state = LanServerStatusKind.Off;

    /// <param name="bindAddress">The address to listen on; null for every interface (tests use the loopback address).</param>
    /// <param name="clock">Monotonic milliseconds; <see cref="Environment.TickCount64"/> by default.</param>
    public LanServer(IPAddress? bindAddress = null, Func<long>? clock = null)
    {
        _bindAddress = bindAddress;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>The status or the device count may have changed; raised on any thread.</summary>
    public event Action? Changed;

    /// <summary>The port the listener is bound to (the ephemeral one when started with 0), or 0.</summary>
    public int BoundPort
    {
        get
        {
            lock (_gate)
            {
                return _listener?.LocalEndPoint is IPEndPoint endpoint ? endpoint.Port : 0;
            }
        }
    }

    public LanServerStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new LanServerStatus(_state, _port, _state == LanServerStatusKind.Listening ? DevicesLocked(_clock()) : 0);
            }
        }
    }

    /// <summary>Open connections, for tests.</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _connections.Count;
            }
        }
    }

    /// <summary>Listens on <paramref name="port"/> with <paramref name="key"/>; a running server on the same port only takes the key.</summary>
    public void Start(int port, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_wanted && _port == port)
            {
                ReplaceKeyLocked(key);
                return;
            }
        }

        Stop();
        lock (_gate)
        {
            _wanted = true;
            _port = port;
            _key = key;
            TryListenLocked();
        }

        RaiseChanged();
    }

    /// <summary>Closes the port and every connection (WebSocket close code 1001).</summary>
    public void Stop() => StopAndClose();

    /// <summary>At quit: stops, and waits up to half a second for the streams' close frames to be written.</summary>
    public void Dispose() => Task.WaitAll(StopAndClose(), TimeSpan.FromMilliseconds(500));

    private Task[] StopAndClose()
    {
        List<LanConnection> open;
        lock (_gate)
        {
            if (!_wanted)
            {
                return [];
            }

            _wanted = false;
            _retry?.Dispose();
            _retry = null;
            CloseListenerLocked();
            _state = LanServerStatusKind.Off;
            _latest = null;
            _payloads = null;
            _authorized.Clear();
            open = [.. _connections];
        }

        var closing = open.Select(c => c.BeginClose(LanWebSocket.GoingAway)).ToArray();
        RaiseChanged();
        return closing;
    }

    /// <summary>A new access key (LAN 4.4): every WebSocket (close code 1008) and line connection closes at once.</summary>
    public void SetKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            ReplaceKeyLocked(key);
        }
    }

    /// <summary>The tick's frame: <c>/v1/live</c> answers with it, and every stream sends it by the push rules (LAN 4.7).</summary>
    public void Publish(LanFrame frame, bool changed, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(frame);
        List<LanConnection> streams;
        lock (_gate)
        {
            if (_state != LanServerStatusKind.Listening)
            {
                return;
            }

            _latest = frame;
            _payloads = null;
            streams = _connections.Where(c => c.IsStreaming).ToList();
        }

        foreach (var stream in streams)
        {
            stream.Push(changed, nowMs);
        }
    }

    internal long Now => _clock();

    /// <summary>The latest frame as a WebSocket message and as a line, or null before the first tick.</summary>
    internal (byte[] WebSocket, byte[] Line)? Payloads()
    {
        lock (_gate)
        {
            if (_latest is not { } frame)
            {
                return null;
            }

            return _payloads ??= (LanWebSocket.Frame(LanWebSocket.Text, frame.Bytes), [.. frame.Bytes, (byte)'\n']);
        }
    }

    internal (LanResponse Response, byte[]? Frame) Respond(LanRequestHead head, string peer, long nowMs)
    {
        lock (_gate)
        {
            var response = LanResponder.Respond(head, peer, nowMs, _key, _latest is not null, _lockout);
            if (response.KeyAccepted)
            {
                _authorized[peer] = nowMs;
            }

            return (response, response.Status == 200 ? _latest?.Bytes : null);
        }
    }

    internal string? Hello(ReadOnlySpan<byte> line, string peer, long nowMs)
    {
        lock (_gate)
        {
            var error = LanHello.Check(line, peer, nowMs, _key, _lockout);
            if (error is null)
            {
                _authorized[peer] = nowMs;
            }

            return error;
        }
    }

    internal void Remove(LanConnection connection)
    {
        lock (_gate)
        {
            _connections.Remove(connection);
        }

        if (connection.IsStreaming)
        {
            RaiseChanged();
        }
    }

    internal void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private void ReplaceKeyLocked(string key)
    {
        if (key == _key)
        {
            return;
        }

        _key = key;
        foreach (var connection in _connections.Where(c => c.IsStreaming).ToList())
        {
            _ = connection.BeginClose(LanWebSocket.PolicyViolation);
        }
    }

    private int DevicesLocked(long nowMs)
    {
        foreach (var stale in _authorized.Where(p => nowMs - p.Value >= DeviceWindowMs).Select(p => p.Key).ToList())
        {
            _authorized.Remove(stale);
        }

        return _connections.Where(c => c.IsStreaming).Select(c => c.Peer).Concat(_authorized.Keys).Distinct(StringComparer.Ordinal).Count();
    }

    private void TryListenLocked()
    {
        Socket? socket = null;
        try
        {
            socket = Bind(_port);
            socket.Listen(64);
            _listener = socket;
            _accepting = new CancellationTokenSource();
            _state = LanServerStatusKind.Listening;
            _retry?.Dispose();
            _retry = null;
            LanLog.Write($"listening on port {BoundPortLocked()}");
            _ = AcceptAsync(socket, _accepting.Token);
        }
        catch (SocketException error)
        {
            socket?.Dispose();
            _state = error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied
                ? LanServerStatusKind.PortInUse
                : LanServerStatusKind.Failed;
            LanLog.Write($"port {_port} could not be opened ({error.SocketErrorCode}); retrying in 30 s");
            _retry ??= new Timer(_ => Retry(), null, RetryMs, RetryMs);
        }
    }

    private int BoundPortLocked() => _listener?.LocalEndPoint is IPEndPoint endpoint ? endpoint.Port : _port;

    private void Retry()
    {
        lock (_gate)
        {
            if (!_wanted || _state == LanServerStatusKind.Listening)
            {
                return;
            }

            TryListenLocked();
        }

        RaiseChanged();
    }

    private Socket Bind(int port)
    {
        if (_bindAddress is { } address)
        {
            var single = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            Exclusive(single);
            single.Bind(new IPEndPoint(address, port));
            return single;
        }

        if (Socket.OSSupportsIPv6)
        {
            var dual = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                dual.DualMode = true;
                Exclusive(dual);
                dual.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                return dual;
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.AddressNotAvailable
                or SocketError.ProtocolNotSupported)
            {
                dual.Dispose();
            }
            catch
            {
                dual.Dispose();
                throw;
            }
        }

        var v4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Exclusive(v4);
        v4.Bind(new IPEndPoint(IPAddress.Any, port));
        return v4;
    }

    /// <summary>On Windows no other socket may bind the port while the server holds it.</summary>
    private static void Exclusive(Socket socket)
    {
        if (OperatingSystem.IsWindows())
        {
            socket.ExclusiveAddressUse = true;
        }
    }

    private void CloseListenerLocked()
    {
        _accepting?.Cancel();
        _accepting?.Dispose();
        _accepting = null;
        _listener?.Dispose();
        _listener = null;
    }

    private async Task AcceptAsync(Socket listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            Admit(client);
        }
    }

    /// <summary>LAN 4.1: a peer that is not local, or one past the 16th connection, is closed before anything is read.</summary>
    private void Admit(Socket client)
    {
        if (client.RemoteEndPoint is not IPEndPoint remote || !LanAddress.IsLocal(remote.Address))
        {
            LanLog.Throttled($"{(client.RemoteEndPoint as IPEndPoint)?.Address} refused: not a local address");
            client.Dispose();
            return;
        }

        var peer = LanAddress.PeerKey(remote.Address);
        LanConnection connection;
        lock (_gate)
        {
            if (_state != LanServerStatusKind.Listening || _connections.Count >= MaxConnections)
            {
                if (_state == LanServerStatusKind.Listening)
                {
                    LanLog.Throttled($"{peer} refused: {MaxConnections} connections open");
                }

                client.Dispose();
                return;
            }

            connection = new LanConnection(this, client, peer);
            _connections.Add(connection);
        }

        _ = connection.RunAsync();
    }
}
