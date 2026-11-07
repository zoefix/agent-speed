using System.Net.Sockets;

namespace AgentSpeed.Core.Lan;

/// <summary>
/// One connection on the server's port (LAN 4.2–4.7): the first line within 5 s and 8192 bytes picks HTTP or the line
/// protocol; an HTTP request is answered once and closed, a WebSocket upgrade or a valid hello becomes a stream that sends
/// frames by the push rules. One write at a time: a frame that is due while the previous one is still being written is
/// skipped and the latest goes out when the write completes; a write stuck for 5 s closes the connection.
/// </summary>
internal sealed class LanConnection
{
    private const int HeadTimeoutMs = 5000;
    private const int DrainMs = 1000;
    private const long StuckMs = 5000;
    private const int SendBufferBytes = 64 * 1024;

    private readonly LanServer _server;
    private readonly Socket _socket;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly object _gate = new();
    private LanPushSchedule? _schedule;
    private bool _webSocket;
    private bool _busy;
    private long _busySince;
    private bool _skipped;
    private bool _closeSent;
    private volatile bool _streaming;
    private int _closing;

    public LanConnection(LanServer server, Socket socket, string peer)
    {
        _server = server;
        _socket = socket;
        Peer = peer;
        try
        {
            socket.NoDelay = true;
            socket.SendBufferSize = SendBufferBytes;
        }
        catch (SocketException)
        {
        }
    }

    public string Peer { get; }

    /// <summary>A WebSocket or line stream after its upgrade or hello.</summary>
    public bool IsStreaming => _streaming;

    public async Task RunAsync()
    {
        try
        {
            await ServeAsync(_life.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _closing, 1);
            _server.Remove(this);
            if (_streaming)
            {
                LanLog.Write($"{Peer} {(_webSocket ? "websocket" : "line")} stream closed");
            }

            _socket.Dispose();
            _life.Dispose();
        }
    }

    /// <summary>Sends the latest frame when the push rules say so (LAN 4.7).</summary>
    public void Push(bool changed, long nowMs)
    {
        var payloads = _server.Payloads();
        byte[] payload;
        lock (_gate)
        {
            if (_schedule is null || payloads is not { } latest || Volatile.Read(ref _closing) != 0 || !_schedule.Due(nowMs, changed))
            {
                return;
            }

            if (_busy)
            {
                _skipped = true;
                if (nowMs - _busySince < StuckMs)
                {
                    return;
                }

                payload = [];
            }
            else
            {
                _schedule.Sent(nowMs);
                _busy = true;
                _busySince = nowMs;
                payload = _webSocket ? latest.WebSocket : latest.Line;
            }
        }

        if (payload.Length == 0)
        {
            LanLog.Write($"{Peer} closed: more than 64 KiB not yet sent");
            Abort();
            return;
        }

        _ = SendAsync(payload);
    }

