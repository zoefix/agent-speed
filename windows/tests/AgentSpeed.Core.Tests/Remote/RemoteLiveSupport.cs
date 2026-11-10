using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>A wall clock and timers the test advances by hand (the macOS <c>ManualScheduling</c>).</summary>
internal sealed class ManualScheduling(DateTimeOffset start) : IRemoteScheduling
{
    private readonly Lock _lock = new();
    private readonly List<Item> _items = [];
    private DateTimeOffset _now = start;

    public ManualScheduling()
        : this(DateTimeOffset.FromUnixTimeSeconds(1_780_000_000))
    {
    }

    public DateTimeOffset Now
    {
        get
        {
            lock (_lock)
            {
                return _now;
            }
        }
    }

    public IReadOnlyList<DateTimeOffset> Deadlines
    {
        get
        {
            lock (_lock)
            {
                return [.. _items.Where(i => !i.Cancelled).Select(i => i.Deadline).Order()];
            }
        }
    }

    public IRemoteScheduledWork Schedule(DateTimeOffset deadline, RemoteQueue queue, Action work)
    {
        var item = new Item(deadline, queue, work);
        lock (_lock)
        {
            _items.Add(item);
        }

        return item;
    }

    public void Advance(TimeSpan by)
    {
        var target = Now + by;
        while (true)
        {
            Item? next;
            lock (_lock)
            {
                _items.RemoveAll(i => i.Cancelled);
                next = _items.Where(i => i.Deadline <= target).OrderBy(i => i.Deadline).FirstOrDefault();
                if (next is null)
                {
                    _now = _now > target ? _now : target;
                    return;
                }

                _items.Remove(next);
                _now = _now > next.Deadline ? _now : next.Deadline;
            }

            next.Queue.Sync(() =>
            {
                if (!next.Cancelled)
                {
                    next.Work();
                }
            });
        }
    }

    private sealed class Item(DateTimeOffset deadline, RemoteQueue queue, Action work) : IRemoteScheduledWork
    {
        private volatile bool _cancelled;

        public DateTimeOffset Deadline { get; } = deadline;

        public RemoteQueue Queue { get; } = queue;

        public Action Work { get; } = work;

        public bool Cancelled => _cancelled;

        public void Cancel() => _cancelled = true;
    }
}

/// <summary>A scripted process: the test sends its stdout and stderr and decides when it ends.</summary>
internal sealed class FakeProcess(RemoteProcessRequest request, RemoteProcessHandlers handlers) : IRemoteProcess
{
    private readonly Lock _lock = new();
    private readonly MemoryStream _input = new();
    private bool _inputClosed;
    private bool _terminated;
    private bool _killed;
    private bool _ended;

    public RemoteProcessRequest Request { get; } = request;

    public Action<FakeProcess>? OnInputClosed { get; set; }

    public Action<FakeProcess, byte[]>? OnWrite { get; set; }

    /// <summary>The status a terminate ends it with; null leaves it running (it then needs the kill).</summary>
    public int? ExitOnTerminate { get; set; } = 143;

    public string RemoteCommand => Request.Arguments.Count > 0 ? Request.Arguments[^1] : string.Empty;

    public bool IsProbe => RemoteCommand.Contains(RemoteScripts.Probe, StringComparison.Ordinal);

    public bool IsUpload => RemoteCommand.Contains(RemoteScripts.Upload, StringComparison.Ordinal);

    public bool IsRun => RemoteCommand.Contains(RemoteScripts.Run, StringComparison.Ordinal);

    public byte[] WrittenData
    {
        get
        {
            lock (_lock)
            {
                return _input.ToArray();
            }
        }
    }

    public bool IsInputClosed
    {
        get
        {
            lock (_lock)
            {
                return _inputClosed;
            }
        }
    }

    public bool WasTerminated
    {
        get
        {
            lock (_lock)
            {
                return _terminated;
            }
        }
    }

    public bool WasKilled
    {
        get
        {
            lock (_lock)
            {
                return _killed;
            }
        }
    }

