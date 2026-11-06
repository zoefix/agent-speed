namespace AgentSpeed.Core.Remote;

/// <summary>
/// When a failed host tries again (REMOTE 4.4): 5, 10, 20, 40, 60 s, then every 60 s; reset after a connection that stayed
/// up for 60 s. Errors that need the user retry every 5 minutes without advancing the back-off.
/// </summary>
internal sealed class RemoteBackoff
{
    public static readonly TimeSpan[] Delays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)];
    public static readonly TimeSpan UserRetryDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ResetAfter = TimeSpan.FromSeconds(60);

    public int Failures { get; private set; }

    /// <summary>The delay before the next attempt after a connection ended with <paramref name="status"/>; <paramref name="upSince"/> is when it received <c>hello</c>.</summary>
    public TimeSpan Delay(RemoteStatus status, DateTimeOffset? upSince, DateTimeOffset now)
    {
        if (status.NeedsUser)
        {
            return UserRetryDelay;
        }

        if (upSince is { } up && now - up >= ResetAfter)
        {
            Failures = 0;
        }

        var delay = Delays[Math.Min(Failures, Delays.Length - 1)];
        Failures++;
        return delay;
    }

    public void Reset() => Failures = 0;
}
