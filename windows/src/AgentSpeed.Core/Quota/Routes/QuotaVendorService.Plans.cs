using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Vendors;

namespace AgentSpeed.Core.Quota.Routes;

/// <summary>A sign-in file's size and time (or that the key comes from the environment): a change is a new credential.</summary>
internal readonly record struct CredentialStamp(string? Path, long Length, DateTime Modified, bool Environment)
{
    public static CredentialStamp Of(string? path)
    {
        if (path is null)
        {
            return new CredentialStamp(null, -1, default, false);
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new CredentialStamp(path, info.Length, info.LastWriteTimeUtc, false) : new CredentialStamp(path, -1, default, false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new CredentialStamp(path, -1, default, false);
        }
    }
}

public sealed partial class QuotaVendorService
{
    /// <summary>Decides whether the route asks this round and reads its credential only then; null when nothing goes out.</summary>
    private FetchPlan? Plan(QuotaRoute route, QuotaVendorRound round)
    {
        var state = State(route);
        var generation = Generation(route);
        var ua = _environment.UserAgent;
        switch (route)
        {
            case QuotaRoute.GrokBuildLive:
                return Token(route, round, state, GrokExpiryMarginMs, () => QuotaCredentials.Grok(_environment.Home, _environment.Variables),
                    token => new SingleFetch(route, token.Fingerprint, QuotaRequests.GrokBilling(token.Value, ua), static (body, _) => GrokBilling.FromResponse(body))
                    {
                        Generation = generation,
                    });
            case QuotaRoute.Kimi:
                return Token(route, round, state, 0, () => QuotaCredentials.Kimi(_environment.Home, _environment.Variables),
                    token => new SingleFetch(route, token.Fingerprint, QuotaRequests.KimiUsages(token.Value, ua), static (body, _) => KimiUsage.Windows(body))
                    {
                        Generation = generation,
                    });
            case QuotaRoute.Gemini:
                return Token(route, round, state, 0, () => QuotaCredentials.Gemini(_environment.Home, _environment.Variables), token =>
                {
                    var known = state.GeminiProject is { } learned && learned.Credential == token.Fingerprint ? (true, learned.Project) : (false, null);
                    return new GeminiFetch(token.Fingerprint, token.Value, known, ua) { Generation = generation };
                });
            case QuotaRoute.Droid:
                return Token(route, round, state, 0,
                    () => QuotaCredentials.Droid(_environment.Home, _environment.Variables) is { } key ? new QuotaToken(key) : null,
                    token => new SingleFetch(route, token.Fingerprint, QuotaRequests.DroidLimits(token.Value, ua),
                        static (body, now) => DroidLimits.Windows(body, now)) { Generation = generation });
            case QuotaRoute.Zcode:
                return ZcodePlan(round, state, generation);
            case QuotaRoute.Antigravity:
                return AntigravityPlan(round, state, generation);
            case QuotaRoute.Qoder or QuotaRoute.WorkBuddy:
                return BrowserPlan(route, round, state, generation);
            default:
                return null;
        }
    }

    /// <summary>
    /// A route with the agent's own sign-in file: nothing is read while the network is off or nothing asks; then the file
    /// is read when the cadence allows a request, or at once when the file changed (a new sign-in may ask at once).
    /// </summary>
    private FetchPlan? Token(QuotaRoute route, QuotaVendorRound round, RouteState state, long marginMs, Func<QuotaToken?> read, Func<QuotaToken, FetchPlan> plan)
    {
        state.Host = route.Host(round.ZaiRegion);
        if (!round.NetworkEnabled)
        {
            state.Status = QuotaRouteStatus.Of(QuotaStatusKind.NetworkOff);
            return null;
        }

        if (!Asks(route, round))
        {
            NotRunning(state);
            return null;
        }

        Resume(state);
        var stamp = StampOf(route);
        if (state.Stamp == stamp)
        {
            if (!state.HasCredential)
            {
                state.Status = QuotaRouteStatus.Of(QuotaStatusKind.NoCredential);
                return null;
            }

            if (state.ExpiresAtMs is { } expires && round.NowMs >= expires - marginMs)
            {
                state.Status = state.Settled = QuotaRouteStatus.Of(QuotaStatusKind.Expired);
                state.SignedIn = false;
                return null;
            }

            if (state.Cadence.LastOutcome == QuotaCadence.Outcome.Denied)
            {
                state.Status = QuotaRouteStatus.Denied(state.Host);
                return null;
            }

            if (state.Cadence.Waits(round.NowMs))
            {
                return null;
            }
        }

        state.Stamp = stamp;
        var token = read();
        if (token is null || !QuotaCredentials.IsHeaderSafe(token.Value))
        {
            state.HasCredential = false;
            state.ExpiresAtMs = null;
            state.Status = state.Settled = QuotaRouteStatus.Of(QuotaStatusKind.NoCredential);
            state.SignedIn = false;
            return null;
        }

        state.HasCredential = true;
        state.ExpiresAtMs = token.ExpiresAtMs;
        if (token.IsExpired(round.NowMs, marginMs))
        {
            state.Status = state.Settled = QuotaRouteStatus.Of(QuotaStatusKind.Expired);
            state.SignedIn = false;
            return null;
        }

        state.SignedIn = true;
        return Gate(route, round, token.Fingerprint, state) ? plan(token) : null;
    }

    private CredentialStamp StampOf(QuotaRoute route)
    {
        var home = _environment.Home;
        var variables = _environment.Variables;
        return route switch
        {
            QuotaRoute.Kimi => CredentialStamp.Of(QuotaCredentials.KimiFile(home, variables)),
            QuotaRoute.Gemini => CredentialStamp.Of(Path.Combine(QuotaCredentials.Folder("GEMINI_CLI_HOME", variables) ?? home, ".gemini", "oauth_creds.json")),
            QuotaRoute.GrokBuildLive => CredentialStamp.Of(Path.Combine(QuotaCredentials.Folder("GROK_HOME", variables) ?? Path.Combine(home, ".grok"), "auth.json")),
            QuotaRoute.Droid when variables("FACTORY_API_KEY") is { Length: > 0 } => new CredentialStamp(null, 0, default, true),
            _ => CredentialStamp.Of(Path.Combine(home, ".factory", ".env")),
        };
    }

    /// <summary>The secret store is read only when a request could go out; a new key or region forgets the route first.</summary>
    private FetchPlan? ZcodePlan(QuotaVendorRound round, RouteState state, int generation)
    {
        if (!round.NetworkEnabled || !Asks(QuotaRoute.Zcode, round))
        {
            Gate(QuotaRoute.Zcode, round, state.Account ?? QuotaFingerprint.Of(string.Empty), state);
            return null;
        }

        Resume(state);
        if (state.Cadence.LastOutcome == QuotaCadence.Outcome.Denied || state.Cadence.Waits(round.NowMs))
        {
            return null;
        }

        var key = _environment.ZcodeKey();
        if (key is null || !QuotaCredentials.IsHeaderSafe(key))
        {
            // No key saved: the key field itself says what to do.
            state.Status = null;
            state.SignedIn = false;
            return null;
        }

        state.SignedIn = true;
        var fingerprint = QuotaFingerprint.Of(key + "|" + round.ZaiRegion.Key());
        return Gate(QuotaRoute.Zcode, round, fingerprint, state)
            ? new SingleFetch(QuotaRoute.Zcode, fingerprint, QuotaRequests.ZaiLimits(key, round.ZaiRegion, _environment.UserAgent),
                static (body, now) => ZaiLimits.Windows(body, now)) { Generation = generation }
            : null;
    }

    /// <summary>
    /// The servers are looked for only when a request could go out: the agent runs (or the dashboard is open), the
    /// network is on and the last request is old enough.
    /// </summary>
    private FetchPlan? AntigravityPlan(QuotaVendorRound round, RouteState state, int generation)
    {
        if (!round.NetworkEnabled)
        {
            state.Status = QuotaRouteStatus.Of(QuotaStatusKind.NetworkOff);
            return null;
        }

        if (!round.DashboardOpen && !round.Running.Contains(SourceKind.Antigravity))
        {
            _antigravityRuns = false;
            state.Status = QuotaRouteStatus.Of(QuotaStatusKind.NotRunning);
            return null;
        }

        Resume(state);
        if (state.Cadence.Waits(round.NowMs) || _environment.Antigravity is not { } locator)
        {
            return null;
        }

        var servers = locator.Servers().ToList();
        _antigravityRuns = servers.Count > 0;
        _forgottenTokens.IntersectWith(servers.Select(s => s.Pid));
        if (servers.Count == 0)
        {
            state.Status = QuotaRouteStatus.Of(QuotaStatusKind.NotRunning);
            state.SignedIn = false;
            return null;
        }

        servers = [.. servers.Select(s => _forgottenTokens.Contains(s.Pid) ? s.WithoutTokens() : s)];
        var identity = string.Join(';', servers.Select(s => s.Pid + ":" + string.Join(',', s.Endpoints.Select(e => e.Scheme + e.Port + e.Token))));
        var fingerprint = QuotaFingerprint.Of(identity);
        return Gate(QuotaRoute.Antigravity, round, fingerprint, state)
            ? new AntigravityFetch(fingerprint, servers, _environment.UserAgent) { Generation = generation }
            : null;
    }

    /// <summary>
    /// The browser is read only when a request is due: every 5 minutes, 15 after a failure or a refusal (a refused
    /// session is asked again only once its cookie changed).
    /// </summary>
    private FetchPlan? BrowserPlan(QuotaRoute route, QuotaVendorRound round, RouteState state, int generation)
    {
        if (!round.NetworkEnabled)
        {
            state.Status = QuotaRouteStatus.Of(QuotaStatusKind.NetworkOff);
            return null;
        }

        if (!Asks(route, round))
        {
            NotRunning(state);
            return null;
        }

        Resume(state);
        var wait = state.Cadence.LastOutcome == QuotaCadence.Outcome.Success ? QuotaCadence.IntervalMs : QuotaCadence.FailureWaitMs;
        if (state.CheckedMs is { } checkedMs && round.NowMs - checkedMs < wait || _environment.Browser is not { } reader)
        {
            return null;
        }

        state.CheckedMs = round.NowMs;
        var refused = state.Cadence.LastOutcome == QuotaCadence.Outcome.Denied ? state.Cadence.Credential : null;
        return new BrowserFetch(route, state.Account ?? QuotaFingerprint.Of(string.Empty), reader, refused, _environment.UserAgent) { Generation = generation };
    }
}
