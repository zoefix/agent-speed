using System.Security.Cryptography;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Reporting;

namespace AgentSpeed.Reporting;

/// <summary>
/// The WebSocket session's connection (METRICS 11.9): the signed upgrade, the events of the current connection (those of an
/// ended one are ignored by their generation), signed <c>report</c> messages, and the reactions of REPORTING 8.3 the stream
/// scheduler asks for.
/// </summary>
internal sealed partial class ReportController
{
    private void Connect(ReportStreamRun run)
    {
        if (Identity is not { Discovery: { } document } identity || StreamSigner(run) is not { } key)
        {
            return;
        }

        ReportRequest handshake;
        try
        {
            handshake = ReportWebSocketProtocol.Handshake(new ReportRequestBuilder(Client.UserAgent, identity.DeviceId, _apiKey), document.Api, key,
                UnixMs / 1000 + _clockCorrection);
        }
        catch (CryptographicException)
        {
            Problem = ReportProblem.KeyMismatch;
            return;
        }

        // The network master switch and a loopback-only session stop every connection (NETWORK 1); the deadline then fails it.
        if (!NetworkEnabled || (LoopbackOnly && !handshake.Url.IsLoopback))
        {
            ReportLog.Write("websocket connection refused here (network off or loopback only)");
            return;
        }

        run.Socket = _streamTransport.Open(handshake, run.Scheduler.Settings, e => _ui.BeginInvoke(() => OnStreamEvent(e)));
    }

    private IReportSigner? StreamSigner(ReportStreamRun run) => run.SigningWithPending ? _pendingSigner ?? _signer : _signer;

    private void OnStreamEvent(ReportStreamEvent e)
    {
        if (_stream is not { } run || run.Socket?.Generation != e.Generation)
        {
            return;
        }

        var now = MonotonicMs;
        var scheduler = run.Scheduler;
        IReadOnlyList<ReportStreamAction> actions;
        switch (e)
        {
            case ReportStreamEvent.Opened:
                actions = scheduler.Opened(now);
                break;
            case ReportStreamEvent.Message { Value: ReportServiceMessage.Ready ready }:
                actions = scheduler.Ready(now, ready.Limits);
                break;
            case ReportStreamEvent.Message { Value: ReportServiceMessage.Ack ack }:
                var matches = ack.RunId == run.Id && ack.Seq <= int.MaxValue && run.Reports.TryGetValue((int)ack.Seq, out var report)
                    && report.Sha256 == ack.BodySha256;
                actions = scheduler.Ack(now, matches ? (int)ack.Seq : 0, ack.Status, matches, ack.Rank);
                break;
            case ReportStreamEvent.Message { Value: ReportServiceMessage.Error error }:
                ReportLog.Write($"websocket error {error.HttpStatus} {error.Code}{(error.Fatal ? " fatal" : string.Empty)}");
                run.ServerTime = error.ServerTime;
                int? seq = error.RunId == run.Id && error.Seq is { } number && number <= int.MaxValue ? (int)number : null;
                actions = scheduler.Error(now, seq, error.Code, error.HttpStatus, error.RetryAfterSeconds, error.Fatal);
                break;
            case ReportStreamEvent.Rejected rejected:
                ReportLog.Write($"websocket upgrade refused {rejected.Rejection.Status} {rejected.Rejection.Code ?? "-"}");
                run.Socket = null;
                run.ServerTime = rejected.Rejection.ServerTime;
                actions = scheduler.Rejected(now, rejected.Rejection.Status, rejected.Rejection.Code, rejected.Rejection.RetryAfterSeconds);
                break;
            case ReportStreamEvent.Closed closed:
                ReportLog.Write($"websocket closed {closed.Code}");
                run.Socket = null;
                actions = scheduler.Closed(now, closed.Code);
                break;
            default:
                return;
        }

        ApplyStream(run, actions, LastSample);
        AfterStream();
    }

    private void Build(ReportStreamRun run, ReportStreamTicket ticket, ReportSample sample)
    {
        var content = run.Window.Take(sample) with { Perf = TakePerf(sample) };
        run.LastBuilt = ReportStreamValues.Of(sample, ScoreExtension);
        if (Identity is not { Username: { } username, Discovery: { } document } identity)
        {
            return;
        }

        var envelope = new ReportEnvelope(Client, identity.DeviceId, username, run.Id, ticket.Seq, UnixMs + _clockCorrection * 1000, ticket.WindowMs,
            ticket.Activity, run.Title.Level);
        var options = Options(document, Preferences.Sections) with
        {
            Stream = new ReportStreamBody(ticket.ObservationAgeMs, run.Scheduler.Limits.MaxMessageBytes),
        };
        var (_, data) = ReportBody.Fitted(envelope, content, sample, options);
        run.Reports[ticket.Seq] = new StreamReport(data, ReportHash.Sha256Hex(data), ReportBody.Fits(data, options));
    }

