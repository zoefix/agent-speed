#if LINUX
using AgentSpeed.Linux.Platform;
#else
using System.Windows.Threading;
#endif
using AgentSpeed.Core;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Services;
using AgentSpeed.Traffic;

namespace AgentSpeed.Reporting;

internal enum ReportServiceStateKind
{
    Unknown,
    Checking,
    Ready,
    Failed,
    NotSupported,
    Invalid,
    Gone,
}

/// <summary>The selected service's discovery state (DESIGN 11, Service).</summary>
internal sealed record ReportServiceState(ReportServiceStateKind Kind, ReportDiscovery? Document = null, ReportFailure? Failure = null)
{
    public static ReportServiceState Unknown { get; } = new(ReportServiceStateKind.Unknown);

    public static ReportServiceState Checking { get; } = new(ReportServiceStateKind.Checking);
}

/// <summary>The consent sheet's form (DESIGN 11, Consent sheet). The device id exists only in memory until Start.</summary>
internal sealed record ReportConsentDraft(ReportServiceUrl Service, ReportDiscovery Discovery, string DeviceId)
{
    public string Username { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public IReadOnlySet<ReportSection> Sections { get; init; } = new HashSet<ReportSection>();

    /// <summary>The identity is registered; the user reviews changed details only.</summary>
    public bool IsReview { get; init; }

    public bool IsStarting { get; init; }

    /// <summary>Localization keys; never a status code.</summary>
    public string? UsernameError { get; init; }

    public string? ApiKeyError { get; init; }

    public string? Failure { get; init; }

    /// <summary><c>username_taken</c> with an API key set: offer <c>report.username.transfer</c>.</summary>
    public bool OffersTransfer { get; init; }

    /// <summary>Never prints the API key.</summary>
    public override string ToString() => $"ReportConsentDraft {{ Service = {Service}, IsReview = {IsReview} }}";
}

internal enum ReportRenameKind
{
    Idle,
    Working,
    Updated,
    Failed,
}

internal enum ReportForgetKind
{
    Idle,
    Working,
    Done,
    Failed,
}

/// <summary>
/// Live reporting (DESIGN 11, METRICS 11, REPORTING) on the UI thread: the user's settings and service identities, the
/// discovery document, the consent sheet, the report scheduler fed by every live engine tick, delivery, the account actions
/// (rename, API key, forget, reset, rotate) and the dashboard invitation. Nothing is requested before the user confirmed a
/// service — in the consent sheet or by accepting the invitation — and nothing at all while the network master switch is off.
/// </summary>
internal sealed partial class ReportController : IDisposable
{
    public static readonly TimeSpan RotationAge = TimeSpan.FromDays(365);
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);
    public static readonly TimeSpan ReRegisterInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan InviteDoneHold = TimeSpan.FromSeconds(2.4);
    public static readonly TimeSpan SetupRetryInterval = TimeSpan.FromHours(1);

    private readonly Dispatcher _ui;
    private readonly SettingsService _settings;
    private readonly SpeedHub _hub;
    private readonly TrafficService _traffic;
    private readonly PriceBook _priceBook;
    private readonly ReportKeyStore _keyStore;
    private readonly HttpReportTransport? _ownedTransport;
    private readonly ReportApi _api;
    private readonly CountingTransport _counting;
    private readonly string _previewRunId = ReportCanonical.NewUuid();
    private readonly string _previewDeviceId = ReportCanonical.NewUuid();
    private readonly string _fallbackUsername = ReportUsername.Random();
    private readonly List<ReportEvent> _pendingEvents = [];
    private readonly HashSet<DispatcherTimer> _timers = [];

