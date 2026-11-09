using System.Security.Principal;
using AgentSpeed.Interop;

namespace AgentSpeed.App;

/// <summary>
/// One instance per user session. A second launch signals the first (which opens the dashboard) and exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    /// <param name="waitForOwner">
    /// How long to wait for a running instance to release the mutex before treating this launch as a second instance
    /// (used by the relaunch after an update, so the exiting old instance does not swallow it).
    /// </param>
    public static SingleInstance? TryAcquire(TimeSpan waitForOwner = default)
    {
        var suffix = UserSuffix();
        var mutex = new Mutex(initiallyOwned: true, $@"Local\AgentSpeed.Instance.{suffix}", out var created);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\AgentSpeed.Activate.{suffix}");
        if (created || (waitForOwner > TimeSpan.Zero && WaitForOwnership(mutex, waitForOwner)))
        {
            return new SingleInstance(mutex, activate);
        }

        try
        {
            User32.AllowSetForegroundWindow(-1);
            activate.Set();
        }
        finally
        {
            activate.Dispose();
            mutex.Dispose();
        }

        return null;
    }

    /// <summary>Invokes <paramref name="callback"/> on a thread-pool thread whenever another launch signals.</summary>
    public void Listen(Action callback)
    {
        _registration ??= ThreadPool.RegisterWaitForSingleObject(
            _activate,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    callback();
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _registration = null;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _mutex.Dispose();
        _activate.Dispose();
    }

    private static bool WaitForOwnership(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner exited without releasing it; ownership passes to this thread.
            return true;
        }
    }

    /// <summary>
    /// The user's instance; a test session with its own settings file (<c>AGENT_SPEED_SETTINGS_FILE</c>) is an instance of
    /// its own, so it can run beside the installed app.
    /// </summary>
    private static string UserSuffix()
    {
        var session = Environment.GetEnvironmentVariable("AGENT_SPEED_SETTINGS_FILE") is { Length: > 0 } file
            ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(file.ToUpperInvariant())))[..12]
            : string.Empty;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return (identity.User?.Value ?? Environment.UserName) + session;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return Environment.UserName + session;
        }
    }
}
