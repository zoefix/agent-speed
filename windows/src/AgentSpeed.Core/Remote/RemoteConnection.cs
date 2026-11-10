namespace AgentSpeed.Core.Remote;

/// <summary>
/// One host's connection (REMOTE 4.2–4.4): probe → (upload) → run over the system <c>ssh</c>, then the protocol of section 5
/// on the relay's stdin and stdout, with pings, the silence rule, back-off and the history schedule. Events go to
/// <c>onEvent</c> in order, on the event queue. Every mutable field is only touched on the connection's queue.
/// </summary>
internal sealed partial class RemoteConnection
{
    public static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(150);
    public static readonly TimeSpan ProbeLimit = TimeSpan.FromSeconds(150);
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan HistoryInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan KillDelay = TimeSpan.FromSeconds(2);

    /// <summary>How long a removed host's relay may take to restore its status line (REMOTE 2.4, 11.3).</summary>
    public static readonly TimeSpan RetireLimit = TimeSpan.FromSeconds(10);

    public const int UploadChunkBytes = 256 * 1024;
    public const int PendingPingLimit = 8;

    private static int _attempts;

    private readonly RemoteEnvironment _environment;
    private readonly RemoteQueue _eventQueue;
    private readonly Action<RemoteHostEvent> _onEvent;
    private readonly Dictionary<TimerSlot, IRemoteScheduledWork> _timers = [];
    private readonly RemoteBackoff _backoff = new();

    private RemoteSessionSettings _settings;
    private Stage _stage = Stage.Idle;
    private bool _stopped;

    /// <summary>Waiting for the relay to restore the host's status line before stopping; nothing is reported any more.</summary>
    private bool _retiring;
    private int _attempt;

    /// <summary>Counts launched processes, so callbacks of an old process are ignored.</summary>
    private int _processToken;
    private IRemoteProcess? _process;

    /// <summary>Why this side ended the running process (silence, protocol error, version); null when ssh ended by itself.</summary>
    private RemoteStatus? _endReason;
    private RemoteHostStatus? _lastStatus;
    private DateTimeOffset? _lastErrorAt;
    private MemoryStream _stdout = new();
    private MemoryStream _stderr = new();
    private RemoteRelayExecutable? _relay;
    private Stream? _upload;
    private System.Security.Cryptography.IncrementalHash? _uploadHash;
    private long _uploadedBytes;
    private double _reportedUploadFraction;
    private RemoteSessionState _session = new(string.Empty);

    public RemoteConnection(
        RemoteHost host, RemoteSessionSettings settings, RemoteEnvironment environment, RemoteQueue eventQueue, Action<RemoteHostEvent> onEvent)
    {
        Host = host;
        _settings = settings;
        _environment = environment;
        _eventQueue = eventQueue;
        _onEvent = onEvent;
        Queue = new RemoteQueue("remote.connection");
    }

    private enum Stage
    {
        Idle,
        Probe,
        Upload,
        Run,
    }

    private enum TimerSlot
    {
        Retry,
        StageLimit,
        Silence,
        Ping,
        History,
        Midnight,
        Retire,
    }

    public RemoteHost Host { get; }

    public RemoteQueue Queue { get; }

    private DateTimeOffset Now => _environment.Scheduling.Now;

    /// <summary>Attempt numbers are unique across connections, so a host's replaced connection never repeats one.</summary>
    private static int NextAttempt() => Interlocked.Increment(ref _attempts);

    // Commands (any thread)

    public void Start() => Queue.Post(() =>
    {
        if (!_stopped && _stage == Stage.Idle && !_timers.ContainsKey(TimerSlot.Retry))
        {
            Begin();
        }
    });

    /// <summary>
    /// Closes the relay's stdin, ends <c>ssh</c> (killed after 2 s) and reports <paramref name="finalStatus"/>, if any, as the
    /// last event. Synchronous; the connection cannot be started again.
    /// </summary>
    public void Stop(RemoteStatus? finalStatus = null) => Queue.Sync(() => Halt(finalStatus));

