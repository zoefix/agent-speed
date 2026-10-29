using System.Security.Cryptography;
using AgentSpeed.Core;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Reporting;

/// <summary>
/// The live side (METRICS 11.5): every engine tick feeds the window and the scheduler; built reports are signed and sent
/// one at a time; each outcome goes back to the scheduler and triggers the reaction of REPORTING 8.3.
/// </summary>
internal sealed partial class ReportController
{
    /// <summary>A reply, a transport failure, or a key that could not sign.</summary>
    private readonly record struct Attempt(ReportCallResult Result, bool SigningFailed)
    {
        public ReportReply? Reply => Result.Reply;
    }

    private void OnLive(object? sender, SpeedSnapshot snapshot)
    {
        _monitorRunning = snapshot.State.Kind == MonitorStateKind.Running;
        if (!_monitorRunning)
        {
            return;
        }

        _lastSnapshot = snapshot;
        // Off and unwatched: nothing to compute until reporting or a preview needs this tick.
        if (_run is null && _stream is null && !Preferences.Enabled && _previewWindow is null)
        {
            return;
        }

        if (snapshot.ObservedAtMs is { } observed)
        {
            _observation = _observation is { } earliest ? Math.Min(earliest, observed) : observed;
        }

        Tick();
    }

    /// <summary>The latest tick's values (METRICS 11.2): the live snapshot and the latest traffic sample.</summary>
    private ReportSample LastSample
    {
        get
        {
            if (_lastSnapshot is not { } snapshot)
            {
                return _cachedSample;
            }

            _lastSnapshot = null;
            var settings = _settings.Current;
            _cachedSample = ReportSample.From(snapshot,
                ReportTrafficSample.From(_traffic.Hub.Snapshot, settings.Traffic.IncludeTools, settings.EnabledSourceSet()));
            return _cachedSample;
        }
    }

    /// <summary>A user event (pause, resume, network, stop) is handled at once with the latest values.</summary>
    private void Post(ReportEvent e)
    {
        _pendingEvents.Add(e);
        Tick();
    }

    /// <summary>Readiness the scheduler does not track itself (METRICS 11.5, Ready).</summary>
    private bool IsReady() =>
        Preferences.Enabled && _monitorRunning && Problem is null && _signer is not null
        && Identity is { IsRegistered: true, Pin: not null, Discovery: { SupportsThisClient: true } } identity
        && Consent?.Service.Origin != identity.Id && !NeedsReview && !IsDiscoveryStale;

    /// <summary>The e2e harness's micro-benchmark: one engine tick with the latest values.</summary>
    internal void BenchTick()
    {
        _lastSnapshot ??= _hub.Snapshot;
        Tick();
    }

    private void Tick()
    {
        TickTransport();
        FollowPerfWatch();
    }

    private void TickTransport()
    {
        FollowEffectiveTransport();
        var sample = LastSample;
        var now = MonotonicMs;
        _previewWindow?.Observe(sample);
        if (UsesWebSocket)
        {
            EndHttpRun();
            StreamTick(sample, now, wake: false);
            return;
        }

        EndStream();
        _observation = null;
        StartRunIfNeeded(sample);
        if (_run is not { } run)
        {
            _pendingEvents.Clear();
            return;
        }

        var events = _pendingEvents.ToArray();
        _pendingEvents.Clear();
        var active = sample.IsWorking || sample.Live.Output > 0 || run.Window.HasTokenIncrement(sample);
        var settings = _settings.Current;
        run.Title.Update(sample.Value(settings.Metric), settings.Redline, now / 1000.0);
        var actions = run.Scheduler.Tick(now, active, blocked: !IsReady(), events);
        Apply(actions, sample);
        if (_run is { } current && Mode != current.Scheduler.Mode)
        {
            Mode = current.Scheduler.Mode;
            Notify();
        }

        if (events.Contains(ReportEvent.Stop))
        {
            EndRunWhenIdle();
        }
    }

    private void Apply(IReadOnlyList<ReportAction> actions, ReportSample sample)
    {
        var windowStarted = false;
        foreach (var action in actions)
        {
            switch (action)
            {
                case ReportAction.Discard discard:
                    _run?.Window.Discard(sample, discard.KeepingTick);
                    windowStarted = true;
                    break;
                case ReportAction.Build build:
                    BuildReport(build.Ticket, sample);
                    windowStarted = true;
                    break;
                case ReportAction.Send send:
                    Send(send.Ticket);
                    break;
                case ReportAction.Delivered delivered:
                    Forget(delivered.Ticket);
                    break;
                case ReportAction.Lost lost:
                    Forget(lost.Ticket);
                    break;
            }
        }

        _run?.Window.Observe(sample, peaks: !windowStarted);

        void Forget(ReportTicket ticket)
        {
            _run?.Bodies.Remove(ticket.Seq);
            _run?.SignWithPending.Remove(ticket.Seq);
        }
    }

