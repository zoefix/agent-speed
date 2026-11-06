using AgentSpeed.Controls;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Remote;

namespace AgentSpeed.Settings.Remote;

/// <summary>
/// One host of Settings → 遠端主機 (DESIGN 13): dot, name and destination, the status line with its notes and tooltip, the
/// switch and the actions.
/// </summary>
internal sealed class RemoteHostRowViewModel(string id, Action<string, bool> setEnabled) : ObservableObject
{
    private string _name = string.Empty;
    private string _destination = string.Empty;
    private bool _showsDestination;
    private RemoteDotKind _dot;
    private string _statusText = string.Empty;
    private string? _statusTooltip;
    private string? _clockNote;
    private string? _historyNote;
    private bool _enabled;
    private bool _canRetry;
    private bool _isRetrying;
    private string _toggleName = string.Empty;
    private string _accessibleName = string.Empty;
    private string _actionsName = string.Empty;
    private bool _syncing;
    private bool _copied;

    public string Id { get; } = id;

    /// <summary>The host as last applied (edit, copy, remove read it).</summary>
    public RemoteHost? Host { get; private set; }

    public string Name { get => _name; private set => Set(ref _name, value); }

    public string Destination { get => _destination; private set => Set(ref _destination, value); }

    /// <summary>The destination follows the name unless they read the same.</summary>
    public bool ShowsDestination { get => _showsDestination; private set => Set(ref _showsDestination, value); }

    public RemoteDotKind Dot { get => _dot; private set => Set(ref _dot, value); }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public string? StatusTooltip { get => _statusTooltip; private set => Set(ref _statusTooltip, value); }

    public string? ClockNote
    {
        get => _clockNote;
        private set
        {
            if (Set(ref _clockNote, value))
            {
                OnPropertyChanged(nameof(HasClockNote));
            }
        }
    }

    public bool HasClockNote => !string.IsNullOrEmpty(_clockNote);

    public string? HistoryNote
    {
        get => _historyNote;
        private set
        {
            if (Set(ref _historyNote, value))
            {
                OnPropertyChanged(nameof(HasHistoryNote));
            }
        }
    }

    public bool HasHistoryNote => !string.IsNullOrEmpty(_historyNote);

    /// <summary>The <c>enabled</c> switch; turning it changes the settings at once.</summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value) && !_syncing)
            {
                setEnabled(Id, value);
            }
        }
    }

    /// <summary><i>Retry now</i> is offered (the host is failing).</summary>
    public bool CanRetry { get => _canRetry; private set => Set(ref _canRetry, value); }

    /// <summary>The line counts down to the next attempt.</summary>
    public bool IsCountingDown { get => _isRetrying; private set => Set(ref _isRetrying, value); }

    /// <summary><c>remote.enabled</c> (連線到「devbox」): the switch's accessible name.</summary>
    public string ToggleName { get => _toggleName; private set => Set(ref _toggleName, value); }

    public string ActionsName { get => _actionsName; private set => Set(ref _actionsName, value); }

    /// <summary><c>win.remote.copied</c> shows beside the actions for a moment after <i>Copy SSH command</i>.</summary>
    public bool ShowsCopied { get => _copied; set => Set(ref _copied, value); }

    /// <summary>Name, destination and the status line with its notes, for screen readers.</summary>
    public string AccessibleName { get => _accessibleName; private set => Set(ref _accessibleName, value); }

    public void Refresh(RemoteHost host, string name, RemoteHostStatus status, int sessions, DateTimeOffset now, Localizer loc)
    {
        Host = host;
        Name = name;
        Destination = host.Destination;
        ShowsDestination = !string.Equals(name, host.Destination, StringComparison.Ordinal);
        Dot = RemoteDots.Of(status.Status);
        StatusText = RemoteStatusText.Sentence(status, host.Destination, sessions, now, loc);
        StatusTooltip = RemoteStatusText.Tooltip(status, now, loc);
        ClockNote = RemoteStatusText.ClockNote(status, loc);
        HistoryNote = RemoteStatusText.HistoryNote(status, loc);
        CanRetry = RemoteDots.CanRetry(status.Status);
        IsCountingDown = status.Status.IsRetrying && status.RetryAt is not null;
        ToggleName = loc.Format("remote.enabled", name);
        ActionsName = $"{loc["remote.actions"]}, {name}";
        _syncing = true;
        try
        {
            IsEnabled = host.Enabled;
        }
        finally
        {
            _syncing = false;
        }

        var parts = new List<string> { name };
        if (ShowsDestination)
        {
            parts.Add(host.Destination);
        }

        parts.Add(StatusText);
        if (ClockNote is { } clock)
        {
            parts.Add(clock);
        }

        if (HistoryNote is { } history)
        {
            parts.Add(history);
        }

        AccessibleName = string.Join(loc.UsesLatinScript ? ", " : "，", parts);
    }
}
