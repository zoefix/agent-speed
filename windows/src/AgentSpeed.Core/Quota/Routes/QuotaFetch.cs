using System.Text.Json;
using AgentSpeed.Core.Quota.Cookies;
using AgentSpeed.Core.Quota.Local;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Vendors;

namespace AgentSpeed.Core.Quota.Routes;

/// <summary>A route found no session, or only the one refused before; nothing was asked.</summary>
internal enum FetchSkip
{
    None,
    NoSession,
    Refused,
}

/// <summary>What one plan brought back; it never holds the credential, only its fingerprint.</summary>
internal sealed class FetchResult(QuotaRoute route, QuotaFingerprint credential, QuotaCadence.Outcome outcome)
{
    public QuotaRoute Route { get; } = route;

    public QuotaFingerprint Credential { get; set; } = credential;

    public QuotaCadence.Outcome Outcome { get; set; } = outcome;

    public FetchSkip Skip { get; init; }

    public BrowserSessionMiss Miss { get; init; }

    /// <summary>The site a browser session came from.</summary>
    public string? Site { get; set; }

    public IReadOnlyList<QuotaWindow> Windows { get; set; } = [];

    public long? ObservedMs { get; set; }

    public int? StatusCode { get; set; }

    /// <summary>Gemini: the project learned for this token (<c>Learned</c> false when none was asked).</summary>
    public (bool Learned, string? Project) GeminiProject { get; set; }

    public HashSet<int> ForgetTokens { get; } = [];

    /// <summary>The service's generation of the route when the plan was made; a forgotten route drops late results.</summary>
    public int Generation { get; set; }
}

/// <summary>One request a round decided on, with its credential inside; it lives only until the request is done.</summary>
internal abstract class FetchPlan(QuotaRoute route, QuotaFingerprint credential)
{
    public QuotaRoute Route { get; } = route;

    public QuotaFingerprint Credential { get; } = credential;

    public int Generation { get; init; }

    public async Task<FetchResult> RunAsync(IQuotaHttpTransport transport, Func<long> now, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(transport, now, cancellationToken).ConfigureAwait(false);
        result.Generation = Generation;
        return result;
    }

    protected abstract Task<FetchResult> ExecuteAsync(IQuotaHttpTransport transport, Func<long> now, CancellationToken cancellationToken);

    /// <summary>One answer: 2xx with a JSON object is a success, 401/403 a refusal, anything else (a redirect, the cap, a timeout) a failure.</summary>
    internal sealed record Answer(QuotaCadence.Outcome Outcome, int? Status, JsonDocument? Body, long? DateMs, long ArrivedMs);

    internal static async Task<Answer> AskAsync(QuotaHttpRequest request, IQuotaHttpTransport transport, Func<long> now, CancellationToken cancellationToken)
    {
        try
        {
            var response = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var arrived = now();
            switch (response.Status)
            {
                case >= 200 and <= 299:
                    var body = VendorValue.ParseObject(response.Body);
                    return body is null
                        ? new Answer(QuotaCadence.Outcome.Failure, response.Status, null, null, arrived)
                        : new Answer(QuotaCadence.Outcome.Success, response.Status, body, response.DateMs, arrived);
                case 401 or 403:
                    return new Answer(QuotaCadence.Outcome.Denied, response.Status, null, null, arrived);
                default:
                    return new Answer(QuotaCadence.Outcome.Failure, response.Status, null, null, arrived);
            }
        }
        catch (Exception e) when (e is QuotaHttpException or OperationCanceledException or HttpRequestException or IOException)
        {
            return new Answer(QuotaCadence.Outcome.Failure, null, null, null, now());
        }
    }
}

/// <summary>One request; <c>Parse</c> turns a 2xx JSON body into windows, null when it is not the expected answer.</summary>
internal sealed class SingleFetch(QuotaRoute route, QuotaFingerprint credential, QuotaHttpRequest request,
    Func<JsonElement, long, IReadOnlyList<QuotaWindow>?> parse) : FetchPlan(route, credential)
{
    protected override async Task<FetchResult> ExecuteAsync(IQuotaHttpTransport transport, Func<long> now, CancellationToken cancellationToken)
    {
        var answer = await AskAsync(request, transport, now, cancellationToken).ConfigureAwait(false);
        using var body = answer.Body;
        var result = new FetchResult(Route, Credential, answer.Outcome) { StatusCode = answer.Status };
        if (answer.Outcome == QuotaCadence.Outcome.Success)
        {
            if (body is not null && parse(body.RootElement, now()) is { } windows)
            {
                result.Windows = windows;
                result.ObservedMs = answer.DateMs ?? answer.ArrivedMs;
            }
            else
            {
                result.Outcome = QuotaCadence.Outcome.Failure;
            }
        }

        return result;
    }
}

