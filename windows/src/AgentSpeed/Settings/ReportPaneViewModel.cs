using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using AgentSpeed.Controls;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Reporting;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Reporting;
using AgentSpeed.Services;

namespace AgentSpeed.Settings;

/// <summary>One switch of <c>report.sections</c>.</summary>
internal sealed class ReportSectionRow(ReportSection section, string titleKey, Action<ReportSection, bool> set) : ObservableObject
{
    private bool _isOn;
    private bool _isEnabled;
    private string? _caption;
    private string _title = string.Empty;
    private bool _syncing;

    public ReportSection Section { get; } = section;

    public string TitleKey { get; } = titleKey;

    public string Title { get => _title; set => Set(ref _title, value); }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (Set(ref _isOn, value) && !_syncing)
            {
                set(Section, value);
            }
        }
    }

    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }

    /// <summary><c>report.section.notAccepted</c>, <c>report.section.trafficOff</c>, <c>report.section.models.perfScore</c> or <c>report.section.models.thinking</c>.</summary>
    public string? Caption { get => _caption; set => Set(ref _caption, value); }

    public void Sync(bool on, bool enabled, string? caption)
    {
        _syncing = true;
        IsOn = on;
        _syncing = false;
        IsEnabled = enabled;
        Caption = caption;
    }
}

/// <summary>One row of <c>report.others</c>: another service this device registered with.</summary>
internal sealed record ReportOtherRow(string Origin, string Host, string Username, ICommand Forget);