    private IReportSigner? _signer;
    private IReportSigner? _pendingSigner;
    private IReportSigner? _draftSigner;
    private string? _apiKey;
    private long _clockCorrection;
    private int _strikes;
    private DateTimeOffset? _lastReRegister;
    private bool _renamedForMismatch;
    private bool _rotating;
    private bool _networkWasEnabled;
    private ReportSample _cachedSample = ReportSample.Empty;
    private SpeedSnapshot? _lastSnapshot;
    private bool _monitorRunning;
    private bool _active;
    private bool _consentWanted;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _pauseTimer;
    private DispatcherTimer? _setupTimer;
    private Action? _quitReply;
    private ReportWindow? _previewWindow;
    private long? _previewStartedAt;
    private int _previewOpenings;
    private ReportRun? _run;

    public ReportController(Dispatcher ui, SettingsService settings, SpeedHub hub, TrafficService traffic, PriceBook priceBook, string version,
        IReportTransport? transport = null, ReportKeyStore? keyStore = null)
    {
        _ui = ui;
        _settings = settings;
        _hub = hub;
        _traffic = traffic;
        _priceBook = priceBook;
        _keyStore = keyStore ?? ReportKeyStore.Standard();
        Client = OperatingSystem.IsLinux() ? ReportClientInfo.Linux(version) : ReportClientInfo.Windows(version);
        if (transport is null)
        {
            _ownedTransport = new HttpReportTransport();
            transport = _ownedTransport;
        }

        if (LoopbackOnly)
        {
            transport = new LoopbackOnlyTransport(transport);
        }

        _counting = new CountingTransport(transport);
        _api = new ReportApi(_counting, Client.UserAgent);
        _networkWasEnabled = settings.Current.NetworkEnabled;
    }

    /// <summary>Anything the UI shows changed (state, status, the stored preferences).</summary>
    public event EventHandler? Changed;

    public ReportClientInfo Client { get; }

    public ReportServiceState ServiceState { get; private set; } = ReportServiceState.Unknown;

    public ReportConsentDraft? Consent { get; private set; }

    public DateTimeOffset? LastDeliveryAt { get; private set; }

    public int? Rank { get; private set; }

    public ReportProblem? Problem { get; private set; }

    public ReportFailure? RetryReason { get; private set; }

    public DateTimeOffset? RateLimitedUntil { get; private set; }

    public bool Registering { get; private set; }

    /// <summary>Setting up after the invitation: the last registration attempt failed for a passing reason.</summary>
    public ReportFailure? RegistrationFailure { get; private set; }

    /// <summary>Setting up after the invitation: what the invitation did not cover, so the consent sheet must finish it.</summary>
    public ReportInviteGap? InviteGap { get; private set; }

    /// <summary>The invitation card shows <c>report.invite.done</c> for a moment after it was accepted.</summary>
    public bool InviteJustAccepted { get; private set; }

    public ReportMode Mode { get; private set; } = ReportMode.Quiet;

    public ReportRenameKind RenameState { get; private set; }

    /// <summary>A localization key for <see cref="ReportRenameKind.Failed"/>.</summary>
    public string? RenameError { get; private set; }

    public ReportForgetKind ForgetState { get; private set; }

    public ReportFailure? ForgetFailure { get; private set; }

    /// <summary>The key store's folder (the e2e harness checks that <i>Forget</i> removes the files).</summary>
    internal ReportKeyStore KeyStore => _keyStore;

    /// <summary>Discovery and API requests made so far (the e2e harness checks that nothing is requested before consent).</summary>
    internal int RequestCount => _counting.Count;

    /// <summary>A rotated key waits for the service's answer.</summary>
    internal bool HasPendingKey => _pendingSigner is not null;

    /// <summary>The e2e harness offers the invitation again after declining it.</summary>
    internal void ResetInvitation()
    {
        UpdatePreferences(p => p with { Invite = null });
        Notify();
    }

    public ReportPreferences Preferences => _settings.Current.Report;

    public bool NetworkEnabled => _settings.Current.NetworkEnabled;

