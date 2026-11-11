using AgentSpeed.Core.Quota;

namespace AgentSpeed.Core.Remote;

/// <summary>The protocol state of one running relay (REMOTE 5, 6, 8.2).</summary>
internal sealed class RemoteSessionState(string hostId)
{
    public RemoteLineFramer Framer { get; } = new();

    public RelayLineDecoder Decoder { get; } = new(hostId);

    public int InvalidLines { get; set; }

    public bool HelloReceived { get; set; }

    /// <summary>When <c>hello</c> arrived: the back-off resets after 60 s up.</summary>
    public DateTimeOffset? HelloAt { get; set; }

    public bool Ready { get; set; }

    public string? RelayVersion { get; set; }

    public string? RelayArch { get; set; }

    public RemoteClockEstimator Clock { get; } = new();

    public Dictionary<long, long> PendingPings { get; } = [];

    public long NextPingId { get; set; } = 1;

    public DateTimeOffset LastLineAt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Prefixed ids of the agents this connection sent and did not remove (at most 4096).</summary>
    public HashSet<string> AgentIds { get; } = new(StringComparer.Ordinal);

    public bool HistoryInFlight { get; set; }

    public bool HistoryDuePending { get; set; }

    /// <summary>The clock offset last reported, in whole seconds.</summary>
    public long? ReportedOffsetSeconds { get; set; }
}

internal sealed partial class RemoteConnection
{
    /// <summary>The relay runs: <c>start</c> is its first line, then the watch if it is on.</summary>
    private void BeginSession()
    {
        _session.LastLineAt = Now;
        ScheduleSilenceCheck();
        Send(RelayLineEncoder.Start(_environment.TodayStartMs(Now), _settings.Sources, RemoteZone.Current(_environment.TimeZone()), _settings.Prices,
            _settings.Quota));
        if (_settings.Watch)
        {
            Send(RelayLineEncoder.Watch(true));
        }
    }

    private void ReceivedRelayOutput(byte[] data)
    {
        _session.LastLineAt = Now;
        var (lines, oversized) = _session.Framer.Consume(data);
        foreach (var line in lines)
        {
            if (_stopped || _stage != Stage.Run || _endReason is not null)
            {
                return;
            }

            Handle(line);
        }

        for (var i = 0; i < oversized && _endReason is null; i++)
        {
            CountInvalid();
        }
    }

    private void Handle(byte[] line)
    {
        var outcome = _session.Decoder.Decode(line);
        switch (outcome.Kind)
        {
            case RelayDecodeKind.Invalid:
                CountInvalid();
                break;
            case RelayDecodeKind.Message:
                Apply(outcome.Message!);
                break;
        }
    }

    /// <summary>More than 100 invalid lines end the connection (REMOTE 5.6).</summary>
    private void CountInvalid()
    {
        _session.InvalidLines++;
        if (_session.InvalidLines > RemoteLimits.InvalidLines)
        {
            EndProcess(RemoteStatus.ProtocolError);
        }
    }

    private void Apply(RelayMessage message)
    {
        if (!_session.HelloReceived)
        {
            if (message is RelayMessage.Hello { Value: var hello })
            {
                Received(hello);
            }
            else
            {
                CountInvalid();
            }

            return;
        }

        switch (message)
        {
            case RelayMessage.Hello:
                CountInvalid();
                break;
            case RelayMessage.Clock { Id: var id, NowMs: var remoteNowMs }:
                if (_session.PendingPings.Remove(id, out var sentMs))
                {
                    _session.Clock.AddSample(sentMs, Now.ToUnixTimeMilliseconds(), remoteNowMs);
                    ReportClockIfChanged();
                }

                break;
            case RelayMessage.Progress { Value: var fraction }:
                if (!_session.Ready)
                {
                    Report(RemoteStatus.Seeding(fraction));
                }

                break;
            case RelayMessage.Ready:
                if (_session.Ready)
                {
                    return;
                }

                _session.Ready = true;
                Emit(new RemoteHostEventKind.Message(message, Seeded: true));
                Report(RemoteStatus.Connected);
                HistoryDue();
                Schedule(TimerSlot.History, HistoryInterval, HistoryTimerFired);
                ScheduleMidnight();
                break;
            case RelayMessage.Agent { Value: var agent }:
                if (!_session.AgentIds.Contains(agent.Id) && _session.AgentIds.Count >= RemoteLimits.Agents)
                {
                    return;
                }

                if (EmitConverted(message))
                {
                    _session.AgentIds.Add(agent.Id);
                }

                break;
            case RelayMessage.AgentRemoved { Id: var removed }:
                _session.AgentIds.Remove(removed);
                Emit(new RemoteHostEventKind.Message(message, !_session.Ready));
                break;
            case RelayMessage.Response or RelayMessage.Tool or RelayMessage.Quota:
                EmitConverted(message);
                break;
            case RelayMessage.QuotaBridge { Value.State: var state }:
                Emit(new RemoteHostEventKind.Message(message, !_session.Ready));
                if (_retiring && state == ClaudeCliBridgeStatus.Off)
                {
                    Halt();
                }

                break;
            case RelayMessage.HistoryDone:
                Emit(new RemoteHostEventKind.Message(message, !_session.Ready));
                HistoryRunEnded();
                break;
            case RelayMessage.Error when message.IsHistoryFailure:
                Emit(new RemoteHostEventKind.Message(message, !_session.Ready));
                HistoryRunEnded();
                break;
            default:
                Emit(new RemoteHostEventKind.Message(message, !_session.Ready));
                break;
        }
    }