    /// <summary>Signs the built body with a fresh nonce and writes it as one <c>report</c> message.</summary>
    private void Write(ReportStreamRun run, int seq, ReportSample sample)
    {
        if (!run.Reports.TryGetValue(seq, out var report) || Identity is not { Discovery: { } document } identity)
        {
            return;
        }

        if (!report.Fits)
        {
            ApplyStream(run, run.Scheduler.Unsendable(seq), sample);
            return;
        }

        if (StreamSigner(run) is not { } key)
        {
            return;
        }

        ReportRequest request;
        try
        {
            request = new ReportRequestBuilder(Client.UserAgent, identity.DeviceId, _apiKey).Make("POST", document.Api.Endpoint(ReportEndpoint.Reports),
                report.Body, key, UnixMs / 1000 + _clockCorrection);
        }
        catch (CryptographicException)
        {
            Problem = ReportProblem.KeyMismatch;
            return;
        }

        if (run.Socket?.Write(ReportWebSocketProtocol.ReportMessage(request.Headers, report.Body)) != true)
        {
            // The connection ended meanwhile; its end drops the report as unknown.
            ReportLog.Write($"websocket report {seq} not written");
        }
    }

    private void Delivered(ReportStreamRun run, int seq)
    {
        run.Reports.Remove(seq);
        LastDeliveryAt = DateTimeOffset.UtcNow;
        Rank = run.Scheduler.Rank ?? Rank;
        RetryReason = null;
        RateLimitedUntil = null;
        if (run.SigningWithPending)
        {
            run.SigningWithPending = false;
            PromotePendingKey();
        }

        RotateKeyIfDue();
    }

    /// <summary>The reactions of REPORTING 8.3 the stream scheduler asks for.</summary>
    private void React(ReportStreamRun run, ReportStreamReaction reaction, ReportSample sample)
    {
        switch (reaction)
        {
            case ReportStreamReaction.CorrectClock:
                React(new ReportReaction.CorrectClock(run.ServerTime));
                break;
            case ReportStreamReaction.ReRegister:
                React(new ReportReaction.ReRegister());
                break;
            case ReportStreamReaction.RecheckRegistration:
                // A rotated key waiting for the service's answer is tried first, then the registration (REPORTING 8.3).
                if (_pendingSigner is not null && !run.SigningWithPending)
                {
                    run.SigningWithPending = true;
                }
                else
                {
                    run.SigningWithPending = false;
                    ReRegister();
                }

                break;
            case ReportStreamReaction.Rename:
                React(new ReportReaction.Rename(null));
                break;
            case ReportStreamReaction.NewRun:
                run.NewRun(sample);
                break;
            case ReportStreamReaction.RefreshDiscovery:
                RefreshDiscoveryIfDue(force: true);
                if (ServiceState.Kind != ReportServiceStateKind.Checking)
                {
                    ApplyStream(run, run.Scheduler.RefreshFailed(MonotonicMs), sample);
                }

                break;
        }
    }

    /// <summary>A stop that the shared status line already has a sentence for becomes that problem (the user's fix clears it).</summary>
    private void Halted(ReportStreamStop stop)
    {
        ReportLog.Write($"websocket stopped: {ReportStreamAction.Name(stop)}");
        if (ReportStreamStatus.ProblemOf(stop) is not { } problem)
        {
            return;
        }

        Problem = problem;
        if (problem == ReportProblem.Gone)
        {
            UpdatePreferences(p => p with { Enabled = false });
        }
    }

    /// <summary>A refreshed document reaches the session: new settings for the next connection; null for a failed refresh.</summary>
    private void StreamDiscovery(ReportDiscovery? document)
    {
        if (_stream is not { } run)
        {
            return;
        }

        var now = MonotonicMs;
        var sample = LastSample;
        if (document is null)
        {
            ApplyStream(run, run.Scheduler.RefreshFailed(now), sample);
        }
        else
        {
            run.Scheduler.HeartbeatMs = document.Limits.HeartbeatSeconds * 1000L;
            ApplyStream(run, run.Scheduler.SettingsChanged(now, document.WebSocket), sample);
        }

        AfterStream();
    }
}