    /// <summary>
    /// The preset's URL; the developer variable <c>AGENT_SPEED_REPORT_PRESET_URL</c> may point it at a loopback reference server
    /// (only <c>127.0.0.1</c>, <c>[::1]</c> or <c>localhost</c>), so the invitation's path can be tested without the real service.
    /// </summary>
    public static string PresetUrl => PresetOverride ?? ReportPreferences.CodexRadarUrl;

    /// <summary>The domain the preset covers (REPORTING 3.3); none while the preset is overridden for testing.</summary>
    public static string? PresetDomain => PresetOverride is null ? ReportPreferences.CodexRadarDomain : null;

    private static string? PresetOverride => PresetOverrideValue.Value;

    private static readonly Lazy<string?> PresetOverrideValue = new(() =>
        Environment.GetEnvironmentVariable("AGENT_SPEED_REPORT_PRESET_URL") is { Length: > 0 } url
        && ReportServiceUrl.Parse(url) is { } parsed && parsed.Host is "127.0.0.1" or "[::1]" or "localhost"
            ? parsed.Text
            : null);

    /// <summary>The selected service URL; null for an empty or invalid custom URL. Parsed once per change of the setting.</summary>
    public ReportServiceUrl? SelectedService
    {
        get
        {
            var text = Preferences.Preset == ReportPreset.CodexRadar ? PresetUrl : Preferences.CustomUrl;
            if (!string.Equals(text, _selectedText, StringComparison.Ordinal))
            {
                _selectedText = text;
                _selected = ReportServiceUrl.Parse(text);
            }

            return _selected;
        }
    }

    private string? _selectedText;
    private ReportServiceUrl? _selected;

    public ReportIdentity? Identity => Preferences.Identity(SelectedService);

    /// <summary>Identities of the services that are not selected (DESIGN 11, Other services).</summary>
    public IReadOnlyList<ReportIdentity> OtherIdentities
    {
        get
        {
            var selected = SelectedService?.Origin;
            return [.. Preferences.Identities.Where(i => i.Id != selected && i.IsRegistered)];
        }
    }

    /// <summary>The discovery document to show: the fresh one, else the stored copy.</summary>
    public ReportDiscovery? Discovery => ServiceState is { Kind: ReportServiceStateKind.Ready, Document: { } document } ? document : Identity?.Discovery;

    /// <summary>The service's display name: its own name, else the preset's.</summary>
    public string? ServiceName => Discovery?.Name ?? (Preferences.Preset == ReportPreset.CodexRadar ? ReportPreferences.CodexRadarName : null);

    /// <summary>The host that receives the reports: the API host, else the service host.</summary>
    public string? DestinationHost => Discovery?.Api.Host ?? SelectedService?.Host;

    /// <summary>Reporting is on and confirmed for the selected service (the menu, dashboard and About show it).</summary>
    public bool IsOn => Preferences.Enabled && Identity?.IsRegistered == true;

    public bool IsPaused => Preferences.IsPaused(DateTimeOffset.UtcNow);

    /// <summary>The pin no longer matches the service's details (REPORTING 3.3); computed once per change of the identity.</summary>
    public bool NeedsReview
    {
        get
        {
            var identity = Identity;
            if (!ReferenceEquals(identity, _reviewedIdentity))
            {
                _reviewedIdentity = identity;
                _needsReview = identity is { Pin: { } pin, Discovery: { } current } && pin.NeedsReview(current, identity.HasApiKey);
            }

            return _needsReview;
        }
    }

    private ReportIdentity? _reviewedIdentity;
    private bool _needsReview;

    /// <summary>The stored document is older than 7 days and could not be refreshed (REPORTING 3.4): reporting waits.</summary>
    public bool IsDiscoveryStale => Identity?.DiscoveryFetchedAt is { } fetched
        ? DateTimeOffset.UtcNow - fetched >= StaleAfter
        : Identity?.IsRegistered == true;

    /// <summary>Optional sections that are both listed by the service and switched on (REPORTING 6.1).</summary>
    public IReadOnlySet<ReportSection> EffectiveSections =>
        Discovery is { } discovery ? Preferences.Sections.Where(discovery.Sections.Contains).ToHashSet() : new HashSet<ReportSection>();

