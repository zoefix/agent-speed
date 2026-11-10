using System.Collections.Immutable;
using AgentSpeed.Core.Quota.Cookies;
using AgentSpeed.Core.Quota.Local;
using AgentSpeed.Core.Quota.Network;

namespace AgentSpeed.Core.Quota.Routes;

/// <summary>
/// The phase 2 readers (QUOTAS 9): local files when they change (checked every 60 s), the loopback language server, and
/// the vendor requests with the agents' own sign-ins, the user's ZCode key and the browser's session, each under the
/// rules of 9.1 and NETWORK 9. A route that is not in the round (its source disabled or its switch off) reads nothing and
/// forgets its state. Credentials are read only when a request could go out and live only inside it. Thread-safe; one
/// round at a time.
/// </summary>
public sealed partial class QuotaVendorService
{
    public const long LocalIntervalMs = 60_000;

    /// <summary>A local route counts as signed in while its snapshot is this fresh (QUOTAS 9.1).</summary>
    public const long LocalSignedInMs = 24 * 3_600_000L;

    /// <summary>Grok's token is not used within a minute of its expiry (QUOTAS 9.2).</summary>
    public const long GrokExpiryMarginMs = 60_000;

    private sealed class RouteState
    {
        public QuotaCadence Cadence { get; } = new();

        /// <summary>The credential the snapshot belongs to.</summary>
        public QuotaFingerprint? Account { get; set; }

        public QuotaSnapshot? Snapshot { get; set; }

        public QuotaRouteStatus? Status { get; set; }

        /// <summary>The status of the last request or read, shown again once the network is back or the agent runs.</summary>
        public QuotaRouteStatus? Settled { get; set; }

        public bool SignedIn { get; set; }

        public string? Host { get; set; }

        /// <summary>Browser routes: when the session was last read (only when a request is due).</summary>
        public long? CheckedMs { get; set; }

        /// <summary>Gemini: the project of the current token, learned once per token.</summary>
        public (QuotaFingerprint Credential, string? Project)? GeminiProject { get; set; }

        /// <summary>Token routes: the sign-in file's size and time when it was last read; a change reads it again at once.</summary>
        public CredentialStamp? Stamp { get; set; }

        public bool HasCredential { get; set; }

        /// <summary>The last read token's own expiry (not the token).</summary>
        public long? ExpiresAtMs { get; set; }

        public QuotaRouteReading Reading => new(Snapshot, Status, SignedIn, Host);
    }

    private readonly QuotaVendorEnvironment _environment;
    private readonly Lock _gate = new();
    private readonly Dictionary<QuotaRoute, RouteState> _states = [];
    private readonly Dictionary<QuotaRoute, int> _generations = [];
    private GrokLogReader? _grokLog;
    private DevinPlanReader? _devin;
    private readonly HashSet<int> _forgottenTokens = [];
    private long? _localCheckedMs;
    private bool _antigravityRuns;
    private bool _inRound;