/// <summary><c>loadCodeAssist</c> (when the token's project is not known yet), then <c>retrieveUserQuota</c> (QUOTAS 9.6).</summary>
internal sealed class GeminiFetch(QuotaFingerprint credential, string token, (bool Known, string? Project) known, string userAgent)
    : FetchPlan(QuotaRoute.Gemini, credential)
{
    protected override async Task<FetchResult> ExecuteAsync(IQuotaHttpTransport transport, Func<long> now, CancellationToken cancellationToken)
    {
        var project = known.Project;
        var learned = false;
        if (!known.Known)
        {
            var load = await AskAsync(QuotaRequests.GeminiLoad(token, userAgent), transport, now, cancellationToken).ConfigureAwait(false);
            using var loadBody = load.Body;
            if (load.Outcome != QuotaCadence.Outcome.Success || loadBody is null)
            {
                return new FetchResult(Route, Credential, load.Outcome == QuotaCadence.Outcome.Denied ? QuotaCadence.Outcome.Denied : QuotaCadence.Outcome.Failure)
                {
                    StatusCode = load.Status,
                };
            }

            project = GeminiQuota.Project(loadBody.RootElement);
            learned = true;
        }

        var answer = await AskAsync(QuotaRequests.GeminiQuota(token, project, userAgent), transport, now, cancellationToken).ConfigureAwait(false);
        using var body = answer.Body;
        var result = new FetchResult(Route, Credential, answer.Outcome) { StatusCode = answer.Status, GeminiProject = (learned, project) };
        if (answer.Outcome == QuotaCadence.Outcome.Success && body is not null)
        {
            result.Windows = GeminiQuota.Windows(body.RootElement);
            result.ObservedMs = answer.DateMs ?? answer.ArrivedMs;
        }
        else if (answer.Outcome == QuotaCadence.Outcome.Success)
        {
            result.Outcome = QuotaCadence.Outcome.Failure;
        }

        return result;
    }
}

/// <summary>The servers' addresses in order, until one answers (QUOTAS 9.4); a 404 falls back to <c>GetUserStatus</c>.</summary>
internal sealed class AntigravityFetch(QuotaFingerprint credential, IReadOnlyList<AntigravityServer> servers, string userAgent)
    : FetchPlan(QuotaRoute.Antigravity, credential)
{
    protected override async Task<FetchResult> ExecuteAsync(IQuotaHttpTransport transport, Func<long> now, CancellationToken cancellationToken)
    {
        var result = new FetchResult(Route, Credential, QuotaCadence.Outcome.Failure);
        var denied = false;
        foreach (var server in servers)
        {
            foreach (var endpoint in server.Endpoints)
            {
                if (QuotaRequests.Antigravity(AntigravityCall.Summary, endpoint.Scheme, endpoint.Port, endpoint.Token, userAgent) is not { } summary)
                {
                    continue;
                }

                var answer = await AskAsync(summary, transport, now, cancellationToken).ConfigureAwait(false);
                var call = AntigravityCall.Summary;
                if (answer.Status == 404
                    && QuotaRequests.Antigravity(AntigravityCall.Status, endpoint.Scheme, endpoint.Port, endpoint.Token, userAgent) is { } status)
                {
                    answer = await AskAsync(status, transport, now, cancellationToken).ConfigureAwait(false);
                    call = AntigravityCall.Status;
                }

                using var body = answer.Body;
                result.StatusCode = answer.Status;
                if (answer.Outcome == QuotaCadence.Outcome.Denied)
                {
                    denied = true;
                    if (endpoint.Token is not null)
                    {
                        result.ForgetTokens.Add(server.Pid);
                    }

                    continue;
                }

                if (answer.Outcome != QuotaCadence.Outcome.Success || body is null)
                {
                    continue;
                }

                result.Outcome = QuotaCadence.Outcome.Success;
                result.Windows = call == AntigravityCall.Summary ? AntigravitySummary.SummaryWindows(body.RootElement) : AntigravitySummary.StatusWindows(body.RootElement);
                result.ObservedMs = answer.DateMs ?? answer.ArrivedMs;
                return result;
            }
        }

        if (denied)
        {
            result.Outcome = QuotaCadence.Outcome.Denied;
        }

        return result;
    }
}

/// <summary>
/// The browser's session, read when the plan runs (off the round), then one request unless that very session was refused
/// before (QUOTAS 9.9, 9.10).
/// </summary>
internal sealed class BrowserFetch(QuotaRoute route, QuotaFingerprint credential, IBrowserSessionReader reader, QuotaFingerprint? refused, string userAgent)
    : FetchPlan(route, credential)
{
    protected override async Task<FetchResult> ExecuteAsync(IQuotaHttpTransport transport, Func<long> now, CancellationToken cancellationToken)
    {
        var nowMs = now();
        var sites = Route.CookieSites();
        var (session, miss) = await Task.Run(() =>
        {
            var found = reader.Session(sites, nowMs, out var why);
            return (found, why);
        }, cancellationToken).ConfigureAwait(false);
        if (session is null || !QuotaCredentials.IsHeaderSafe(session.Cookie) || !QuotaCredentials.IsHeaderSafe(session.UserAgent))
        {
            return new FetchResult(Route, Credential, QuotaCadence.Outcome.Failure)
            {
                Skip = FetchSkip.NoSession,
                Miss = session is null ? miss : BrowserSessionMiss.NoSession,
            };
        }

        var fingerprint = session.Fingerprint;
        if (fingerprint == refused)
        {
            return new FetchResult(Route, fingerprint, QuotaCadence.Outcome.Denied) { Skip = FetchSkip.Refused, Site = session.Site };
        }

        QuotaHttpRequest? request;
        Func<JsonElement, long, IReadOnlyList<QuotaWindow>?> parse;
        if (Route == QuotaRoute.Qoder)
        {
            request = QuotaRequests.QoderCredits(session.Cookie, session.Site, userAgent);
            parse = static (body, _) => BrowserSessionCredits.QoderWindows(body);
        }
        else
        {
            request = QuotaRequests.WorkBuddySummary(session.Cookie, session.UserAgent);
            parse = static (body, _) => BrowserSessionCredits.WorkBuddyWindows(body);
        }

        if (request is null)
        {
            return new FetchResult(Route, fingerprint, QuotaCadence.Outcome.Failure) { Skip = FetchSkip.NoSession, Miss = BrowserSessionMiss.NoSession };
        }

        var result = await new SingleFetch(Route, fingerprint, request, parse).RunAsync(transport, now, cancellationToken).ConfigureAwait(false);
        result.Site = session.Site;
        return result;
    }
}
