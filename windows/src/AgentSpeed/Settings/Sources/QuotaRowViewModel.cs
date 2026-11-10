using AgentSpeed.Controls;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Settings.Sources;

/// <summary>What leads a quota row's status line (DESIGN 5): nothing, a check, a pause, no network, or the caution triangle.</summary>
internal enum QuotaStatusMark
{
    None,
    Ok,
    Paused,
    NetworkOff,
    Warning,
}

/// <summary>Everything a quota row reads to show itself: the settings, the quotas, the bridge's failure and the moment.</summary>
internal sealed record QuotaRowContext(AppSettings Settings, QuotaState State, string? CliFailure, string CliSettingsPath, long NowMs);

/// <summary>
/// One quota switch of a source group (DESIGN 5, 額度): its title, its hint and an optional quieter note as the caption, an
/// optional status line, and whether it can be switched (a disabled source dims its rows, QUOTAS 9.1). Phase 1 and phase 2
/// switches share the row's template.
/// </summary>
internal abstract class QuotaRowViewModel : ObservableObject
{
    private bool _isOn;
    private bool _canSwitch = true;
    private string _status = string.Empty;
    private QuotaStatusMark _mark;

    protected QuotaRowViewModel(Localizer loc, Func<IQuotaSettingsActions?> actions)
    {
        Loc = loc;
        Actions = actions;
    }

    protected Localizer Loc { get; private set; }

    protected Func<IQuotaSettingsActions?> Actions { get; }

    public abstract string Title { get; }

    public abstract string Hint { get; }

    public bool HasHint => Hint.Length > 0;

    /// <summary>The second caption, a step quieter than the hint (<c>settings.quota.unverified</c>).</summary>
    public virtual string Note => string.Empty;

    public bool HasNote => Note.Length > 0;

    /// <summary>A dependent switch, indented under the one it needs.</summary>
    public virtual bool IsIndented => false;

    /// <summary>The ZCode key and region, shown under the ZCode switch while it is on.</summary>
    public virtual ZcodeFieldsViewModel? Zcode => null;

    public bool ShowsZcodeFields => Zcode is not null && _isOn;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (value != _isOn)
            {
                Toggle(value);
            }
        }
    }

    /// <summary>The source is enabled (and any switch this one needs is on); otherwise the row is dimmed.</summary>
    public bool CanSwitch { get => _canSwitch; protected set => Set(ref _canSwitch, value); }

    public string Status
    {
        get => _status;
        protected set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => _status.Length > 0;

    public QuotaStatusMark StatusMark
    {
        get => _mark;
        protected set
        {
            if (Set(ref _mark, value))
            {
                OnPropertyChanged(nameof(IsWarning));
            }
        }
    }

    /// <summary>The status reads in the warning style: the caution triangle before secondary text.</summary>
    public bool IsWarning => _mark == QuotaStatusMark.Warning;

    /// <summary>The switch's accessible help: the hint, then the note.</summary>
    public string HelpText => HasNote ? Hint + " " + Note : Hint;

    // The Claude CLI bridge offers Reconnect inside its status line; the other rows never do.
    public virtual string ReconnectText => string.Empty;

    public virtual RelayCommand? ReconnectCommand => null;

    public virtual bool ShowsReconnect => false;

    public string ReconnectLink => ShowsReconnect ? ReconnectText : string.Empty;

    public string ReconnectGap => ShowsReconnect ? "   " : string.Empty;

    /// <summary>The user flipped the switch; the row decides whether it follows at once or asks first.</summary>
    protected abstract void Toggle(bool on);

    public abstract void Update(QuotaRowContext context);

    /// <summary>Shows the switch's value without acting on it.</summary>
    protected void ShowOn(bool on)
    {
        if (Set(ref _isOn, on, nameof(IsOn)))
        {
            OnPropertyChanged(nameof(ShowsZcodeFields));
        }
    }

    /// <summary>The switch returns to the shown value (a dialog was cancelled or the bridge failed).</summary>
    protected void RefreshOn() => OnPropertyChanged(nameof(IsOn));

    protected static QuotaStatusMark MarkOf(QuotaStatusGlyph glyph) => glyph switch
    {
        QuotaStatusGlyph.Ok => QuotaStatusMark.Ok,
        QuotaStatusGlyph.Paused => QuotaStatusMark.Paused,
        QuotaStatusGlyph.NetworkOff => QuotaStatusMark.NetworkOff,
        _ => QuotaStatusMark.Warning,
    };

    public virtual void Relocalize(Localizer loc)
    {
        Loc = loc;
        OnPropertyChanged(string.Empty);
    }
}