    public QuotaVendorService(QuotaVendorEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    /// <summary>Failures, with host and status only (NETWORK 9).</summary>
    public Action<string>? Log { get; init; }

    public QuotaVendorReadings Readings
    {
        get
        {
            lock (_gate)
            {
                return new QuotaVendorReadings(_states.ToImmutableDictionary(p => p.Key, p => p.Value.Reading), _antigravityRuns);
            }
        }
    }

    /// <summary>One round: routes that are off forget their state; the others read, ask when due, and report.</summary>
    public async Task<QuotaVendorReadings> RefreshAsync(QuotaVendorRound round, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(round);
        List<FetchPlan> plans;
        lock (_gate)
        {
            if (_inRound)
            {
                return Readings;
            }

            _inRound = true;
            try
            {
                foreach (var route in QuotaRoutes.All.Where(r => !round.Routes.Contains(r)))
                {
                    _states.Remove(route);
                }

                // Turning the browser routes off and on again tries the browsers whose key failed again.
                if (!round.Routes.Any(r => r.ReadsBrowser()))
                {
                    _environment.Browser?.ResetRefusals();
                }

                ReadLocal(round);
                plans = [];
                foreach (var route in QuotaRoutes.All.Where(r => round.Routes.Contains(r) && !r.IsLocal()))
                {
                    if (Plan(route, round) is { } plan)
                    {
                        plans.Add(plan);
                    }
                }
            }
            catch
            {
                _inRound = false;
                throw;
            }
        }

        try
        {
            if (plans.Count > 0)
            {
                var results = await Task.WhenAll(plans.Select(p => p.RunAsync(_environment.Transport, _environment.Now, cancellationToken))).ConfigureAwait(false);
                lock (_gate)
                {
                    foreach (var result in results)
                    {
                        Apply(result, round);
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _inRound = false;
            }
        }

        return Readings;
    }

    /// <summary>Forgets a route's state: its credential changed in the app (a new ZCode key or region); a request in flight is dropped.</summary>
    public void Forget(QuotaRoute route)
    {
        lock (_gate)
        {
            _states.Remove(route);
            _generations[route] = _generations.GetValueOrDefault(route) + 1;
        }
    }

    private RouteState State(QuotaRoute route)
    {
        if (!_states.TryGetValue(route, out var state))
        {
            state = new RouteState();
            _states[route] = state;
        }

        return state;
    }

    private int Generation(QuotaRoute route) => _generations.GetValueOrDefault(route);

    // Local files (QUOTAS 9.2 log, 9.3).
    private void ReadLocal(QuotaVendorRound round)
    {
        var due = round.ReadsLocalNow || _localCheckedMs is not { } last || round.NowMs - last >= LocalIntervalMs;
        if (!due)
        {
            RefreshLocalSignIn(round.NowMs);
            return;
        }

        _localCheckedMs = round.NowMs;
        if (round.Routes.Contains(QuotaRoute.GrokBuild))
        {
            // The log's folder is looked up only once its switch is on.
            _grokLog ??= new GrokLogReader(_environment.GrokLogFile ?? GrokLogReader.DefaultFile(_environment.Home, _environment.Variables));
            _grokLog.Refresh();
            UpdateLocal(QuotaRoute.GrokBuild, _grokLog.Snapshot, round.NowMs);
        }

        if (round.Routes.Contains(QuotaRoute.Devin))
        {
            _devin ??= new DevinPlanReader(_environment.DevinFolders ?? DevinPlanReader.DefaultFolders(_environment.AppData));
            _devin.Refresh(_environment.TimeZone());
            UpdateLocal(QuotaRoute.Devin, _devin.Snapshot, round.NowMs);
        }
    }

    private void UpdateLocal(QuotaRoute route, QuotaSnapshot? snapshot, long nowMs)
    {
        var state = State(route);
        state.Snapshot = snapshot;
        state.Status = snapshot is null ? QuotaRouteStatus.Of(QuotaStatusKind.NoCredential) : QuotaRouteStatus.Ok(snapshot.ObservedMs);
        state.SignedIn = snapshot is not null && nowMs - snapshot.ObservedMs <= LocalSignedInMs;
    }

    private void RefreshLocalSignIn(long nowMs)
    {
        foreach (var route in new[] { QuotaRoute.GrokBuild, QuotaRoute.Devin })
        {
            if (_states.TryGetValue(route, out var state) && state.Snapshot is { } snapshot)
            {
                state.SignedIn = nowMs - snapshot.ObservedMs <= LocalSignedInMs;
            }
        }
    }

    /// <summary>The agent runs, or the dashboard is open (or an Antigravity server was seen).</summary>
    private bool Asks(QuotaRoute route, QuotaVendorRound round) =>
        round.DashboardOpen || route.Agents().Any(round.Running.Contains) || (route == QuotaRoute.Antigravity && _antigravityRuns);

    /// <summary>Whether a request may go out now for <paramref name="credential"/>; otherwise the status says why not.</summary>
    private bool Gate(QuotaRoute route, QuotaVendorRound round, QuotaFingerprint credential, RouteState state, string? site = null)
    {
        if (state.Account is { } account && account != credential)
        {
            state.Snapshot = null;
            state.Status = null;
        }

        state.Account = credential;
        state.Host = route.Host(round.ZaiRegion, site);
        if (!round.NetworkEnabled)
        {
            state.Status = QuotaRouteStatus.Of(QuotaStatusKind.NetworkOff);
            return false;
        }

        if (state.Cadence.IsDenied(credential))
        {
            state.Status = QuotaRouteStatus.Denied(state.Host);
            return false;
        }

        if (!Asks(route, round))
        {
            NotRunning(state);
            return false;
        }

        return state.Cadence.IsDue(round.NowMs, credential);
    }

    /// <summary>A pause ends: the line says again how the last request went.</summary>
    private static void Resume(RouteState state)
    {
        if (state.Status is { Kind: QuotaStatusKind.NetworkOff or QuotaStatusKind.NotRunning })
        {
            state.Status = state.Settled;
        }
    }

    private static void NotRunning(RouteState state)
    {
        if (state.Status is null || state.Status.Value.Kind == QuotaStatusKind.NetworkOff)
        {
            state.Status = QuotaRouteStatus.Of(QuotaStatusKind.NotRunning);
        }
    }

    /// <summary>A browser route's session as found: none, the refused one again, or a session (another sign-in drops the old account's numbers).</summary>
    private static void ApplyBrowser(FetchResult result, QuotaVendorRound round, RouteState state)
    {
        switch (result.Skip)
        {
            case FetchSkip.NoSession:
                state.Status = QuotaRouteStatus.Of(result.Miss switch
                {
                    BrowserSessionMiss.Locked => QuotaStatusKind.BrowserLocked,
                    BrowserSessionMiss.Protected => QuotaStatusKind.BrowserProtected,
                    _ => QuotaStatusKind.NoBrowser,
                });
                state.SignedIn = false;
                state.Settled = state.Status;
                state.Cadence.Record(QuotaCadence.Outcome.Failure, round.NowMs, state.Cadence.Credential ?? result.Credential);
                break;
            case FetchSkip.Refused:
                state.Status = QuotaRouteStatus.Denied(result.Route.Host(round.ZaiRegion, result.Site));
                state.Settled = state.Status;
                state.SignedIn = false;
                break;
            default:
                if (state.Account is { } account && account != result.Credential)
                {
                    state.Snapshot = null;
                }

                state.Account = result.Credential;
                state.Host = result.Route.Host(round.ZaiRegion, result.Site);
                state.SignedIn = result.Outcome != QuotaCadence.Outcome.Denied;
                break;
        }
    }

    private void Apply(FetchResult result, QuotaVendorRound round)
    {
        if (result.Generation != Generation(result.Route) || !round.Routes.Contains(result.Route))
        {
            return;
        }

        var state = State(result.Route);
        if (result.Route.ReadsBrowser())
        {
            ApplyBrowser(result, round, state);
            if (result.Skip != FetchSkip.None)
            {
                return;
            }
        }
        else if (state.Account is { } account && account != result.Credential)
        {
            return;
        }

        state.Cadence.Record(result.Outcome, round.NowMs, result.Credential);
        if (result.GeminiProject.Learned)
        {
            state.GeminiProject = (result.Credential, result.GeminiProject.Project);
        }

        _forgottenTokens.UnionWith(result.ForgetTokens);
        var host = state.Host ?? result.Route.Host(round.ZaiRegion, result.Site);
        switch (result.Outcome)
        {
            case QuotaCadence.Outcome.Success:
                var observed = result.ObservedMs ?? round.NowMs;
                state.Snapshot = new QuotaSnapshot(result.Route.Source(), observed, result.Windows);
                state.Status = QuotaRouteStatus.Ok(observed);
                if (result.Route == QuotaRoute.Antigravity)
                {
                    state.SignedIn = true;
                }

                break;
            case QuotaCadence.Outcome.Denied:
                state.Status = QuotaRouteStatus.Denied(host);
                if (result.Route == QuotaRoute.Antigravity || result.Route.ReadsBrowser())
                {
                    state.SignedIn = false;
                }

                break;
            default:
                state.Status = QuotaRouteStatus.Of(QuotaStatusKind.Failed);
                if (result.Route == QuotaRoute.Antigravity)
                {
                    state.SignedIn = false;
                }

                break;
        }

        state.Settled = state.Status;
        if (result.Outcome != QuotaCadence.Outcome.Success)
        {
            Log?.Invoke($"request failed: {host} {(result.StatusCode is { } code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")}");
        }
    }
}