    /// <summary>
    /// Closes the connection: a stream with a WebSocket close frame (<paramref name="code"/>) or by ending its output. The task
    /// completes once the close frame is written (the app waits for it briefly when it quits).
    /// </summary>
    public Task BeginClose(int code)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return Task.CompletedTask;
        }

        if (!_streaming)
        {
            Cancel();
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            try
            {
                if (_webSocket)
                {
                    await SendCloseAsync(code).ConfigureAwait(false);
                }
                else
                {
                    _socket.Shutdown(SocketShutdown.Send);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
            finally
            {
                CancelAfter(DrainMs);
            }
        });
    }

    private async Task ServeAsync(CancellationToken token)
    {
        var buffer = new byte[LanRequestHead.Limit];
        var count = 0;
        int headLength;
        LanFirstLine kind;
        using (var head = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            head.CancelAfter(HeadTimeoutMs);
            try
            {
                while ((kind = LanRequestHead.Classify(buffer.AsSpan(0, count))) == LanFirstLine.Incomplete)
                {
                    var read = await _socket.ReceiveAsync(buffer.AsMemory(count), SocketFlags.None, head.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    count += read;
                }

                if (kind == LanFirstLine.Close)
                {
                    return;
                }

                if (kind == LanFirstLine.Line)
                {
                    var end = Array.IndexOf(buffer, (byte)'\n', 0, count);
                    var length = end > 0 && buffer[end - 1] == '\r' ? end - 1 : end;
                    await LinesAsync(buffer.AsMemory(0, length).ToArray(), token).ConfigureAwait(false);
                    return;
                }

                while ((headLength = LanRequestHead.HeadLength(buffer.AsSpan(0, count))) < 0)
                {
                    if (count == buffer.Length)
                    {
                        return;
                    }

                    var read = await _socket.ReceiveAsync(buffer.AsMemory(count), SocketFlags.None, head.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    count += read;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return;
            }
        }

        if (LanRequestHead.Parse(buffer.AsSpan(0, count)) is not { } request)
        {
            return;
        }

        await HttpAsync(request, buffer.AsSpan(headLength, count - headLength).ToArray(), token).ConfigureAwait(false);
    }

    private async Task HttpAsync(LanRequestHead request, byte[] rest, CancellationToken token)
    {
        var (response, frame) = _server.Respond(request, Peer, _server.Now);
        var path = request.Path is LanResponder.LivePath or LanResponder.StreamPath ? " " + request.Path : string.Empty;
        LanLog.Throttled($"{Peer} http {response.Status}{path}");
        if (response.KeyAccepted)
        {
            _server.RaiseChanged();
        }

        if (!response.IsUpgrade)
        {
            await WriteAsync(LanResponder.Bytes(response, frame), token).ConfigureAwait(false);
            await DrainAsync().ConfigureAwait(false);
            return;
        }

        await WriteAsync(LanResponder.Bytes(response, null), token).ConfigureAwait(false);
        StartStream(webSocket: true);
        var buffer = new byte[LanRequestHead.Limit];
        rest.CopyTo(buffer, 0);
        var count = rest.Length;
        while (true)
        {
            while (true)
            {
                var frameIn = LanWebSocket.Client(buffer.AsSpan(0, count));
                if (frameIn.Action == LanClientAction.Incomplete)
                {
                    break;
                }

                switch (frameIn.Action)
                {
                    case LanClientAction.Pong:
                        await WriteAsync(LanWebSocket.Frame(LanWebSocket.PongOpcode, frameIn.Payload), token).ConfigureAwait(false);
                        break;
                    case LanClientAction.EchoClose:
                        Interlocked.Exchange(ref _closing, 1);
                        await SendCloseAsync(frameIn.Code).ConfigureAwait(false);
                        return;
                    case LanClientAction.Close:
                        Interlocked.Exchange(ref _closing, 1);
                        LanLog.Throttled($"{Peer} websocket closed with {frameIn.Code}");
                        await SendCloseAsync(frameIn.Code).ConfigureAwait(false);
                        await DrainAsync().ConfigureAwait(false);
                        return;
                }

                var consumed = Math.Max(frameIn.Consumed, 1);
                Buffer.BlockCopy(buffer, consumed, buffer, 0, count - consumed);
                count -= consumed;
            }

            var read = await _socket.ReceiveAsync(buffer.AsMemory(count), SocketFlags.None, token).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            count += read;
        }
    }

    private async Task LinesAsync(byte[] line, CancellationToken token)
    {
        if (_server.Hello(line, Peer, _server.Now) is { } error)
        {
            LanLog.Throttled($"{Peer} line {error}");
            await WriteAsync([.. LanResponder.ErrorBody(error), (byte)'\n'], token).ConfigureAwait(false);
            await DrainAsync().ConfigureAwait(false);
            return;
        }

        _server.RaiseChanged();
        StartStream(webSocket: false);
        var scratch = new byte[4096];
        while (await _socket.ReceiveAsync(scratch, SocketFlags.None, token).ConfigureAwait(false) > 0)
        {
        }
    }

    private void StartStream(bool webSocket)
    {
        lock (_gate)
        {
            _webSocket = webSocket;
            _schedule = new LanPushSchedule(LanReceiverKind.Stream);
        }

        _streaming = true;
        LanLog.Write($"{Peer} {(webSocket ? "websocket" : "line")} stream opened");
        _server.RaiseChanged();
        Push(changed: false, _server.Now);
    }

    private async Task SendAsync(byte[] payload)
    {
        try
        {
            while (true)
            {
                await WriteAsync(payload, _life.Token).ConfigureAwait(false);
                var latest = _server.Payloads();
                lock (_gate)
                {
                    if (!_skipped || latest is null || Volatile.Read(ref _closing) != 0)
                    {
                        _busy = false;
                        return;
                    }

                    var now = _server.Now;
                    _skipped = false;
                    _schedule?.Sent(now);
                    _busySince = now;
                    payload = _webSocket ? latest.Value.WebSocket : latest.Value.Line;
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            Abort();
        }
    }

    /// <summary>Writes <paramref name="data"/> unless a WebSocket close frame went out already.</summary>
    private async Task WriteAsync(byte[] data, CancellationToken token)
    {
        await _write.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_closeSent)
            {
                await SendAllAsync(data, token).ConfigureAwait(false);
            }
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task SendCloseAsync(int? code)
    {
        using var timeout = new CancellationTokenSource(DrainMs);
        await _write.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            if (_closeSent)
            {
                return;
            }

            _closeSent = true;
            await SendAllAsync(LanWebSocket.Close(code), timeout.Token).ConfigureAwait(false);
            _socket.Shutdown(SocketShutdown.Send);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task SendAllAsync(byte[] data, CancellationToken token)
    {
        var sent = 0;
        while (sent < data.Length)
        {
            sent += await _socket.SendAsync(data.AsMemory(sent), SocketFlags.None, token).ConfigureAwait(false);
        }
    }

    /// <summary>Ends the output and reads what the peer still sends for up to a second, so the reply is not reset away.</summary>
    private async Task DrainAsync()
    {
        try
        {
            _socket.Shutdown(SocketShutdown.Send);
            using var drain = new CancellationTokenSource(DrainMs);
            var scratch = new byte[1024];
            while (await _socket.ReceiveAsync(scratch, SocketFlags.None, drain.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private void Abort()
    {
        Interlocked.Exchange(ref _closing, 1);
        Cancel();
    }

    private void Cancel()
    {
        try
        {
            _life.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CancelAfter(int milliseconds)
    {
        try
        {
            _life.CancelAfter(milliseconds);
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
