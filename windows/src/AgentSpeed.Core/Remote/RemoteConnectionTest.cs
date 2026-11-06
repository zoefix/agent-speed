namespace AgentSpeed.Core.Remote;

/// <summary>
/// What <i>Test Connection</i> found (DESIGN 13, Add / Edit sheet): the probe of REMOTE 4.2 step 1 succeeded on a supported
/// system (<see cref="System"/>, <see cref="Arch"/>), or <see cref="Failure"/> is the status the connection would show.
/// </summary>
public sealed record RemoteTestOutcome(string? System, RemoteArch? Arch, RemoteStatus? Failure)
{
    public bool IsSupported => Failure is null;

    public static RemoteTestOutcome Supported(string system, RemoteArch arch) => new(system, arch, null);

    public static RemoteTestOutcome Failed(RemoteStatus status) => new(null, null, status);
}

/// <summary>
/// One run of the probe command for a host that may not be saved yet: nothing is uploaded or started, and nothing on the
/// host is written. The result arrives once through the completion (on a pool thread; marshal to the UI yourself);
/// <see cref="Cancel"/> ends the <c>ssh</c> process and drops the result.
/// </summary>
public sealed class RemoteConnectionTest
{
    /// <summary>Longer than <c>ConnectTimeout</c> plus a slow login shell; a host that never answers reads as <c>unreachable</c>.</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(45);

    private readonly RemoteQueue _queue = new("remote.test");
    private readonly RemoteEnvironment _environment;
    private readonly MemoryStream _stdout = new();
    private readonly MemoryStream _stderr = new();
    private Action<RemoteTestOutcome>? _completion;
    private IRemoteProcess? _process;
    private IRemoteScheduledWork? _timer;
    private bool _timedOut;

    private RemoteConnectionTest(RemoteEnvironment environment, Action<RemoteTestOutcome> completion)
    {
        _environment = environment;
        _completion = completion;
    }

    /// <summary>Starts the probe of <paramref name="host"/> with the system <c>ssh</c>; <paramref name="appVersion"/> names the bundled relay.</summary>
    public static RemoteConnectionTest Start(RemoteHost host, string appVersion, Action<RemoteTestOutcome> completion) =>
        Start(host, RemoteEnvironment.System(appVersion), completion);

    internal static RemoteConnectionTest Start(RemoteHost host, RemoteEnvironment environment, Action<RemoteTestOutcome> completion)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(completion);
        var test = new RemoteConnectionTest(environment, completion);
        test._queue.Post(() => test.Launch(host));
        return test;
    }

    /// <summary>Ends the probe; its result is never delivered.</summary>
    public void Cancel() => _queue.Post(() =>
    {
        _completion = null;
        End();
    });

    /// <summary>How a finished probe reads: its exit and stderr as a connection classifies them, else the probe's verdict.</summary>
    internal static RemoteTestOutcome Outcome(int exitStatus, ReadOnlySpan<byte> stdout, ReadOnlySpan<byte> stderr)
    {
        if (exitStatus != 0)
        {
            return RemoteTestOutcome.Failed(RemoteStatusClassifier.Classify(exitStatus, stderr, RemoteStage.Probe));
        }

        var result = RemoteProbeResult.Parse(stdout);
        var verdict = result.Verdict;
        return verdict.Kind switch
        {
            RemoteProbeVerdictKind.Supported => RemoteTestOutcome.Supported(result.System, verdict.Arch),
            RemoteProbeVerdictKind.UnsupportedSystem => RemoteTestOutcome.Failed(RemoteStatus.UnsupportedSystem(verdict.System)),
            RemoteProbeVerdictKind.CannotVerify => RemoteTestOutcome.Failed(RemoteStatus.CannotVerify),
            _ => RemoteTestOutcome.Failed(RemoteStatus.Exited(exitStatus)),
        };
    }

    private void Launch(RemoteHost host)
    {
        if (_completion is null)
        {
            return;
        }

        var configFile = RemoteSsh.DevelopmentConfigFile(_environment.ProcessEnvironment);
        if (!host.IsConnectable || RemoteCommand.Probe(host.EffectiveRelayFolder) is not { } command
            || RemoteSsh.Arguments(host.Destination, command, configFile) is not { } arguments)
        {
            Finish(RemoteTestOutcome.Failed(RemoteStatus.InvalidDestination));
            return;
        }

        if (!_environment.Transport.IsAvailable(_environment.SshExecutable))
        {
            Finish(RemoteTestOutcome.Failed(RemoteStatus.SshMissing));
            return;
        }

        var handlers = new RemoteProcessHandlers(
            data => _queue.Post(() => Append(_stdout, data, RemoteProbeResult.MaxOutputBytes, keepTail: false)),
            data => _queue.Post(() => Append(_stderr, data, RemoteStatusClassifier.MaxStderrBytes, keepTail: true)),
            status => _queue.Post(() => Exited(status)));
        try
        {
            _process = _environment.Transport.Launch(
                new RemoteProcessRequest(_environment.SshExecutable, arguments, RemoteSsh.Environment(_environment.ProcessEnvironment)), handlers);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Finish(RemoteTestOutcome.Failed(RemoteStatus.Exited(127)));
            return;
        }

        _process.CloseInput();
        _timer = _environment.Scheduling.Schedule(_environment.Scheduling.Now + TimeLimit, _queue, () =>
        {
            if (_process is null)
            {
                return;
            }

            _timedOut = true;
            End();
        });
    }

    private static void Append(MemoryStream buffer, byte[] data, int limit, bool keepTail)
    {
        if (!keepTail)
        {
            if (buffer.Length < limit)
            {
                buffer.Write(data, 0, (int)Math.Min(data.Length, limit - buffer.Length));
            }

            return;
        }

        buffer.Write(data);
        if (buffer.Length > limit)
        {
            var kept = buffer.GetBuffer().AsSpan((int)buffer.Length - limit, limit).ToArray();
            buffer.SetLength(0);
            buffer.Write(kept);
        }
    }

    private void Exited(int status)
    {
        _process = null;
        _timer?.Cancel();
        _timer = null;
        Finish(_timedOut
            ? RemoteTestOutcome.Failed(RemoteStatus.Unreachable)
            : Outcome(status, _stdout.GetBuffer().AsSpan(0, (int)_stdout.Length), _stderr.GetBuffer().AsSpan(0, (int)_stderr.Length)));
    }

    private void End()
    {
        _timer?.Cancel();
        _timer = null;
        if (_process is not { } process)
        {
            return;
        }

        process.CloseInput();
        process.Terminate();
        _environment.Scheduling.Schedule(_environment.Scheduling.Now + RemoteConnection.KillDelay, _queue, process.Kill);
    }

    private void Finish(RemoteTestOutcome outcome)
    {
        if (_completion is not { } completion)
        {
            return;
        }

        _completion = null;
        completion(outcome);
    }
}
