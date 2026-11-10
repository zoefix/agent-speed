using System.Windows.Threading;
using AgentSpeed.Controls;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Quota;

namespace AgentSpeed.Settings.Sources;

/// <summary>
/// A phase 1 quota switch with its hint as the caption and, for the Claude CLI bridge, its status line: waiting, connected
/// with the quota file's time, replaced (warning, with Reconnect), or why it could not be turned on (warning). Codex and
/// Claude Desktop dim while their source is disabled; the bridge stays switchable so it can always be restored.
/// </summary>
internal sealed class QuotaToggleViewModel : QuotaRowViewModel
{
    private bool _reconnect;

    public QuotaToggleViewModel(QuotaSwitch quota, Localizer loc, Func<IQuotaSettingsActions?> actions)
        : base(loc, actions)
    {
        Switch = quota;
        ReconnectCommand = new RelayCommand(() => Actions()?.ReconnectClaudeCli());
    }

    public QuotaSwitch Switch { get; }

    public override string Title => Loc[Switch switch
    {
        QuotaSwitch.Codex => "settings.quota.codex",
        QuotaSwitch.ClaudeDesktop => "settings.quota.claudeDesktop",
        _ => "settings.quota.claudeCli",
    }];

    public override string Hint => Loc[Switch switch
    {
        QuotaSwitch.Codex => "settings.quota.codex.hint",
        QuotaSwitch.ClaudeDesktop => "settings.quota.claudeDesktop.hint",
        _ => "settings.quota.claudeCli.hint",
    }];

    public override string ReconnectText => Loc["settings.quota.cli.reconnect"];

    public override RelayCommand ReconnectCommand { get; }

    public override bool ShowsReconnect => _reconnect;

    protected override void Toggle(bool on)
    {
        if (Switch != QuotaSwitch.ClaudeCli)
        {
            ShowOn(on);
            Actions()?.Set(Switch, on);
            return;
        }

        if (!on)
        {
            ShowOn(false);
            Actions()?.DisableClaudeCli();
            return;
        }

        // Turning the bridge on asks first: the switch stays off until the dialog confirms and the install succeeds.
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            ShowOn(Actions()?.EnableClaudeCli() ?? false);
            RefreshOn();
        });
    }

    /// <summary>Takes the switch's stored value and, for the bridge, its status; the bridge's failure wins while set.</summary>
    public override void Update(QuotaRowContext context)
    {
        var settings = context.Settings;
        var on = Switch switch
        {
            QuotaSwitch.Codex => settings.Quota.Codex,
            QuotaSwitch.ClaudeDesktop => settings.Quota.ClaudeDesktop,
            _ => settings.Quota.ClaudeCli,
        };
        ShowOn(on);
        CanSwitch = Switch switch
        {
            QuotaSwitch.Codex => settings.IsSourceEnabled(SourceKind.Codex),
            QuotaSwitch.ClaudeDesktop => settings.IsSourceEnabled(SourceKind.ClaudeDesktop),
            _ => true,
        };

        if (Switch != QuotaSwitch.ClaudeCli)
        {
            return;
        }

        var state = context.State;
        string status;
        var mark = QuotaStatusMark.None;
        var reconnect = false;
        if (context.CliFailure is { Length: > 0 } failure)
        {
            status = failure;
            mark = QuotaStatusMark.Warning;
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
                    status = Loc.Format("settings.quota.cli.ok", QuotaText.When(time.ToUnixTimeMilliseconds(), context.NowMs, Loc));
                    mark = QuotaStatusMark.Ok;
                    break;
                case ClaudeCliBridgeStatus.Replaced:
                    status = Loc["settings.quota.cli.replaced"];
                    mark = QuotaStatusMark.Warning;
                    reconnect = true;
                    break;
                case ClaudeCliBridgeStatus.Unreadable:
                    status = Loc.Format("settings.quota.cli.unreadable", context.CliSettingsPath);
                    mark = QuotaStatusMark.Warning;
                    break;
                default:
                    status = Loc["settings.quota.cli.waiting"];
                    break;
            }
        }

        Status = status;
        StatusMark = mark;
        if (reconnect != _reconnect)
        {
            _reconnect = reconnect;
            OnPropertyChanged(nameof(ShowsReconnect));
            OnPropertyChanged(nameof(ReconnectLink));
            OnPropertyChanged(nameof(ReconnectGap));
        }
    }
}
