namespace AgentSpeed.Core.Remote;

/// <summary>
/// Where the remote hosts' log lines go (the app's log). Lines carry status codes and counts only: never stderr, host
/// names, destinations or paths (REMOTE 4.1).
/// </summary>
public static class RemoteLog
{
    private static Action<string>? _sink;

    public static void SetSink(Action<string>? sink) => Volatile.Write(ref _sink, sink);

    internal static void Write(string message)
    {
        try
        {
            Volatile.Read(ref _sink)?.Invoke(message);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
        }
    }
}
