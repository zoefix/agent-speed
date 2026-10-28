#if LINUX
using AgentSpeed.Linux.Platform;
#else
using System.Windows.Threading;
#endif
using AgentSpeed.App;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Services;

internal sealed class SettingsChangedEventArgs(AppSettings previous, AppSettings current) : EventArgs
{
    public AppSettings Previous { get; } = previous;

    public AppSettings Current { get; } = current;
}

/// <summary>Holds the live settings, applies changes immediately and persists them shortly after (coalescing drags).</summary>
internal sealed class SettingsService : IDisposable
{
    private readonly SettingsStore _store;
    private readonly DispatcherTimer _saveTimer;
    private bool _dirty;
    private bool _ephemeral;

    public SettingsService(Dispatcher dispatcher)
    {
        _store = new SettingsStore(AppPaths.SettingsFile);
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => Flush(), dispatcher);
        _saveTimer.Stop();
    }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public AppSettings Current { get; private set; } = AppSettings.Default;

    public void Load()
    {
        var result = _store.Load();
        Current = result.Settings;
        switch (result.Status)
        {
            case SettingsLoadStatus.RecoveredFromCorruption:
                AppLog.Warn($"Settings file was invalid and has been set aside ({(result.BackupPath is null ? "not moved" : "moved")}); defaults are used.");
                break;
            case SettingsLoadStatus.Unreadable:
                AppLog.Warn("Settings file could not be read; defaults are used for this session.");
                break;
        }
    }

    /// <summary>Development sessions (orb demos): keep every change in memory and never write the settings file.</summary>
    public void MakeEphemeral()
    {
        _ephemeral = true;
        _dirty = false;
        _saveTimer.Stop();
    }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var previous = Current;
        var next = change(previous).Validated();
        if (next == previous)
        {
            return;
        }

        Current = next;
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke(this, new SettingsChangedEventArgs(previous, next));
    }

    public void Flush()
    {
        _saveTimer.Stop();
        if (!_dirty || _ephemeral)
        {
            return;
        }

        try
        {
            _store.Save(Current);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Saving settings failed; will retry on the next change.", ex);
        }
    }

    public void Dispose() => Flush();
}