    private void Received(RelayHello hello)
    {
        _session.HelloReceived = true;
        if (hello.ProtocolVersion != RemoteProtocol.Version)
        {
            EndProcess(RemoteStatus.VersionMismatch(hello.ProtocolVersion));
            return;
        }

        _session.HelloAt = Now;
        _session.RelayVersion = hello.Version;
        _session.RelayArch = hello.Arch;
        _session.Clock.NoteHello(hello.NowMs, Now.ToUnixTimeMilliseconds());
        _session.ReportedOffsetSeconds = _session.Clock.OffsetMs / 1000;
        Emit(new RemoteHostEventKind.Message(new RelayMessage.Hello(hello), Seeded: true));
        Report(RemoteStatus.Seeding(0));
        SendPing();
    }

    /// <summary>Converts the line's times with the current offset; a time that leaves 2000–2100 makes it invalid.</summary>
    private bool EmitConverted(RelayMessage message)
    {
        if (message.Converted(_session.Clock.OffsetMs ?? 0) is not { } converted)
        {
            CountInvalid();
            return false;
        }

        Emit(new RemoteHostEventKind.Message(converted, !_session.Ready));
        return true;
    }

    private void ReportClockIfChanged()
    {
        var seconds = _session.Clock.OffsetMs / 1000;
        if (seconds == _session.ReportedOffsetSeconds || _lastStatus?.Status is not { } status)
        {
            return;
        }

        _session.ReportedOffsetSeconds = seconds;
        Report(status);
    }

    // Pings and silence

    private void SendPing()
    {
        var id = _session.NextPingId++;
        _session.PendingPings[id] = Now.ToUnixTimeMilliseconds();
        if (_session.PendingPings.Count > PendingPingLimit)
        {
            _session.PendingPings.Remove(_session.PendingPings.Keys.Min());
        }

        Send(RelayLineEncoder.Ping(id));
        Schedule(TimerSlot.Ping, PingInterval, SendPing);
    }

    /// <summary>150 s without any line end the connection as <c>unreachable</c> (REMOTE 4.4).</summary>
    private void ScheduleSilenceCheck() => Schedule(TimerSlot.Silence, _session.LastLineAt + SilenceLimit, () =>
    {
        if (_stage != Stage.Run)
        {
            return;
        }

        if (Now - _session.LastLineAt >= SilenceLimit)
        {
            EndProcess(RemoteStatus.Unreachable);
        }
        else
        {
            ScheduleSilenceCheck();
        }
    });

    // History (REMOTE 8.2)

    /// <summary>Sends <c>history</c> with the digests the app holds for the host. Ignored before <c>ready</c>.</summary>
    public void RequestHistory(IReadOnlyDictionary<string, string> digests) => Queue.Post(() =>
    {
        if (_stage != Stage.Run || !_session.Ready || RelayLineEncoder.History(digests) is not { } line)
        {
            return;
        }

        _session.HistoryInFlight = true;
        Send(line);
    });

    /// <summary>The app's time zone changed: the relay gets the zone and the new local midnight, the schedule its next one.</summary>
    public void NoteTimeZoneChange() => Queue.Post(() =>
    {
        if (_stage != Stage.Run)
        {
            return;
        }

        Send(RelayLineEncoder.Zone(RemoteZone.Current(_environment.TimeZone())));
        Send(RelayLineEncoder.Today(_environment.TodayStartMs(Now)));
        if (_session.Ready)
        {
            ScheduleMidnight();
        }
    });

    /// <summary>A <c>historyDone</c> or a <c>historyFailed</c> error ends the run: a sync that waited for it is due now.</summary>
    private void HistoryRunEnded()
    {
        _session.HistoryInFlight = false;
        if (!_session.HistoryDuePending)
        {
            return;
        }

        _session.HistoryDuePending = false;
        HistoryDue();
    }

    /// <summary>Asks the app for a sync, or once the running one is done.</summary>
    private void HistoryDue()
    {
        if (_session.HistoryInFlight)
        {
            _session.HistoryDuePending = true;
        }
        else
        {
            Emit(new RemoteHostEventKind.HistoryDue());
        }
    }

    private void HistoryTimerFired()
    {
        HistoryDue();
        Schedule(TimerSlot.History, HistoryInterval, HistoryTimerFired);
    }

    private void ScheduleMidnight() => Schedule(TimerSlot.Midnight, _environment.NextMidnight(Now), () =>
    {
        Send(RelayLineEncoder.Today(_environment.TodayStartMs(Now)));
        HistoryDue();
        ScheduleMidnight();
    });
}
