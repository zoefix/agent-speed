using System.Windows.Threading;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Reporting;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Reporting;

/// <summary>
/// Live reporting over WebSocket (METRICS 11.9, DESIGN 11 item 2b), the default transport: every snapshot and every wake-up the
/// stream scheduler asks for drive one transport session; its actions open and end the connection, build and write reports
/// and take the reactions of REPORTING 8.3. Nothing of this runs while HTTP is effective — chosen by the user, or because the
/// service's document lacks the capability — and a stop of this transport never falls back to HTTP.
/// </summary>
internal sealed partial class ReportController
{
    private static readonly TimeSpan CloseFrameGrace = TimeSpan.FromMilliseconds(150);

    private readonly IReportStreamTransport _streamTransport;
    private ReportStreamRun? _stream;
    private DispatcherTimer? _wakeTimer;
    private long? _wakeAt;
    private long? _observation;
    private bool _quitting;
    private bool _quitFlushing;
    private StreamSignature _streamSignature;
    private readonly ReportTransportTracker _effectiveTransport = new();

    /// <summary>The selected service's chosen transport (DESIGN 11, Transport): WebSocket by default, HTTP by the user's choice.</summary>
    public ReportTransportKind Transport => Preferences.TransportOf(SelectedService);

    /// <summary>The transport reports take (METRICS 11.9): the choice, or HTTP while the stored document lacks the capability.</summary>
    public ReportTransportKind EffectiveTransport => Preferences.EffectiveTransportOf(SelectedService);

    public bool UsesWebSocket => EffectiveTransport == ReportTransportKind.WebSocket;

    /// <summary>The WebSocket transport session (counters, latency, connection), or null while none runs.</summary>
    public ReportStreamScheduler? StreamSession => _stream?.Scheduler;

    /// <summary>The stream's own status line (DESIGN 11, Status), or null when the shared statuses apply.</summary>
    public ReportStreamStatusKind? StreamStatus => UsesWebSocket && _stream is { Scheduler: var s }
        ? ReportStreamStatus.Of(s.Stop, s.Connection, s.Failures, s.Mode)
        : null;

    /// <summary>
    /// Chooses the selected service's transport. When that changes the effective transport it applies at once (see
    /// <see cref="FollowEffectiveTransport"/>); choosing HTTP while WebSocket falls back to it keeps the HTTP run going.
    /// </summary>
    public void SetTransport(ReportTransportKind transport)
    {
        if (SelectedService is not { } service || transport == Transport)
        {
            return;
        }

        UpdatePreferences(p => p.WithTransport(service.Origin, transport));
        ReportLog.Write($"transport chosen for {service.Host}: {Name(transport)}");
        Tick();
        Notify();
    }

    /// <summary>
    /// A change of the effective transport — the user's choice, or a stored or refreshed document that adds or drops the
    /// capability — is a change of transport (METRICS 11.9): the old run stops without a leaving report (its unanswered reports
    /// are dropped) and the caller's tick starts a new run on the new one. True when it changed.
    /// </summary>
    private bool FollowEffectiveTransport()
    {
        var service = SelectedService;
        var effective = EffectiveTransport;
        if (!_effectiveTransport.Update(service?.Origin, effective))
        {
            return false;
        }

        EndRun();
        RetryReason = null;
        RateLimitedUntil = null;
        LastDeliveryAt = null;
        ClearRejection();
        var fallback = effective != Transport ? " (the service lacks websocket)" : string.Empty;
        ReportLog.Write($"transport for {service?.Host}: {Name(effective)}{fallback}");
        return true;
    }

    private static string Name(ReportTransportKind transport) => transport == ReportTransportKind.WebSocket ? "websocket" : "http";

    /// <summary><i>Try Again</i> after <c>report.status.wsMismatch</c> or <c>report.status.wsReplaced</c>: clears the stop and reconnects.</summary>
    public void RetryStream()
    {
        if (_stream is not { } run || !NetworkEnabled)
        {
            return;
        }

        var sample = LastSample;
        ApplyStream(run, run.Scheduler.Retry(MonotonicMs), sample);
        StreamTick(sample, MonotonicMs, wake: false);
        Notify();
    }