    /// <summary><i>Retry now</i>: an attempt at once unless one is under way; the back-off starts over.</summary>
    public void RetryNow() => Queue.Post(() =>
    {
        if (!_stopped && _stage == Stage.Idle)
        {
            _backoff.Reset();
            Begin();
        }
    });

    /// <summary>
    /// The computer woke or its network came back: a host waiting for its back-off retries at once; a running relay is
    /// pinged and its silence clock restarts.
    /// </summary>
    public void NoteWake() => Queue.Post(() =>
    {
        if (_stopped)
        {
            return;
        }

        if (_stage == Stage.Idle && _lastStatus?.Status.IsRetrying == true && _timers.ContainsKey(TimerSlot.Retry))
        {
            Begin();
        }
        else if (_stage == Stage.Run)
        {
            _session.LastLineAt = Now;
            if (_session.HelloReceived)
            {
                SendPing();
            }
        }
    });

    public void UpdatePrices(RemotePriceTableJson prices) => Queue.Post(() =>
    {
        if (_settings.Prices == prices)
        {
            return;
        }

        _settings = _settings with { Prices = prices };
        if (_stage == Stage.Run)
        {
            Send(RelayLineEncoder.Prices(prices));
        }
    });

    /// <summary>The quota config changed (a switch, the host's bridge): a running relay gets a <c>quota</c> line, no reconnect.</summary>
    public void UpdateQuota(RemoteQuotaConfig config) => Queue.Post(() =>
    {
        if (_retiring || _settings.Quota == config)
        {
            return;
        }

        _settings = _settings with { Quota = config };
        if (_stage == Stage.Run)
        {
            Send(RelayLineEncoder.Quota(config));
        }
    });

    /// <summary>
    /// The host left the settings with its bridge on (REMOTE 2.4, 11.3): the relay is asked to restore the status line
    /// first, and the connection stops once it reports the bridge <c>off</c>, after <paramref name="timeout"/> (10 s) or when
    /// the relay ends. A relay that is not running stops at once. A retiring connection reports nothing.
    /// </summary>
    public void Retire(TimeSpan? timeout = null) => Queue.Post(() =>
    {
        if (_stopped || _retiring)
        {
            return;
        }

        if (_stage != Stage.Run || !_session.HelloReceived || _endReason is not null || _process is null)
        {
            Halt();
            return;
        }

        _retiring = true;
        var config = (_settings.Quota ?? RemoteQuotaConfig.Off).WithoutBridge();
        _settings = _settings with { Quota = config };
        Send(RelayLineEncoder.Quota(config));
        Schedule(TimerSlot.Retire, timeout ?? RetireLimit, () => Halt());
    });

    /// <summary><i>Reconnect</i> of the bridge: the config once without it and again with it, so the relay installs it anew.</summary>
    public void ReapplyQuota() => Queue.Post(() =>
    {
        if (_stopped || _retiring || _stage != Stage.Run || _settings.Quota is not { ClaudeBridge: true } config)
        {
            return;
        }

        Send(RelayLineEncoder.Quota(config.WithoutBridge()));
        Send(RelayLineEncoder.Quota(config));
    });

    public void SetWatch(bool on) => Queue.Post(() =>
    {
        if (_settings.Watch == on)
        {
            return;
        }

        _settings = _settings with { Watch = on };
        if (_stage == Stage.Run)
        {
            Send(RelayLineEncoder.Watch(on));
        }
    });

    // Attempts

    private void Begin()
    {
        CancelTimers();
        _attempt = NextAttempt();
        _stage = Stage.Idle;
        _endReason = null;
        _relay = null;
        _session = new RemoteSessionState(Host.Id);
        if (!Host.IsConnectable)
        {
            Report(RemoteStatus.InvalidDestination);
            return;
        }

        if (!_environment.Transport.IsAvailable(_environment.SshExecutable))
        {
            Fail(RemoteStatus.SshMissing);
            return;
        }

        Report(RemoteStatus.Connecting);
        if (RemoteCommand.Probe(Host.EffectiveRelayFolder) is not { } command)
        {
            Fail(RemoteStatus.InvalidDestination);
            return;
        }

        Launch(Stage.Probe, command);
    }