/// <summary>
/// Settings → API (DESIGN 11): the master switch, the service and its card, the username (always shown) and the API key,
/// what is sent, the status line, this device and the other registered services. Everything applies at once; opening the
/// pane never touches the network.
/// </summary>
internal sealed class ReportPaneViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan UrlDebounce = TimeSpan.FromMilliseconds(500);

    private readonly ReportController _report;
    private readonly SettingsService _settings;
    private readonly Func<string, string, bool> _confirmForget;
    private readonly Action _openPreview;
    private readonly DispatcherTimer _urlTimer;
    private Localizer _loc;
    private string _customUrlText;
    private ReportUrlProblem? _urlProblem;
    private bool _urlLoopback;
    private string _usernameText;
    private bool _usernameEditing;
    private bool _apiKeyRejected;
    private ReportProfileWindow? _profileWindow;
    internal ReportProfileWindow? DeviceManagerWindow => _profileWindow;
    private bool _visible;

    /// <param name="confirmForget">Asks <c>report.forget.confirm</c> (heading, body); true to forget.</param>
    /// <param name="openPreview">Opens the preview window.</param>
    public ReportPaneViewModel(ReportController report, SettingsService settings, Localizer localizer, Dispatcher dispatcher,
        Func<string, string, bool> confirmForget, Action openPreview)
    {
        _report = report;
        _settings = settings;
        _loc = localizer;
        _confirmForget = confirmForget;
        _openPreview = openPreview;
        _customUrlText = report.Preferences.CustomUrl;
        _usernameText = report.FieldUsername;
        Profile = new ReportProfileViewModel(report, localizer);
        _urlTimer = new DispatcherTimer(UrlDebounce, DispatcherPriority.Background, (_, _) => ValidateUrl(), dispatcher);
        _urlTimer.Stop();
        Sections =
        [
            new ReportSectionRow(ReportSection.Sources, "report.section.sources", SetSection),
            new ReportSectionRow(ReportSection.Models, "report.section.models", SetSection),
            new ReportSectionRow(ReportSection.Cost, "report.section.cost", SetSection),
            new ReportSectionRow(ReportSection.Traffic, "report.section.traffic", SetSection),
        ];
        RetryCommand = new RelayCommand(() => _report.Retry(), () => NetworkEnabled);
        StreamRetryCommand = new RelayCommand(() => _report.RetryStream(), () => NetworkEnabled);
        ReviewCommand = new RelayCommand(() =>
        {
            if (_report.Status.Kind == ReportStatusKind.Review)
            {
                _report.ReviewInvitation();
            }
            else
            {
                _report.BeginConsent(review: true);
            }
        }, () => NetworkEnabled);
        TermsCommand = new RelayCommand(() => ReportLinks.Open(_report.Discovery?.Terms));
        PrivacyCommand = new RelayCommand(() => ReportLinks.Open(_report.Discovery?.Privacy));
        LeaderboardCommand = new RelayCommand(() => ReportLinks.Open(_report.Discovery?.Leaderboard));
        RandomCommand = new RelayCommand(() =>
        {
            UsernameText = ReportUsername.Random();
            CommitUsername();
        });
        TransferCommand = new RelayCommand(() => _report.TransferUsername());
        ClearApiKeyCommand = new RelayCommand(() => _report.ClearApiKey());
        GetApiKeyCommand = new RelayCommand(() => ReportLinks.Open(_report.Discovery?.ApiKeyUrl));
        PreviewCommand = new RelayCommand(_openPreview);
        PauseHourCommand = new RelayCommand(() => _report.Pause(TimeSpan.FromHours(1)));
        PauseIndefiniteCommand = new RelayCommand(() => _report.Pause(null));
        ResumeCommand = new RelayCommand(() => _report.Resume());
        ProfileCommand = new RelayCommand(() => ReportLinks.Open(_report.Identity?.ProfileUrl));
        ManageDevicesCommand = new RelayCommand(OpenDeviceManager, () => Profile.CanIdentity);
        ForgetCommand = new RelayCommand(() => Forget(null), () => NetworkEnabled && _report.ForgetState != ReportForgetKind.Working);
        ForgetLocalCommand = new RelayCommand(() => _report.ForgetLocally());
        ResetCommand = new RelayCommand(() => _report.ResetDevice(), () => NetworkEnabled);
        _report.Changed += (_, _) => Refresh();
        _settings.Changed += (_, e) =>
        {
            if (e.Previous.NetworkEnabled != e.Current.NetworkEnabled || e.Previous.Traffic.Enabled != e.Current.Traffic.Enabled)
            {
                Refresh();
            }
        };
        Refresh();
    }

    public ReportController Report => _report;
    public ReportProfileViewModel Profile { get; }
    public bool ShowsInlineProfile => _report.Identity?.IsRegistered == true && _report.Discovery?.Extensions.HasProfile == true;
    public bool ShowsLegacyUsername => !ShowsInlineProfile;

    public bool NetworkEnabled => _report.NetworkEnabled;

    // 1. Master

    public bool Enabled
    {
        get => _report.Preferences.Enabled || _report.Consent is { IsReview: false };
        set
        {
            _report.SetEnabled(value);
            OnPropertyChanged();
        }
    }

    public string EnabledHint => NetworkEnabled ? _loc["report.enabled.hint"] : _loc["settings.network.offHint"];

    public bool ForgetDone => _report.ForgetState == ReportForgetKind.Done;

    // 2. Service

    /// <summary>0: the preset, 2: a custom URL (1 is the separator).</summary>
    public int ServiceIndex
    {
        get => _report.Preferences.Preset == ReportPreset.Custom ? 2 : 0;
        set
        {
            // Only a real choice switches services (and may fetch the new one's document, REPORTING 3.4).
            if (value == ServiceIndex || value is not (0 or 2))
            {
                return;
            }

            if (value == 0)
            {
                _report.SelectPreset(ReportPreset.CodexRadar);
            }
            else if (value == 2)
            {
                if (ReportServiceUrl.Parse(_customUrlText) is { } url)
                {
                    _report.CommitCustomUrl(url.Text);
                }
                else
                {
                    _settings.Update(s => s with { Report = s.Report with { Preset = ReportPreset.Custom } });
                }
            }

            OnPropertyChanged(string.Empty);
        }
    }

    public string PresetName => ReportPreferences.CodexRadarName;

    public bool IsCustom => _report.Preferences.Preset == ReportPreset.Custom;

    public string CustomUrlText
    {
        get => _customUrlText;
        set
        {
            if (Set(ref _customUrlText, value))
            {
                _urlTimer.Stop();
                _urlTimer.Start();
            }
        }
    }

    public string? UrlMessage => _urlProblem switch
    {
        ReportUrlProblem.NotHttps => _loc["report.url.https"],
        ReportUrlProblem.Invalid => _loc["report.url.invalid"],
        _ => null,
    };

    public bool UrlLoopback => _urlProblem is null && _urlLoopback;

    /// <summary>Return or leaving the field: the URL is validated and, when valid, committed (the only fetch it causes).</summary>
    public void CommitUrl()
    {
        ValidateUrl();
        if (_urlProblem is null && ReportServiceUrl.Parse(_customUrlText) is { } url)
        {
            _report.CommitCustomUrl(url.Text);
            _customUrlText = url.Text;
            OnPropertyChanged(nameof(CustomUrlText));
        }
    }

    public bool HasCard => _report.SelectedService is not null && _report.Discovery is not null;

    public bool HasSummary => _report.SelectedService is not null && _report.Discovery is null;

    public string? CardName => _report.Discovery?.Name ?? (_report.Preferences.Preset == ReportPreset.CodexRadar ? PresetName : null);

    public bool HasCardName => CardName is not null;

    public string CardHost => _report.DestinationHost is { } host ? _loc.Format("report.service.host", host) : string.Empty;

    /// <summary>The service URL's host when the API lives elsewhere: both are named (REPORTING 3.3).</summary>
    public string? CardServiceHost => _report.Discovery is { } d && _report.SelectedService is { } s && d.Api.Host != s.Host ? s.Host : null;

    public string? CardDescription => _report.Discovery?.Summary;

    public string? CardRetention => _report.Discovery?.RetentionDays is { } days ? _loc.Format("report.service.retention", days) : null;

    public bool HasLeaderboard => _report.Discovery?.Leaderboard is not null;

    public bool IsChecking => _report.ServiceState.Kind == ReportServiceStateKind.Checking;

    /// <summary>The failed fetch's sentence; while reporting is being set up the status line carries it instead.</summary>
    public string? ServiceIssueText => _report.ServiceIssue is { } issue && !_report.IsSettingUp ? _loc[ReportStatus.IssueKey(issue)] : null;

    public bool ServiceIssueIsPermanent => _report.ServiceIssue is ReportServiceIssue.NotSupported or ReportServiceIssue.Invalid;

    public bool NeedsReview => _report.NeedsReview;

    // 2b. Transport

    public bool ShowsTransport => _report.SelectedService is not null;

    public bool TransportHttp
    {
        get => _report.Transport == ReportTransportKind.Http;
        set => SetTransport(ReportTransportKind.Http, value);
    }

    public bool TransportWebSocket
    {
        get => _report.Transport == ReportTransportKind.WebSocket;
        set => SetTransport(ReportTransportKind.WebSocket, value);
    }

    private ReportTransportNotes TransportNotes => ReportTransportNotes.For(_report.Transport, _report.Discovery);

    public string? TransportCaption => TransportNotes.CaptionKey is { } key ? _loc[key] : null;

    /// <summary>The secondary <c>report.status.wsUnsupported</c> in the caption's place: reports go over HTTP meanwhile, the choice stays.</summary>
    public string? TransportUnsupportedText => TransportNotes.Unsupported ? _loc["report.status.wsUnsupported"] : null;

    /// <summary>What the segments' help text reads: the caption, or the note that replaces it.</summary>
    public string? TransportHelpText => TransportCaption ?? TransportUnsupportedText;

    public string? TransportThrottledText => TransportNotes.ThrottledMs is { } interval ? _loc.Format("report.transport.throttled", interval) : null;

    private void SetTransport(ReportTransportKind transport, bool chosen)
    {
        if (chosen && transport != _report.Transport)
        {
            _report.SetTransport(transport);
        }
    }

    // 3. Username

    public string UsernameText
    {
        get => _usernameText;
        set
        {
            if (Set(ref _usernameText, value))
            {
                OnPropertyChanged(nameof(UsernameMessage));
                OnPropertyChanged(nameof(UsernameMessageIsError));
                OnPropertyChanged(nameof(HasUsernameMessage));
            }
        }
    }

    public bool UsernameEditing
    {
        get => _usernameEditing;
        set => _usernameEditing = value;
    }

    public string? UsernameMessage
    {
        get
        {
            if (Problem(_usernameText, _report.UsernameRules) is { } problem)
            {
                return problem;
            }

            return _report.RenameState switch
            {
                ReportRenameKind.Updated => _loc["report.username.updated"],
                ReportRenameKind.Failed when _report.RenameError is { } key => Message(key, _report.UsernameRules),
                _ => null,
            };
        }
    }

    public bool UsernameMessageIsError => _report.RenameState != ReportRenameKind.Updated || Problem(_usernameText, _report.UsernameRules) is not null;

    public bool HasUsernameMessage => UsernameMessage is not null;

    public bool ShowsTransfer => _report.IsSettingUp && _report.Problem == ReportProblem.UsernameTaken && _report.MaskedApiKey is not null
        && _report.Discovery?.ApiKeyMode is ReportApiKeyMode.Optional or ReportApiKeyMode.Required;

    public void CommitUsername()
    {
        if (ReportUsername.Normalized(_usernameText) == _report.FieldUsername || Problem(_usernameText, _report.UsernameRules) is not null)
        {
            return;
        }

        _report.CommitUsername(_usernameText);
    }

    public void RestoreUsername() => UsernameText = _report.FieldUsername;

    // 4. API key

    public bool ShowsApiKey => _report.Identity is not null && _report.Discovery?.ApiKeyMode is ReportApiKeyMode.Optional or ReportApiKeyMode.Required;

    public bool ApiKeyRequired => _report.Discovery?.ApiKeyMode == ReportApiKeyMode.Required;

    public string ApiKeyPlaceholder => ApiKeyRequired ? string.Empty : _loc["report.apiKey.optional"];

    public string? MaskedApiKey => _report.MaskedApiKey;

    public bool HasSavedApiKey => _report.MaskedApiKey is not null;

    public bool ApiKeyRejected { get => _apiKeyRejected; private set => Set(ref _apiKeyRejected, value); }

    public bool HasApiKeyUrl => _report.Discovery?.ApiKeyUrl is not null;

    public bool SaveApiKey(string key)
    {
        ApiKeyRejected = !_report.SetApiKey(key);
        return !ApiKeyRejected;
    }

    // 5. What's sent

    public IReadOnlyList<ReportSectionRow> Sections { get; }

    // 6. Status

    public string StatusText => _report.Status.Text(_loc, DateTimeOffset.UtcNow);

    public TrafficHelperDot StatusDot => _report.Status.Dot;

    public bool StatusNeedsUser => _report.Status.NeedsUser;

    public string? StatusDetail => _report.Status.DetailKey is { } key ? _loc[key] : null;

    public string? RankText => _report.Rank is { } rank && _report.IsOn ? _loc.Format("report.status.rank", rank) : null;

    /// <summary><c>report.ws.stats</c> while WebSocket is effective and the session delivered or lost anything.</summary>
    public string? StreamStatsText => _report.UsesWebSocket && _report.StreamSession is { } session
        ? ReportStreamStats.Line(_loc, session.Counters, session.Latency)
        : null;

    /// <summary>Every counter (<c>report.ws.counters</c>), the stats line's tooltip.</summary>
    public string? StreamCountersText => StreamStatsText is not null && _report.StreamSession is { } session
        ? ReportStreamStats.Tooltip(_loc, session.Counters)
        : null;

    /// <summary><i>Try Again</i> after <c>report.status.wsMismatch</c> or <c>report.status.wsReplaced</c>.</summary>
    public bool ShowsStreamRetry => _report.Status is { Kind: ReportStatusKind.Stream, Stream: var kind } && ReportStreamStatus.OffersRetry(kind);

    public bool ShowsPause => _report.IsOn && !_report.IsPaused;

    public bool ShowsResume => _report.Preferences.Enabled && _report.IsPaused;

    public bool ShowsStatusRetry => _report.IsSettingUp && _report.Status.Kind is ReportStatusKind.Service or ReportStatusKind.RegisterFailed;

    public bool ShowsStatusReview => _report.Status.Kind is ReportStatusKind.Review or ReportStatusKind.Changed;

    // 7. This device

    public bool ShowsDevice => _report.Identity?.IsRegistered == true;

    public string DeviceDetail => _report.Identity is { Username: { } name } && _report.DestinationHost is { } host
        ? _loc.Format("report.device.detail", Isolated(name), host)
        : string.Empty;

    public bool HasProfile => _report.Identity?.ProfileUrl is not null;

    public string FingerprintText => _report.Fingerprint ?? string.Empty;

    public bool ForgetWorking => _report.ForgetState == ReportForgetKind.Working;

    public string? ForgetFailedText => _report.ForgetState == ReportForgetKind.Failed && _report.ForgetFailure is { } failure
        ? _loc.Format("report.forget.failed", _loc[ReportFailures.Key(failure)])
        : null;

    public bool ShowsForgetLocal => ForgetFailedText is not null;

    public bool ShowsReset => _report.Problem == ReportProblem.KeyMismatch && _report.Identity is not null;

    // 8. Other services

    public ObservableCollection<ReportOtherRow> Others { get; } = [];

    public bool HasOthers => Others.Count > 0;

    public ICommand RetryCommand { get; }

    public ICommand StreamRetryCommand { get; }

    public ICommand ReviewCommand { get; }

    public ICommand TermsCommand { get; }

    public ICommand PrivacyCommand { get; }

    public ICommand LeaderboardCommand { get; }

    public ICommand RandomCommand { get; }

    public ICommand TransferCommand { get; }

    public ICommand ClearApiKeyCommand { get; }

    public ICommand GetApiKeyCommand { get; }

    public ICommand PreviewCommand { get; }

    public ICommand PauseHourCommand { get; }

    public ICommand PauseIndefiniteCommand { get; }

    public ICommand ResumeCommand { get; }

    public ICommand ProfileCommand { get; }

    public ICommand ManageDevicesCommand { get; }

    public string? ManageProfileHint => _report.CanManageProfile ? null : _loc["profile.unavailable"];

    internal void OpenDeviceManager()
    {
        if (_profileWindow is { } open) { open.Activate(); return; }
        _profileWindow = new ReportProfileWindow(Profile) { Owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive) };
        _profileWindow.Closed += (_, _) => _profileWindow = null;
        _profileWindow.Show();
    }

    public ICommand ForgetCommand { get; }

    public ICommand ForgetLocalCommand { get; }

    public ICommand ResetCommand { get; }

    public void SetLocalizer(Localizer localizer)
    {
        _loc = localizer;
        Profile.SetLocalizer(localizer);
        Refresh();
    }

    /// <summary>Once a second while the pane shows: the status line's "seconds ago" and resume times.</summary>
    public void Tick()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusDot));
        OnPropertyChanged(nameof(StatusNeedsUser));
        OnPropertyChanged(nameof(StreamStatsText));
        OnPropertyChanged(nameof(StreamCountersText));
    }

    public void Opened()
    {
        _visible = true;
        if (ShowsInlineProfile) Profile.Activate();
    }

    public void Closed()
    {
        _visible = false;
        _profileWindow?.Close();
        Profile.Deactivate();
        _report.PaneClosed();
    }

    public void Dispose() { Closed(); Profile.Dispose(); }

    private void Refresh()
    {
        if (_visible && ShowsInlineProfile) Profile.Activate();
        else Profile.Deactivate();
        if (!_usernameEditing)
        {
            _usernameText = _report.FieldUsername;
        }

        if (!IsCustom || string.IsNullOrEmpty(_customUrlText))
        {
            _customUrlText = _report.Preferences.CustomUrl;
        }

        var discovery = _report.Discovery;
        var trafficOn = _settings.Current.Traffic.Enabled;
        foreach (var row in Sections)
        {
            row.Title = _loc[row.TitleKey];
            var accepted = discovery is null || discovery.Sections.Contains(row.Section);
            var trafficOff = row.Section == ReportSection.Traffic && !trafficOn;
            row.Sync(accepted && _report.Preferences.IsSectionOn(row.Section), accepted && !trafficOff,
                ReportSectionCaption.KeyOf(row.Section, discovery, trafficOn) is { } caption ? _loc[caption] : null);
        }

        var others = _report.OtherIdentities;
        if (!others.Select(o => (o.Id, o.Username)).SequenceEqual(Others.Select(o => (o.Origin, (string?)o.Username))))
        {
            Others.Clear();
            foreach (var other in others)
            {
                Others.Add(new ReportOtherRow(other.Id, other.Service.Host, other.Username ?? string.Empty,
                    new RelayCommand(() => Forget(other.Id), () => NetworkEnabled)));
            }
        }

        OnPropertyChanged(string.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetSection(ReportSection section, bool on) => _report.SetSection(section, on);

    private void ValidateUrl()
    {
        _urlTimer.Stop();
        if (string.IsNullOrWhiteSpace(_customUrlText))
        {
            _urlProblem = null;
            _urlLoopback = false;
        }
        else if (ReportServiceUrl.TryParse(_customUrlText, out var url, out var problem))
        {
            _urlProblem = null;
            _urlLoopback = url!.IsLoopbackHttp;
        }
        else
        {
            _urlProblem = problem;
            _urlLoopback = false;
        }

        OnPropertyChanged(nameof(UrlMessage));
        OnPropertyChanged(nameof(UrlLoopback));
    }

    private void Forget(string? origin)
    {
        var name = _report.Preferences.Identities.FirstOrDefault(i => i.Id == (origin ?? _report.Identity?.Id)) is { } target
            ? target.Discovery?.Name ?? target.Service.Host
            : _report.ServiceName ?? string.Empty;
        if (_confirmForget(_loc.Format("report.forget.confirm", name), _loc["report.forget.detail"]))
        {
            _report.ForgetDevice(origin);
        }
    }

    /// <summary>The localized message for a username, or null when it passes the rules.</summary>
    private string? Problem(string text, ReportUsernameRules rules)
    {
        var name = ReportUsername.Normalized(text);
        return ReportUsername.Validate(name, rules) is { } problem ? Message(ReportController.UsernameKey(problem, name, rules), rules) : null;
    }

    private string Message(string key, ReportUsernameRules rules) =>
        key == "report.username.invalid.length" ? _loc.Format(key, rules.MinLength, rules.MaxLength) : _loc[key];

    /// <summary>A username isolated with U+2068 … U+2069 so right-to-left names cannot reorder the text around them.</summary>
    public static string Isolated(string name) => "\u2068" + name + "\u2069";
}