    /// <summary>At quit with an open connection: one <c>leaving</c> report, then the close, within a second (METRICS 11.9).</summary>
    private void PrepareStreamToQuit(Action reply)
    {
        _quitting = true;
        if (_stream is not { Scheduler: var s } || s.Connection != ReportStreamConnection.Open || s.Mode == ReportMode.Quiet || !IsReady()
            || !NetworkEnabled)
        {
            EndStream();
            reply();
            return;
        }

        _quitReply = reply;
        After(TimeSpan.FromSeconds(1), FinishQuit);
        Post(ReportEvent.Stop);
    }

    /// <summary>One tick of the stream scheduler: a snapshot, a user event or (<paramref name="wake"/>) a wake-up it asked for.</summary>
    private void StreamTick(ReportSample sample, long now, bool wake)
    {
        if (_stream is { } stale && stale.Origin != SelectedService?.Origin)
        {
            EndStream();
        }

        StartStreamIfNeeded(sample);
        var events = _pendingEvents.ToArray();
        _pendingEvents.Clear();
        var observed = wake ? null : _observation;
        if (!wake)
        {
            _observation = null;
        }

        if (_stream is not { } run)
        {
            return;
        }

        var active = wake ? run.LastActive : sample.IsWorking || sample.Live.Output > 0 || run.Window.HasTokenIncrement(sample);
        run.LastActive = active;
        var changed = !wake && (run.Window.HasIncrement(sample, EffectiveSections.Contains(ReportSection.Traffic))
            || ReportStreamValues.Of(sample, ScoreExtension) != run.LastBuilt);
        var settings = _settings.Current;
        run.Title.Update(sample.Value(settings.Metric), settings.Redline, now / 1000.0);
        if (run.Scheduler.Stop is { } stop && ReportStreamStatus.ProblemOf(stop) is not null && Problem is null)
        {
            // The user fixed what the stop stood for (a key, a section, a reset): automatic attempts resume.
            ApplyStream(run, run.Scheduler.Retry(now), sample);
        }

        ApplyStream(run, run.Scheduler.Tick(now, active, changed, observed, blocked: !IsReady(), events), sample);
        AfterStream();
    }

    private void StartStreamIfNeeded(ReportSample sample)
    {
        if (_stream is not null || _quitting || !Preferences.Enabled || !_monitorRunning || _signer is null
            || Identity is not { IsRegistered: true, Discovery: { } document } identity)
        {
            return;
        }

        var scheduler = new ReportStreamScheduler(document.WebSocket, document.Limits.HeartbeatSeconds, IsPaused, !NetworkEnabled);
        _stream = new ReportStreamRun(identity.Id, scheduler, new ReportWindow(sample));
        _renamedForMismatch = false;
        ReportLog.Write($"websocket session for {identity.Service.Host}");
    }

    /// <summary>Ends the session: an open connection is closed (a leaving report already written goes first), any other dropped.</summary>
    private void EndStream()
    {
        if (_stream is not { } run)
        {
            return;
        }

        _stream = null;
        CancelWake();
        if (run.Socket is { } socket)
        {
            if (run.Scheduler.Connection is ReportStreamConnection.Open or ReportStreamConnection.Closing)
            {
                socket.Close();
            }
            else
            {
                socket.Abort();
            }

            run.Socket = null;
        }

        if (Mode != ReportMode.Quiet)
        {
            Mode = ReportMode.Quiet;
        }

        Notify();
    }