    public List<JsonElement> WrittenLines => [.. Encoding.UTF8.GetString(WrittenData).Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonDocument.Parse(line).RootElement.Clone())];

    public List<string> WrittenTypes => [.. WrittenLines.Select(l => l.GetProperty("t").GetString()!)];

    public void Write(byte[] data, Action<bool>? completion = null)
    {
        bool accepted;
        lock (_lock)
        {
            accepted = !_inputClosed;
            if (accepted)
            {
                _input.Write(data);
            }
        }

        if (accepted)
        {
            OnWrite?.Invoke(this, data);
        }

        completion?.Invoke(accepted);
    }

    public void CloseInput()
    {
        bool first;
        lock (_lock)
        {
            first = !_inputClosed;
            _inputClosed = true;
        }

        if (first)
        {
            OnInputClosed?.Invoke(this);
        }
    }

    public void Terminate()
    {
        lock (_lock)
        {
            _terminated = true;
        }

        if (ExitOnTerminate is { } status)
        {
            Finish(status);
        }
    }

    public void Kill()
    {
        lock (_lock)
        {
            _killed = true;
        }

        Finish(137);
    }

    public void Send(string line) => handlers.Stdout(Encoding.UTF8.GetBytes(line + "\n"));

    public void SendStdout(string text) => handlers.Stdout(Encoding.UTF8.GetBytes(text));

    public void SendStderr(string text) => handlers.Stderr(Encoding.UTF8.GetBytes(text));

    public void Finish(int status)
    {
        bool first;
        lock (_lock)
        {
            first = !_ended;
            _ended = true;
        }

        if (first)
        {
            handlers.Exit(status);
        }
    }
}

internal sealed class FakeTransport : IRemoteTransport
{
    private readonly Lock _lock = new();
    private readonly List<FakeProcess> _processes = [];
    private bool _available = true;

    public Action<FakeProcess>? Script { get; set; }

    public List<FakeProcess> Launched
    {
        get
        {
            lock (_lock)
            {
                return [.. _processes];
            }
        }
    }

    public List<FakeProcess> Runs => [.. Launched.Where(p => p.IsRun)];

    public bool IsAvailable(string executable)
    {
        lock (_lock)
        {
            return _available;
        }
    }

    public void SetAvailable(bool available)
    {
        lock (_lock)
        {
            _available = available;
        }
    }

    public IRemoteProcess Launch(RemoteProcessRequest request, RemoteProcessHandlers handlers)
    {
        var process = new FakeProcess(request, handlers);
        lock (_lock)
        {
            _processes.Add(process);
        }

        Script?.Invoke(process);
        return process;
    }
}

/// <summary>Every event a connection or hub reported, in order.</summary>
internal sealed class RemoteEventLog
{
    private readonly Lock _lock = new();
    private readonly List<RemoteHostEvent> _recorded = [];

    public RemoteQueue Queue { get; } = new("test.remote.events");

    public Action<RemoteHostEvent> Handler => hostEvent =>
    {
        lock (_lock)
        {
            _recorded.Add(hostEvent);
        }
    };

    public List<RemoteHostEvent> Events
    {
        get
        {
            Queue.Sync(() => { });
            lock (_lock)
            {
                return [.. _recorded];
            }
        }
    }

    public List<RemoteStatus> Statuses(string? hostId = null) =>
        [.. Events.Where(e => hostId is null || e.HostId == hostId).Select(e => e.Kind).OfType<RemoteHostEventKind.Status>().Select(s => s.Value.Status)];

    public RemoteHostStatus? LastStatus => Events.Select(e => e.Kind).OfType<RemoteHostEventKind.Status>().LastOrDefault()?.Value;

    public List<(RelayMessage Message, bool Seeded)> Messages =>
        [.. Events.Select(e => e.Kind).OfType<RemoteHostEventKind.Message>().Select(m => (m.Value, m.Seeded))];

    public int HistoryDueCount(string? hostId = null) =>
        Events.Count(e => (hostId is null || e.HostId == hostId) && e.Kind is RemoteHostEventKind.HistoryDue);

    public void Clear()
    {
        Queue.Sync(() => { });
        lock (_lock)
        {
            _recorded.Clear();
        }
    }
}

/// <summary>A connection with a scripted relay: the probe and upload answer by themselves; the run is driven by the test.</summary>
internal sealed class ConnectionHarness : IDisposable
{
    public const string HostId = RelayLines.HostId;

    private long _remoteOffsetMs;