    /// <summary>The traffic section is actually reported (the Network Traffic pane's note).</summary>
    public bool ReportsTraffic => IsOn && EffectiveSections.Contains(ReportSection.Traffic);

    /// <summary>The failed fetch of the selected service, as the pane says it (DESIGN 11, Service).</summary>
    public ReportServiceIssue? ServiceIssue => ServiceState.Kind switch
    {
        ReportServiceStateKind.Failed => ReportStatus.IssueOf(ServiceState.Failure ?? ReportFailure.Unavailable),
        ReportServiceStateKind.NotSupported => ReportServiceIssue.NotSupported,
        ReportServiceStateKind.Invalid => ReportServiceIssue.Invalid,
        _ => null,
    };

    /// <summary>Reporting is on but this device is not registered yet: the invitation's setup, or a registration to redo.</summary>
    public bool IsSettingUp => Preferences.Enabled && Identity?.IsRegistered != true;

    /// <summary>The rules a username is checked against: the known document's, else the protocol grammar with the default lengths.</summary>
    public ReportUsernameRules UsernameRules => Discovery?.UsernameRules ?? ReportUsernameRules.Standard;

    /// <summary>The computer name fitted to <paramref name="rules"/> (DESIGN 11, Default username), else a random name.</summary>
    public string DefaultUsername(ReportUsernameRules rules) =>
        ReportComputerName.Current is { } name && ReportUsername.Fit(name, rules) is { } fitted ? fitted : _fallbackUsername;

    /// <summary>The name the next registration uses: the user's own, else the default.</summary>
    public string NextUsername(ReportUsernameRules rules) => Preferences.Username ?? DefaultUsername(rules);

    /// <summary>What the always-visible username field shows: the registered name, else the next registration's.</summary>
    public string FieldUsername => Identity?.Username ?? NextUsername(UsernameRules);

    /// <summary>The dashboard invitation is offered (DESIGN 11, Dashboard invitation).</summary>
    public bool InvitationOffered => ReportInvite.IsOffered(Preferences.Invite, Preferences.Enabled, NetworkEnabled,
        Preferences.Preset == ReportPreset.CodexRadar, Preferences.HasRegisteredIdentity);

    /// <summary>The dashboard footer's indicator: whenever reporting is on, also while it is being set up.</summary>
    public bool IndicatorShown => Preferences.Enabled && !InviteJustAccepted;

    public ReportStatus Status
    {
        get
        {
            if (Problem == ReportProblem.Gone)
            {
                return new ReportStatus(ReportStatusKind.Problem) { Problem = ReportProblem.Gone };
            }

            if (!Preferences.Enabled)
            {
                return new ReportStatus(ReportStatusKind.Off);
            }

            if (!NetworkEnabled)
            {
                return new ReportStatus(ReportStatusKind.NetworkOff);
            }

            if (Problem is { } problem)
            {
                return new ReportStatus(ReportStatusKind.Problem) { Problem = problem };
            }

            if (NeedsReview)
            {
                return new ReportStatus(ReportStatusKind.Changed);
            }

            if (Preferences.PausedUntil is { } until && until > DateTimeOffset.UtcNow)
            {
                return until == DateTimeOffset.MaxValue
                    ? new ReportStatus(ReportStatusKind.PausedIndefinitely)
                    : new ReportStatus(ReportStatusKind.Paused) { At = until };
            }

            if (Registering)
            {
                return new ReportStatus(ReportStatusKind.Registering);
            }

            if (Identity?.IsRegistered != true)
            {
                if (Consent is not null)
                {
                    return new ReportStatus(ReportStatusKind.Connecting);
                }

                if (InviteGap is not null)
                {
                    return new ReportStatus(ReportStatusKind.Review);
                }

                if (RegistrationFailure is { } failure)
                {
                    return new ReportStatus(ReportStatusKind.RegisterFailed) { Failure = failure };
                }

                if (ServiceIssue is { } issue)
                {
                    return new ReportStatus(ReportStatusKind.Service) { Issue = issue };
                }

                return ServiceState.Kind == ReportServiceStateKind.Gone
                    ? new ReportStatus(ReportStatusKind.Problem) { Problem = ReportProblem.Gone }
                    : new ReportStatus(ReportStatusKind.Connecting);
            }

            if (RateLimitedUntil is { } limited && limited > DateTimeOffset.UtcNow)
            {
                return new ReportStatus(ReportStatusKind.RateLimited) { At = limited };
            }

            if (RetryReason is { } reason)
            {
                return new ReportStatus(ReportStatusKind.Retrying) { Failure = reason };
            }

            if (IsDiscoveryStale)
            {
                return new ReportStatus(ReportStatusKind.Connecting);
            }

            return Mode switch
            {
                ReportMode.Active => LastDeliveryAt is { } last
                    ? new ReportStatus(ReportStatusKind.Reporting) { At = last }
                    : new ReportStatus(ReportStatusKind.Never),
                ReportMode.Idle => new ReportStatus(LastDeliveryAt is null ? ReportStatusKind.Never : ReportStatusKind.Idle),
                _ => new ReportStatus(LastDeliveryAt is null ? ReportStatusKind.Never : ReportStatusKind.Quiet),
            };
        }
    }