    private void ApplyStream(ReportStreamRun run, IReadOnlyList<ReportStreamAction> actions, ReportSample sample)
    {
        var windowStarted = false;
        foreach (var action in actions)
        {
            switch (action)
            {
                case ReportStreamAction.Discard discard:
                    run.Window.Discard(sample, discard.KeepingTick);
                    windowStarted = true;
                    break;
                case ReportStreamAction.Connect:
                    Connect(run);
                    break;
                case ReportStreamAction.Abort:
                    run.Socket?.Abort();
                    run.Socket = null;
                    break;
                case ReportStreamAction.Close:
                    run.Socket?.Close();
                    run.Socket = null;
                    break;
                case ReportStreamAction.Build build:
                    Build(run, build.Ticket, sample);
                    windowStarted = true;
                    break;
                case ReportStreamAction.Write write:
                    Write(run, write.Seq, sample);
                    break;
                case ReportStreamAction.Delivered delivered:
                    Delivered(run, delivered.Seq);
                    break;
                case ReportStreamAction.Dropped dropped:
                    run.Reports.Remove(dropped.Seq);
                    break;
                case ReportStreamAction.React react:
                    React(run, react.Reaction, sample);
                    break;
                case ReportStreamAction.Stop stop:
                    Halted(stop.Reason);
                    break;
                case ReportStreamAction.Wait wait:
                    var seconds = Math.Max(0, (wait.Until - MonotonicMs) / 1000.0);
                    RateLimitedUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
                    After(TimeSpan.FromSeconds(seconds + 1), Notify);
                    break;
            }
        }

        run.Window.Observe(sample, peaks: !windowStarted);
    }

    /// <summary>After any step: the mode, the wake-up timer, the end of a finished session, the quit reply, the task watch and the status.</summary>
    private void AfterStream()
    {
        FollowPerfWatch();
        if (_stream is { } run)
        {
            Mode = run.Scheduler.Mode;
            ScheduleWake(run.Scheduler.WakeAt);
            if ((run.Scheduler.Ended || !Preferences.Enabled) && run.Scheduler.Connection == ReportStreamConnection.Closed)
            {
                EndStream();
            }
        }

        if (_quitReply is not null && !_quitFlushing && _stream?.Scheduler.Connection is null or ReportStreamConnection.Closed)
        {
            // The close frame after the leaving report is queued; give it a moment within the one-second cap.
            _quitFlushing = true;
            After(CloseFrameGrace, FinishQuit);
        }

        var signature = StreamSignature.Of(_stream?.Scheduler, Mode, LastDeliveryAt is null, RateLimitedUntil is null);
        if (signature != _streamSignature)
        {
            _streamSignature = signature;
            Notify();
        }
    }

    private void ScheduleWake(long? at)
    {
        if (at == _wakeAt)
        {
            return;
        }

        CancelWake();
        if (at is not { } target)
        {
            return;
        }

        _wakeAt = target;
        if (_wakeTimer is null)
        {
            _wakeTimer = new DispatcherTimer(DispatcherPriority.Normal, _ui);
            _wakeTimer.Tick += (_, _) =>
            {
                CancelWake();
                if (_stream is not null)
                {
                    StreamTick(LastSample, MonotonicMs, wake: true);
                }
            };
        }

        _wakeTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, target - MonotonicMs));
        _wakeTimer.Start();
    }

    private void CancelWake()
    {
        _wakeTimer?.Stop();
        _wakeAt = null;
    }

    /// <summary>What the pane shows of the session; a change of it notifies, an unchanged tick does not.</summary>
    private readonly record struct StreamSignature(
        bool Open, ReportStreamStop? Stop, bool Available, bool Failing, ReportMode Mode, bool NeverDelivered, bool NotLimited, int Settled)
    {
        public static StreamSignature Of(ReportStreamScheduler? s, ReportMode mode, bool neverDelivered, bool notLimited) => new(
            s?.Connection == ReportStreamConnection.Open, s?.Stop, s?.Available ?? true, s?.Failures > 0, mode, neverDelivered, notLimited,
            s is null ? 0 : s.Counters.Delivered + s.Counters.NotDelivered > 0 ? 2 : 1);
    }
}