    private void StartRunIfNeeded(ReportSample sample)
    {
        if (_run is not null || !Preferences.Enabled || !_monitorRunning || _signer is null
            || Identity is not { IsRegistered: true, Discovery: { } document } identity)
        {
            return;
        }

        _run = new ReportRun(identity.Id, new ReportScheduler(document.Limits, IsPaused, !NetworkEnabled), new ReportWindow(sample));
        _renamedForMismatch = false;
    }

    /// <summary>Ends the run of either transport (a new run, another service, a reset, forgetting).</summary>
    private void EndRun()
    {
        EndStream();
        EndHttpRun();
    }

    private void EndHttpRun()
    {
        if (_run is null)
        {
            return;
        }

        _run = null;
        if (Mode != ReportMode.Quiet)
        {
            Mode = ReportMode.Quiet;
            Notify();
        }
    }

    /// <summary>After <c>stop</c>: the run ends once its leaving report is answered (or at once when nothing is in flight).</summary>
    private void EndRunWhenIdle()
    {
        if (_run is not { } run)
        {
            return;
        }

        if (run.Scheduler.IsBusy)
        {
            var id = run.Id;
            After(ReportApi.ApiTimeout + TimeSpan.FromSeconds(1), () =>
            {
                if (_run?.Id == id && _run.Scheduler.Stopped)
                {
                    EndRun();
                }
            });
        }
        else if (run.Scheduler.Stopped)
        {
            EndRun();
        }
    }

    private void BuildReport(ReportTicket ticket, ReportSample sample)
    {
        if (_run is not { } run || Identity is not { Username: { } username, Discovery: { } document } identity)
        {
            return;
        }

        var content = run.Window.Take(sample) with { Perf = TakePerf(sample) };
        var envelope = new ReportEnvelope(Client, identity.DeviceId, username, run.Id, ticket.Seq, UnixMs + _clockCorrection * 1000,
            ticket.WindowMs, ticket.Activity, run.Title.Level);
        run.Bodies[ticket.Seq] = ReportBody.Data(envelope, content, sample, Options(document, Preferences.Sections));
    }

    private ReportOptions Options(ReportDiscovery document, IEnumerable<ReportSection> switches)
    {
        var settings = _settings.Current;
        return new ReportOptions(
            switches.Where(document.Sections.Contains).ToHashSet(), document.Limits.MaxModels, document.Limits.SendLimitBytes,
            settings.Metric, settings.Redline, _priceBook.ReportPriceVersion(UnixMs)) { Extensions = document.Extensions };
    }

    private void Send(ReportTicket ticket)
    {
        if (_run is not { } run || !run.Bodies.TryGetValue(ticket.Seq, out var body) || Identity is not { Discovery: { } document } identity)
        {
            var runId = _run?.Id ?? string.Empty;
            _ui.BeginInvoke(() => Complete(ticket, runId, new Attempt(new ReportCallResult(null, null), SigningFailed: false), usedPending: false));
            return;
        }

        var usedPending = run.SignWithPending.Contains(ticket.Seq);
        var key = usedPending ? _pendingSigner : _signer;
        var builder = new ReportRequestBuilder(Client.UserAgent, identity.DeviceId, _apiKey);
        var url = document.Api.Endpoint(ReportEndpoint.Reports);
        var timestamp = UnixMs / 1000 + _clockCorrection;
        var id = run.Id;
        _ = SendAsync();

        async Task SendAsync()
        {
            var attempt = key is null
                ? new Attempt(default, SigningFailed: true)
                : await Perform(builder, "POST", url, body, key, timestamp).ConfigureAwait(true);
            Complete(ticket, id, attempt, usedPending);
        }
    }