    private void Launch(Stage next, string command)
    {
        var configFile = RemoteSsh.DevelopmentConfigFile(_environment.ProcessEnvironment);
        if (RemoteSsh.Arguments(Host.Destination, command, configFile) is not { } arguments)
        {
            Fail(RemoteStatus.InvalidDestination);
            return;
        }

        var token = ++_processToken;
        _stdout = new MemoryStream();
        _stderr = new MemoryStream();
        var handlers = new RemoteProcessHandlers(
            data => Queue.Post(() => ReceivedStdout(data, token)),
            data => Queue.Post(() => ReceivedStderr(data, token)),
            status => Queue.Post(() => Exited(status, token)));
        var request = new RemoteProcessRequest(_environment.SshExecutable, arguments, RemoteSsh.Environment(_environment.ProcessEnvironment));
        try
        {
            _process = _environment.Transport.Launch(request, handlers);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _process = null;
            Fail(_environment.Transport.IsAvailable(_environment.SshExecutable) ? RemoteStatus.Exited(127) : RemoteStatus.SshMissing);
            return;
        }

        _stage = next;
        switch (next)
        {
            case Stage.Probe:
                // The probe reads nothing; Windows' OpenSSH client would otherwise keep waiting for the end of its input.
                _process.CloseInput();
                Schedule(TimerSlot.StageLimit, ProbeLimit, () => EndProcess(RemoteStatus.Unreachable));
                break;
            case Stage.Upload:
                StreamUpload();
                break;
            case Stage.Run:
                BeginSession();
                break;
        }
    }

    private void ReceivedStdout(byte[] data, int token)
    {
        if (token != _processToken || _stopped)
        {
            return;
        }

        switch (_stage)
        {
            case Stage.Probe or Stage.Upload when _stdout.Length < RemoteProbeResult.MaxOutputBytes:
                _stdout.Write(data, 0, (int)Math.Min(data.Length, RemoteProbeResult.MaxOutputBytes - _stdout.Length));
                break;
            case Stage.Run:
                ReceivedRelayOutput(data);
                break;
        }
    }

    private void ReceivedStderr(byte[] data, int token)
    {
        if (token != _processToken || _stopped)
        {
            return;
        }

        _stderr.Write(data);
        if (_stderr.Length > RemoteStatusClassifier.MaxStderrBytes)
        {
            var kept = _stderr.GetBuffer().AsSpan((int)_stderr.Length - RemoteStatusClassifier.MaxStderrBytes, RemoteStatusClassifier.MaxStderrBytes).ToArray();
            _stderr = new MemoryStream();
            _stderr.Write(kept);
        }
    }

    private void Exited(int status, int token)
    {
        if (token != _processToken)
        {
            return;
        }

        _process = null;
        Cancel(TimerSlot.StageLimit);
        CloseUpload();
        if (_stopped)
        {
            return;
        }

        if (_retiring)
        {
            Halt();
            return;
        }

        var ended = _stage;
        _stage = Stage.Idle;
        if (_endReason is { } reason)
        {
            Fail(reason);
            return;
        }

        switch (ended)
        {
            case Stage.Probe:
                ProbeFinished(status);
                break;
            case Stage.Upload:
                UploadFinished(status);
                break;
            case Stage.Run:
                Fail(RemoteStatusClassifier.Classify(status, Stderr, RemoteStage.Run));
                break;
        }
    }

    private ReadOnlySpan<byte> Stderr => _stderr.GetBuffer().AsSpan(0, (int)_stderr.Length);

    private ReadOnlySpan<byte> Stdout => _stdout.GetBuffer().AsSpan(0, (int)_stdout.Length);