    /// <summary>The masked API key (<c>••••</c> and its last 4 characters), never the key itself.</summary>
    public string? MaskedApiKey => _apiKey is { Length: > 0 } key ? "••••" + key[Math.Max(0, key.Length - 4)..] : null;

    public string? Fingerprint => _signer?.PublicKey.Fingerprint;

    /// <summary>Starts following the live engine, the network switch and the settings.</summary>
    public void Activate()
    {
        if (_active)
        {
            return;
        }

        _active = true;
        LoadSecrets();
        _hub.LivePublished += OnLive;
        _settings.Changed += OnSettingsChanged;
        SchedulePauseEnd();
        RefreshDiscoveryIfDue(force: true);
        ContinueSetup();
    }

    /// <summary>At quit: one <c>leaving</c> report when it can be sent within a second (REPORTING 7.1), then <paramref name="reply"/>.</summary>
    public void PrepareToQuit(Action reply)
    {
        if (_run is not { } run || run.Scheduler.Mode == ReportMode.Quiet || !IsReady() || !NetworkEnabled)
        {
            reply();
            return;
        }

        _quitReply = reply;
        Post(ReportEvent.Stop);
        After(TimeSpan.FromSeconds(1), FinishQuit);
        if (_run?.Scheduler.IsBusy != true)
        {
            FinishQuit();
        }
    }

    public void Dispose()
    {
        _hub.LivePublished -= OnLive;
        _settings.Changed -= OnSettingsChanged;
        foreach (var timer in _timers.ToArray())
        {
            timer.Stop();
        }

        _timers.Clear();
        _ownedTransport?.Dispose();
        _active = false;
    }

    private void FinishQuit()
    {
        var reply = _quitReply;
        _quitReply = null;
        reply?.Invoke();
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        var enabled = e.Current.NetworkEnabled;
        if (enabled != _networkWasEnabled)
        {
            _networkWasEnabled = enabled;
            Post(enabled ? ReportEvent.NetworkOn : ReportEvent.NetworkOff);
            if (enabled)
            {
                RefreshDiscoveryIfDue(force: true);
                ContinueSetup();
            }
        }

        if (!ReferenceEquals(e.Previous.Report, e.Current.Report))
        {
            Notify();
        }
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    private void UpdatePreferences(Func<ReportPreferences, ReportPreferences> change) =>
        _settings.Update(s => s with { Report = change(s.Report) });

    private void UpdateIdentity(string origin, Func<ReportIdentity, ReportIdentity> change) =>
        UpdatePreferences(p => p.Updating(origin, change));

    /// <summary>Loads the selected identity's keys (nothing is read before a key exists).</summary>
    private void LoadSecrets()
    {
        if (Identity is not { } identity)
        {
            _signer = null;
            _pendingSigner = null;
            _apiKey = null;
            return;
        }

        _signer = _keyStore.LoadKey(identity.DeviceId);
        _pendingSigner = identity.PendingKey ? _keyStore.LoadKey(identity.DeviceId, pending: true) : null;
        _apiKey = identity.HasApiKey ? _keyStore.LoadApiKey(identity.DeviceId) : null;
    }

    /// <summary>A one-shot timer on the UI thread.</summary>
    private DispatcherTimer After(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = delay < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _timers.Remove(timer);
            action();
        };
        _timers.Add(timer);
        timer.Start();
        return timer;
    }

