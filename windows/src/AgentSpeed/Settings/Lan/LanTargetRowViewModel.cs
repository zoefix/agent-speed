using AgentSpeed.Controls;
using AgentSpeed.Core.Lan;
using AgentSpeed.Presentation.Lan;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Remote;

namespace AgentSpeed.Settings.Lan;

/// <summary>
/// One target of Settings → API → 區域網路 (DESIGN 14), laid out as a host row: dot, name, <c>UDP · address</c>, the status
/// line and the switch.
/// </summary>
internal sealed class LanTargetRowViewModel(string id, Action<string, bool> setEnabled) : ObservableObject
{
    private string _name = string.Empty;
    private string _address = string.Empty;
    private RemoteDotKind _dot;
    private string _statusText = string.Empty;
    private bool _enabled;
    private bool _syncing;
    private string _toggleName = string.Empty;
    private string _actionsName = string.Empty;
    private string _accessibleName = string.Empty;
    private bool _ticks;

    public string Id { get; } = id;

    public LanTarget? Target { get; private set; }

    public string Name { get => _name; private set => Set(ref _name, value); }

    /// <summary><c>UDP · 192.168.1.50:7247</c> or <c>HTTP · http://…</c>.</summary>
    public string Address { get => _address; private set => Set(ref _address, value); }

    public RemoteDotKind Dot { get => _dot; private set => Set(ref _dot, value); }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>The line changes every second (the time since the last send, a countdown).</summary>
    public bool Ticks { get => _ticks; private set => Set(ref _ticks, value); }

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

    /// <summary><c>lan.target.enabled</c> (送到「Desk」), the switch's accessible name.</summary>
    public string ToggleName { get => _toggleName; private set => Set(ref _toggleName, value); }

    public string ActionsName { get => _actionsName; private set => Set(ref _actionsName, value); }

    public string AccessibleName { get => _accessibleName; private set => Set(ref _accessibleName, value); }

    public void Refresh(LanTarget target, LanTargetStatus status, long nowMs, DateTimeOffset now, Localizer loc)
    {
        Target = target;
        Name = target.DisplayName;
        var kind = target.Kind == LanTargetKind.Udp ? "UDP" : "HTTP";
        // The address is already the title when there is no name (DESIGN 14).
        Address = string.IsNullOrEmpty(target.Name) ? kind : kind + " · " + target.Address;
        Dot = LanStatusText.TargetDot(status);
        StatusText = LanStatusText.Target(status, nowMs, now, loc);
        Ticks = LanStatusText.CountsDown(status) || status.Kind == LanTargetStatusKind.Ok;
        ToggleName = loc.Format("lan.target.enabled", Name);
        ActionsName = $"{loc["remote.actions"]}, {Name}";
        AccessibleName = string.Join(loc.UsesLatinScript ? ", " : "，", Name, Address, StatusText);
        _syncing = true;
        try
        {
            IsEnabled = target.Enabled;
        }
        finally
        {
            _syncing = false;
        }
    }
}

/// <summary>One of this computer's addresses with the server's port, and what its pull-down copies (DESIGN 14, Address).</summary>
internal sealed record LanAddressRow(string Text, string HttpUrl, string WebSocketUrl, string TcpAddress);