    private void ProbeFinished(int status)
    {
        if (status != 0)
        {
            Fail(RemoteStatusClassifier.Classify(status, Stderr, RemoteStage.Probe));
            return;
        }

        var result = RemoteProbeResult.Parse(Stdout);
        var verdict = result.Verdict;
        switch (verdict.Kind)
        {
            case RemoteProbeVerdictKind.Malformed:
                Fail(RemoteStatus.Exited(status));
                return;
            case RemoteProbeVerdictKind.UnsupportedSystem:
                Fail(RemoteStatus.UnsupportedSystem(verdict.System));
                return;
            case RemoteProbeVerdictKind.CannotVerify:
                Fail(RemoteStatus.CannotVerify);
                return;
        }

        if (_environment.Relays.Executable(verdict.Arch) is not { } executable)
        {
            RemoteLog.Write($"No bundled relay for {verdict.Arch.Name()}");
            Fail(RemoteStatus.UploadFailed);
            return;
        }

        _relay = executable;
        if (result.HasInstalled(executable.InstalledName, executable.Sha256))
        {
            StartRelay();
        }
        else if (RemoteCommand.Upload(Host.EffectiveRelayFolder, executable.InstalledName, executable.Sha256) is { } command)
        {
            Report(RemoteStatus.Installing(0));
            Launch(Stage.Upload, command);
        }
        else
        {
            Fail(RemoteStatus.UploadFailed);
        }
    }

    private void UploadFinished(int status)
    {
        if (status != 0)
        {
            Fail(RemoteStatusClassifier.Classify(status, Stderr, RemoteStage.Upload));
            return;
        }

        if (_relay is null || RemoteProbeResult.UploadedHash(Stdout) != _relay.Sha256)
        {
            Fail(RemoteStatus.UploadFailed);
            return;
        }

        StartRelay();
    }

    private void StartRelay()
    {
        if (_relay is null || RemoteCommand.Run(Host.EffectiveRelayFolder, _relay.InstalledName) is not { } command)
        {
            Fail(RemoteStatus.UploadFailed);
            return;
        }

        Launch(Stage.Run, command);
    }

    /// <summary>
    /// Streams the bundled relay into the upload command's stdin, one chunk after the other, then closes it. The bytes are
    /// hashed on the way: when they do not match the digest the command was given, the upload ends as <c>uploadFailed</c>
    /// (the host deletes the file, REMOTE 4.2).
    /// </summary>
    private void StreamUpload()
    {
        try
        {
            _upload = _relay?.Open();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _upload = null;
        }

        if (_upload is null)
        {
            EndProcess(RemoteStatus.UploadFailed);
            return;
        }

        _uploadedBytes = 0;
        _reportedUploadFraction = 0;
        _uploadHash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        UploadNextChunk(_processToken);
    }

    private void UploadNextChunk(int token)
    {
        if (token != _processToken || _stage != Stage.Upload || _process is not { } process || _upload is not { } upload)
        {
            CloseUpload();
            return;
        }

        var chunk = new byte[UploadChunkBytes];
        int count;
        try
        {
            count = upload.Read(chunk, 0, chunk.Length);
        }
        catch (IOException)
        {
            count = 0;
        }

        if (count == 0)
        {
            var sent = _uploadHash is { } hash ? Convert.ToHexStringLower(hash.GetHashAndReset()) : null;
            CloseUpload();
            if (sent != _relay?.Sha256)
            {
                RemoteLog.Write("The relay's bytes do not match its digest");
                EndProcess(RemoteStatus.UploadFailed);
                return;
            }

            process.CloseInput();
            return;
        }

        _uploadHash?.AppendData(chunk, 0, count);

        process.Write(count == chunk.Length ? chunk : chunk[..count], written => Queue.Post(() =>
        {
            if (token != _processToken)
            {
                return;
            }

            if (!written)
            {
                CloseUpload();
                return;
            }

            _uploadedBytes += count;
            ReportUploadProgress(_relay?.Size ?? 0);
            UploadNextChunk(token);
        }));
    }

    private void CloseUpload()
    {
        _upload?.Dispose();
        _upload = null;
        _uploadHash?.Dispose();
        _uploadHash = null;
    }