    private void Cancel(ref DispatcherTimer? timer)
    {
        if (timer is not null)
        {
            timer.Stop();
            _timers.Remove(timer);
            timer = null;
        }
    }

    /// <summary>Monotonic milliseconds that keep counting while the computer sleeps, so the gap rule sees a sleep (METRICS 11.5).</summary>
    private static long MonotonicMs => Environment.TickCount64;

    private static long UnixMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// The developer variable <c>AGENT_SPEED_REPORT_LOOPBACK_ONLY=1</c> (and the e2e harness) refuses every request to a host
    /// other than <c>127.0.0.1</c>, <c>[::1]</c> or <c>localhost</c>, so a test session can never reach a real service.
    /// </summary>
    internal static bool LoopbackOnly => Environment.GetEnvironmentVariable("AGENT_SPEED_REPORT_LOOPBACK_ONLY") == "1";

    private sealed class LoopbackOnlyTransport(IReportTransport inner) : IReportTransport
    {
        public Task<ReportHttpResponse> GetAsync(Uri url, string? etag, string userAgent, int maxBytes, TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            IsLoopback(url) ? inner.GetAsync(url, etag, userAgent, maxBytes, timeout, cancellationToken) : Refuse(url);

        public Task<ReportHttpResponse> SendAsync(ReportRequest request, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            IsLoopback(request.Url) ? inner.SendAsync(request, maxBytes, timeout, cancellationToken) : Refuse(request.Url);

        private static bool IsLoopback(Uri url) => url.IsLoopback;

        private static Task<ReportHttpResponse> Refuse(Uri url)
        {
            App.AppLog.Warn($"report: loopback-only session refused a request to {url.Host}\n{Environment.StackTrace}");
            throw new ReportTransportException(ReportTransportErrorKind.Offline, "loopback only");
        }
    }

    /// <summary>Counts the requests the controller makes (discovery and API), passing them on unchanged.</summary>
    private sealed class CountingTransport(IReportTransport inner) : IReportTransport
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task<ReportHttpResponse> GetAsync(Uri url, string? etag, string userAgent, int maxBytes, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return inner.GetAsync(url, etag, userAgent, maxBytes, timeout, cancellationToken);
        }

        public Task<ReportHttpResponse> SendAsync(ReportRequest request, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return inner.SendAsync(request, maxBytes, timeout, cancellationToken);
        }
    }

    /// <summary>One run (METRICS 11.5, Run): its id, the scheduler, the open window, the title tracker and the unsent bodies.</summary>
    private sealed class ReportRun(string origin, ReportScheduler scheduler, ReportWindow window)
    {
        public string Id { get; } = ReportCanonical.NewUuid();

        public string Origin { get; } = origin;

        public ReportScheduler Scheduler { get; } = scheduler;

        public ReportWindow Window { get; } = window;

        public ReportTitleTracker Title { get; } = new();

        public Dictionary<int, byte[]> Bodies { get; } = [];

        /// <summary>Tickets whose next attempt is signed with the pending key.</summary>
        public HashSet<int> SignWithPending { get; } = [];
    }
}
