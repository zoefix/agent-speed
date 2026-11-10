using System.Windows.Threading;
using AgentSpeed.Controls;
using AgentSpeed.Core.Quota;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Settings.Sources;

/// <summary>The quota switches of Settings → Data Sources (DESIGN 5).</summary>
internal enum QuotaSwitch
{
    Codex,
    ClaudeDesktop,
    ClaudeCli,
}

/// <summary>What the quota switches do; the app implements it (the bridge asks first and may fail).</summary>
internal interface IQuotaSettingsActions
{
    void Set(QuotaSwitch quota, bool on);

    /// <summary>Asks, then turns the Claude CLI bridge on; true when it is on.</summary>
    bool EnableClaudeCli();

    void DisableClaudeCli();

    void ReconnectClaudeCli();
}

/// <summary>
/// One quota switch with its hint as the caption and, for the Claude CLI bridge, its status line: waiting, connected
/// with the quota file's time, replaced (warning, with Reconnect), or why it could not be turned on (warning).
/// </summary>
internal sealed class QuotaToggleViewModel : ObservableObject
{
    private readonly Func<IQuotaSettingsActions?> _actions;
    private Localizer _loc;
    private bool _isOn;
    private string _status = string.Empty;
    private bool _warning;
    private bool _reconnect;
    private bool _ok;

    public QuotaToggleViewModel(QuotaSwitch quota, Localizer loc, Func<IQuotaSettingsActions?> actions)
    {
        Switch = quota;
        _loc = loc;
        _actions = actions;
        ReconnectCommand = new RelayCommand(() => _actions()?.ReconnectClaudeCli());
    }

    public QuotaSwitch Switch { get; }

    public string Title => _loc[Switch switch
    {
        QuotaSwitch.Codex => "settings.quota.codex",
        QuotaSwitch.ClaudeDesktop => "settings.quota.claudeDesktop",
        _ => "settings.quota.claudeCli",
    }];

    public string Hint => _loc[Switch switch
    {
        QuotaSwitch.Codex => "settings.quota.codex.hint",
        QuotaSwitch.ClaudeDesktop => "settings.quota.claudeDesktop.hint",
        _ => "settings.quota.claudeCli.hint",
    }];

    public string ReconnectText => _loc["settings.quota.cli.reconnect"];

    public RelayCommand ReconnectCommand { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (value == _isOn)
            {
                return;
            }

            if (Switch != QuotaSwitch.ClaudeCli)
            {
                _isOn = value;
                OnPropertyChanged();
                _actions()?.Set(Switch, value);
                return;
            }

            if (!value)
            {
                _isOn = false;
                OnPropertyChanged();
                _actions()?.DisableClaudeCli();
                return;
            }

            // Turning the bridge on asks first: the switch stays off until the dialog confirms and the install succeeds.
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                var on = _actions()?.EnableClaudeCli() ?? false;
                _isOn = on;
                OnPropertyChanged(nameof(IsOn));
            });
        }
    }

    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool HasStatus => _status.Length > 0;

    /// <summary>The status reads in the warning style (replaced, unreadable, failed).</summary>
    public bool IsWarning { get => _warning; private set => Set(ref _warning, value); }

    public bool ShowsReconnect { get => _reconnect; private set => Set(ref _reconnect, value); }

    /// <summary>Connected with a quota file: the status line leads with a check.</summary>
    public bool IsConnected { get => _ok; private set => Set(ref _ok, value); }

    /// <summary>Takes the switch's stored value and, for the bridge, its status; <paramref name="failure"/> wins while set.</summary>
    public void Update(QuotaSettings settings, QuotaState state, string? failure, string settingsPath)
    {
        var on = Switch switch
        {
            QuotaSwitch.Codex => settings.Codex,
            QuotaSwitch.ClaudeDesktop => settings.ClaudeDesktop,
            _ => settings.ClaudeCli,
        };
        if (on != _isOn)
        {
            _isOn = on;
            OnPropertyChanged(nameof(IsOn));
        }

        if (Switch != QuotaSwitch.ClaudeCli)
        {
            return;
        }

        string status;
        var warning = false;
        var reconnect = false;
        if (failure is { Length: > 0 })
        {
            status = failure;
            warning = true;
        }
        else if (!on)
        {
            status = string.Empty;
        }
        else
        {
            switch (state.CliStatus)
            {
                case ClaudeCliBridgeStatus.Ok when state.CliFileTime is { } time:
                    status = _loc.Format("settings.quota.cli.ok",
                        QuotaText.When(time.ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _loc));
                    break;
                case ClaudeCliBridgeStatus.Replaced:
                    status = _loc["settings.quota.cli.replaced"];
                    warning = true;
                    reconnect = true;
                    break;
                case ClaudeCliBridgeStatus.Unreadable:
                    status = _loc.Format("settings.quota.cli.unreadable", settingsPath);
                    warning = true;
                    break;
                default:
                    status = _loc["settings.quota.cli.waiting"];
                    break;
            }
        }

        Status = status;
        IsWarning = warning;
        IsConnected = !warning && on && state.CliStatus == ClaudeCliBridgeStatus.Ok && state.CliFileTime is not null && failure is not { Length: > 0 };
        ShowsReconnect = reconnect;
        OnPropertyChanged(nameof(HasStatus));
    }

    public void Relocalize(Localizer loc)
    {
        _loc = loc;
        OnPropertyChanged(string.Empty);
    }
}