    private void ReportUploadProgress(long total)
    {
        var fraction = total > 0 ? Math.Min(1, (double)_uploadedBytes / total) : 1;
        if (fraction - _reportedUploadFraction >= 0.05 || (fraction == 1 && _reportedUploadFraction < 1))
        {
            _reportedUploadFraction = fraction;
            Report(RemoteStatus.Installing(fraction));
        }
    }

    // Ending

    /// <summary>Stops for good: timers cancelled, the process ended, <paramref name="finalStatus"/> (if any) reported last.</summary>
    private void Halt(RemoteStatus? finalStatus = null)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        CancelTimers();
        CloseUpload();
        EndProcess();
        if (finalStatus is { } status)
        {
            Report(status);
        }
    }

    /// <summary>Ends the running process: stdin closed, then killed after 2 s. Its exit then reports <paramref name="reason"/>.</summary>
    private void EndProcess(RemoteStatus? reason = null)
    {
        if (reason is not null && _endReason is null)
        {
            _endReason = reason;
        }

        if (_process is not { } process)
        {
            return;
        }

        process.CloseInput();
        process.Terminate();
        _environment.Scheduling.Schedule(Now + KillDelay, Queue, process.Kill);
        if (_stopped)
        {
            _process = null;
        }
    }

    /// <summary>A failed attempt: the status with the time of the next one.</summary>
    private void Fail(RemoteStatus status)
    {
        CancelTimers();
        _stage = Stage.Idle;
        var failedAt = Now;
        _lastErrorAt = failedAt;
        if (status.Kind == RemoteStatusKind.InvalidDestination)
        {
            Report(status);
            return;
        }

        var delay = _backoff.Delay(status, _session.HelloAt, failedAt);
        var retryAt = failedAt + delay;
        RemoteLog.Write($"Remote host ended: {status.Code}, retry in {(int)delay.TotalSeconds} s");
        Report(status, retryAt);
        Schedule(TimerSlot.Retry, retryAt, Begin);
    }

    // Plumbing

    /// <summary>Reports a status once; a live status carries the relay's <c>hello</c> facts and the clock offset.</summary>
    private void Report(RemoteStatus status, DateTimeOffset? retryAt = null)
    {
        var value = new RemoteHostStatus(status, retryAt, _lastErrorAt);
        if (_session.HelloReceived && (status.Kind == RemoteStatusKind.Connected || status.IsSeeding))
        {
            value = value with { ClockOffsetMs = _session.Clock.OffsetMs, RelayVersion = _session.RelayVersion, RelayArch = _session.RelayArch };
        }

        if (value == _lastStatus)
        {
            return;
        }

        _lastStatus = value;
        Emit(new RemoteHostEventKind.Status(value));
    }

    private void Emit(RemoteHostEventKind kind)
    {
        if (_retiring)
        {
            return;
        }

        var hostEvent = new RemoteHostEvent(Host.Id, _attempt, kind);
        var onEvent = _onEvent;
        _eventQueue.Post(() => onEvent(hostEvent));
    }

    private void Send(byte[]? line)
    {
        if (line is not null && _process is { } process)
        {
            process.Write(line);
        }
    }

    private void Schedule(TimerSlot slot, TimeSpan after, Action work) => Schedule(slot, Now + after, work);

    private void Schedule(TimerSlot slot, DateTimeOffset deadline, Action work)
    {
        if (_timers.Remove(slot, out var previous))
        {
            previous.Cancel();
        }

        var generation = _attempt;
        IRemoteScheduledWork? scheduled = null;
        scheduled = _environment.Scheduling.Schedule(deadline, Queue, () =>
        {
            if (_stopped || _attempt != generation || !_timers.TryGetValue(slot, out var current) || current != scheduled)
            {
                return;
            }

            _timers.Remove(slot);
            work();
        });
        _timers[slot] = scheduled;
    }

    private void Cancel(TimerSlot slot)
    {
        if (_timers.Remove(slot, out var timer))
        {
            timer.Cancel();
        }
    }

    private void CancelTimers()
    {
        foreach (var timer in _timers.Values)
        {
            timer.Cancel();
        }

        _timers.Clear();
    }
}
