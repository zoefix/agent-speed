namespace AgentSpeed.Linux.Platform;

/// <summary>Retries an idempotent activation while the lock holder is still registering its session-bus service.</summary>
internal static class ExistingInstanceActivation
{
    internal const int DefaultAttempts = 20;
    internal static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(150);

    internal static async Task<Exception?> RetryAsync(
        Func<Task> activate,
        int attempts = DefaultAttempts,
        TimeSpan? retryDelay = null,
        Func<TimeSpan, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(activate);
        if (attempts < 1) throw new ArgumentOutOfRangeException(nameof(attempts));
        var delay = retryDelay ?? DefaultRetryDelay;
        var wait = delayAsync ?? (duration => Task.Delay(duration));
        Exception? failure = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                await activate();
                return null;
            }
            catch (Exception ex)
            {
                failure = ex;
                if (attempt + 1 < attempts) await wait(delay);
            }
        }

        return failure;
    }
}
