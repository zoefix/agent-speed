namespace AgentSpeed.Core.Lan;

/// <summary>
/// Where the local network feed's log lines go (the app's log). Lines name the peer address, the protocol and status codes
/// (LAN 6): never the key, a frame, a request's query or a target's address.
/// </summary>
public static class LanLog
{
    private const long RepeatMs = 60_000;
    private static readonly Dictionary<string, long> Recent = new(StringComparer.Ordinal);
    private static Action<string>? _sink;

    public static void SetSink(Action<string>? sink) => Volatile.Write(ref _sink, sink);

    internal static void Write(string message)
    {
        try
        {
            Volatile.Read(ref _sink)?.Invoke("LAN: " + message);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
        }
    }

    /// <summary>Writes <paramref name="message"/> unless the same line was written in the last minute (a device that polls).</summary>
    internal static void Throttled(string message)
    {
        var now = Environment.TickCount64;
        lock (Recent)
        {
            if (Recent.TryGetValue(message, out var last) && now - last < RepeatMs)
            {
                return;
            }

            if (Recent.Count > 512)
            {
                Recent.Clear();
            }

            Recent[message] = now;
        }

        Write(message);
    }
}