    public ConnectionHarness(
        string destination = "devbox",
        bool installed = true,
        bool answersPings = false,
        DateTimeOffset? start = null,
        SourceSet? sources = null,
        Action<FakeProcess, FakeRelayBundle>? probe = null,
        Action<FakeProcess, FakeRelayBundle>? upload = null,
        RemoteRelayBundle? bundle = null,
        RemoteQuotaConfig? quota = null)
    {
        Scheduling = new ManualScheduling(start ?? DateTimeOffset.FromUnixTimeSeconds(1_780_000_000));
        Relays = new FakeRelayBundle();
        Environment = new RemoteEnvironment(Transport, bundle ?? Relays.Bundle, Scheduling, static () => TimeZoneInfo.Utc,
            new Dictionary<string, string> { ["SystemRoot"] = @"C:\Windows", ["HOME"] = "/Users/me" });
        var prices = RemotePriceTableJson.From("""{"schema":3}"""u8)!;
        Connection = new RemoteConnection(new RemoteHost(destination, HostId),
            new RemoteSessionSettings(sources ?? SourceSet.Of(SourceKind.Codex).With(SourceKind.ClaudeCode), prices, Quota: quota), Environment,
            Events.Queue, Events.Handler);
        var relays = Relays;
        Transport.Script = process =>
        {
            if (process.IsProbe)
            {
                if (probe is not null)
                {
                    probe(process, relays);
                }
                else
                {
                    process.SendStdout(ProbeOutput(relays, installed));
                    process.Finish(0);
                }
            }
            else if (process.IsUpload)
            {
                process.OnInputClosed = p =>
                {
                    if (upload is not null)
                    {
                        upload(p, relays);
                    }
                    else
                    {
                        p.SendStdout($"hash {TestBytes.Sha256(p.WrittenData)}\n");
                        p.Finish(0);
                    }
                };
            }
            else if (process.IsRun && answersPings)
            {
                process.OnWrite = (p, data) =>
                {
                    using var line = JsonDocument.Parse(data.AsMemory(0, data.Length - 1));
                    if (line.RootElement.GetProperty("t").GetString() == "ping")
                    {
                        p.Send(RelayLines.Clock(line.RootElement.GetProperty("id").GetInt64(), Scheduling.Now.ToUnixTimeMilliseconds() + RemoteOffsetMs));
                    }
                };
            }
        };
    }

    public ManualScheduling Scheduling { get; }

    public FakeTransport Transport { get; } = new();

    public FakeRelayBundle Relays { get; }

    public RemoteEventLog Events { get; } = new();

    public RemoteEnvironment Environment { get; }

    public RemoteConnection Connection { get; }

    /// <summary>The relay's clock offset to the manual clock, in milliseconds.</summary>
    public long RemoteOffsetMs
    {
        get => Interlocked.Read(ref _remoteOffsetMs);
        set => Interlocked.Exchange(ref _remoteOffsetMs, value);
    }

    public long NowMs => Scheduling.Now.ToUnixTimeMilliseconds();

    public FakeProcess? Run => Transport.Runs.LastOrDefault();

    public List<double> RetryDelays => [.. Events.Events.Select(e => e.Kind).OfType<RemoteHostEventKind.Status>()
        .Where(s => s.Value.RetryAt is not null && s.Value.LastErrorAt is not null)
        .Select(s => (s.Value.RetryAt!.Value - s.Value.LastErrorAt!.Value).TotalSeconds)];

    public static string ProbeOutput(FakeRelayBundle relays, bool installed, string machine = "x86_64") =>
        $"system Linux {machine}\ntool sha256sum\n" + (installed ? $"hash {relays.X86.Sha256} {relays.X86.InstalledName}\n" : string.Empty) + "end\n";

    /// <summary>Waits until the connection's queue and the event queue are idle (every callback hops through them).</summary>
    public void Settle()
    {
        for (var i = 0; i < 64; i++)
        {
            Connection.Queue.Sync(() => { });
            Events.Queue.Sync(() => { });
        }
    }

    public void Start()
    {
        Connection.Start();
        Settle();
    }

    public void Advance(double seconds)
    {
        Settle();
        Scheduling.Advance(TimeSpan.FromSeconds(seconds));
        Settle();
    }

    /// <summary>Starts the connection and the relay's <c>hello</c> (and <c>ready</c>).</summary>
    public FakeProcess Connect(bool ready = true)
    {
        Start();
        var run = Run ?? throw new InvalidOperationException("no run");
        run.Send(RelayLines.Hello(NowMs + RemoteOffsetMs));
        if (ready)
        {
            run.Send(RelayLines.Ready);
        }

        Settle();
        return run;
    }

    public void Dispose()
    {
        Connection.Stop();
        Relays.Dispose();
    }
}