    /// <summary>Signs and sends one request.</summary>
    private async Task<Attempt> Perform(ReportRequestBuilder builder, string method, Uri url, byte[] body, IReportSigner signer, long timestamp,
        IReportSigner? newKey = null)
    {
        // The network master switch stops every request, wherever it comes from (NETWORK 1, 6).
        if (!NetworkEnabled)
        {
            return new Attempt(new ReportCallResult(null, new ReportTransportException(ReportTransportErrorKind.Offline, "network off")), SigningFailed: false);
        }

        ReportRequest request;
        try
        {
            request = builder.Make(method, url, body, signer, timestamp, newKey: newKey);
        }
        catch (CryptographicException)
        {
            return new Attempt(default, SigningFailed: true);
        }

        return new Attempt(await _api.CallAsync(request).ConfigureAwait(true), SigningFailed: false);
    }

    private void Complete(ReportTicket ticket, string runId, Attempt attempt, bool usedPending)
    {
        var now = MonotonicMs;
        ReportAttemptOutcome outcome;
        ReportReaction reaction;
        if (attempt.SigningFailed)
        {
            (outcome, reaction) = (new ReportAttemptOutcome.Rejected(), new ReportReaction.NeedsUser(ReportProblem.KeyMismatch));
        }
        else
        {
            (outcome, reaction) = ReportOutcome.Classify(attempt.Reply, _pendingSigner is not null && !usedPending);
            Note(attempt, outcome, usedPending);
        }

        if (_run is not { } run || run.Id != runId)
        {
            return;
        }

        if (reaction is ReportReaction.UsePendingKey)
        {
            run.SignWithPending.Add(ticket.Seq);
        }

        var actions = run.Scheduler.Finish(ticket, outcome, now);
        Apply(actions, LastSample);
        React(reaction);
        FollowPerfWatch();
        if (_quitReply is not null && _run?.Scheduler.IsBusy != true)
        {
            FinishQuit();
        }

        if (_run is { Scheduler: { Stopped: true, IsBusy: false } })
        {
            EndRun();
        }

        Notify();
    }

    /// <summary>Status bookkeeping of one answer.</summary>
    private void Note(Attempt attempt, ReportAttemptOutcome outcome, bool usedPending)
    {
        switch (outcome)
        {
            case ReportAttemptOutcome.Delivered:
                LastDeliveryAt = DateTimeOffset.UtcNow;
                if (attempt.Reply?.Rank is { } rank)
                {
                    Rank = rank;
                }

                RetryReason = null;
                RateLimitedUntil = null;
                _strikes = 0;
                if (usedPending)
                {
                    PromotePendingKey();
                }

                RotateKeyIfDue();
                break;
            case ReportAttemptOutcome.Transient:
                RetryReason = attempt.Result.Error?.Failure ?? ReportFailure.Unavailable;
                break;
            case ReportAttemptOutcome.RateLimited limited:
                RateLimitedUntil = DateTimeOffset.UtcNow.AddSeconds(limited.Seconds);
                RetryReason = null;
                After(TimeSpan.FromSeconds(limited.Seconds + 1), Notify);
                break;
        }
    }

    /// <summary>The reactions of REPORTING 8.3.</summary>
    private void React(ReportReaction reaction)
    {
        switch (reaction)
        {
            case ReportReaction.CorrectClock correct when correct.ServerTime is { } serverTime:
                var correction = serverTime - UnixMs / 1000;
                if (Math.Abs(correction) <= 86_400)
                {
                    _clockCorrection = correction;
                }

                break;
            case ReportReaction.ReRegister:
                if (_lastReRegister is { } last && DateTimeOffset.UtcNow - last < ReRegisterInterval)
                {
                    return;
                }

                _lastReRegister = DateTimeOffset.UtcNow;
                ReRegister();
                break;
            case ReportReaction.RecheckRegistration:
                ReRegister();
                break;
            case ReportReaction.Rename:
                if (!_renamedForMismatch && Identity?.Username is { } name)
                {
                    _renamedForMismatch = true;
                    _ = Rename(name);
                }

                break;
            case ReportReaction.NewRun:
                EndRun();
                break;
            case ReportReaction.RefreshDiscovery refresh:
                if (refresh.Upgrade)
                {
                    Problem = ReportProblem.Upgrade;
                }

                RefreshDiscoveryIfDue(force: true);
                break;
            case ReportReaction.Strike:
                if (++_strikes >= 3)
                {
                    Problem = ReportProblem.Rejected;
                }

                break;
            case ReportReaction.NeedsUser needs:
                Problem = needs.Problem;
                if (needs.Problem == ReportProblem.Gone)
                {
                    UpdatePreferences(p => p with { Enabled = false });
                    EndRun();
                }

                break;
        }
    }
}
