using System.Diagnostics;
using AgentSpeed.Core.Engine;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.OpenCode;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Antigravity;

namespace AgentSpeed.Core.Monitoring;

internal interface IResponseObserver
{
    void OnResponse(ParsedResponse response, bool isCompletion, bool seeded);
}

internal sealed record SeedReport(long ElapsedMs, int FilesDiscovered, int FilesRead, long BytesRead, int Responses);

/// <summary>
/// Owns discovery, tailing, parsing and the engine for one configuration (METRICS 2 and 5). Single-threaded:
/// every public member must be called from the monitor thread; only seeding fans file reads out to the pool.
/// </summary>
internal sealed class MonitorCore : IDisposable
{
    public const long BacklogWindowMs = 15 * 60_000;
    public const long LargeFileBytes = 256L * 1024 * 1024;
    public const long LargeFileTailBytes = 64L * 1024 * 1024;
    public const long RecentWriteWindowMs = 2 * 60_000;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly IReadOnlySet<SourceKind> _enabled;
    private readonly IResponseObserver? _observer;
    private readonly Deduper _deduper = new();
    private readonly SpeedEngine _engine;
    private readonly Dictionary<string, TrackedFile> _files = new(PathComparer);
    private readonly Dictionary<string, AgentLink> _links = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parents = new(StringComparer.Ordinal);
    private readonly Collector _collector = new();
    private readonly List<SourceRoot> _roots = [];
    private readonly Dictionary<TrackedFile, BackgroundRead> _wholeReads = [];
    private readonly CancellationTokenSource _backgroundStop = new();
    private readonly OpenCodeSource? _openCode;
    private readonly List<AntigravitySource> _antigravity = [];
    private readonly Dictionary<SourceKind, string> _antigravityRoots;
    private bool _openCodeSeeded;
    private bool _linksChanged;
    private long _removedMalformedLines;
    private long _removedOversizedLines;
    private int _responsesSeen;
    private bool _partialHistory;

    public MonitorCore(
        MonitorConfiguration configuration,
        string codexRoot,
        string claudeRoot,
        string dshRoot,
        long todayStartMs,
        IResponseObserver? observer = null)
        : this(configuration, new SourceLocations(codexRoot, claudeRoot, dshRoot, string.Empty, []), todayStartMs, observer)
    {
    }

    public MonitorCore(
        MonitorConfiguration configuration,
        SourceLocations locations,
        long todayStartMs,
        IResponseObserver? observer = null)
    {
        _enabled = configuration.EnabledSources;
        _observer = observer;
        _engine = new SpeedEngine(todayStartMs);
        foreach (var (family, path) in locations.FileRoots())
        {
            _roots.Add(new SourceRoot(family, path, Families.IsEnabled(family, _enabled)));
        }

        CodexRoot = _roots.First(r => r.Family == RootFamily.Codex);
        ClaudeRoot = _roots.First(r => r.Family == RootFamily.Claude);
        DshRoot = _roots.First(r => r.Family == RootFamily.DeepSeek);
        if (Families.IsEnabled(RootFamily.OpenCode, _enabled) && locations.OpenCodeRoot.Length > 0)
        {
            _openCode = new OpenCodeSource(locations.OpenCodeRoot, _deduper);
            OpenCodeRoot = new SourceRoot(RootFamily.OpenCode, _openCode.WatchDirectory, enabled: true);
        }

        OpenCodeRootPath = locations.OpenCodeRoot;
        _antigravityRoots = locations.AntigravityRoots().ToDictionary(r => r.Kind, r => r.Path);
        foreach (var (kind, path) in locations.AntigravityRoots())
        {
            if (_enabled.Contains(kind) && path.Length > 0) _antigravity.Add(new AntigravitySource(kind, path));
        }
        RefreshAvailability();
    }

    public SourceRoot CodexRoot { get; }

    public SourceRoot ClaudeRoot { get; }

    public SourceRoot DshRoot { get; }

    /// <summary>The folder watched for the OpenCode database; null when OpenCode is disabled.</summary>
    public SourceRoot? OpenCodeRoot { get; }

    public string OpenCodeRootPath { get; }

    internal OpenCodeSource? OpenCode => _openCode;

    /// <summary>
    /// While running, whole-file reads (<c>.jsonl.zst</c>, legacy Gemini <c>.json</c>) happen on the pool and are applied
    /// later by <see cref="ApplyCompletedReads"/>, so the live loop never waits for them (METRICS 5.3).
    /// </summary>
    public bool ReadWholeFilesInBackground { get; set; }

    /// <summary>Raised on a pool thread when a background read finished.</summary>
    public Action? BackgroundReadCompleted { get; set; }

    /// <summary>Runs on the pool thread right before a background read (tests use it to make a read slow).</summary>
    internal Action<string>? BeforeBackgroundRead { get; set; }

    public int PendingReadCount => _wholeReads.Count;

    /// <summary>Transcript roots (file families).</summary>
    public IReadOnlyList<SourceRoot> Roots => _roots;

    /// <summary>Every folder a file watcher should observe, including the OpenCode database folder.</summary>
    public IEnumerable<SourceRoot> WatchRoots => OpenCodeRoot is { } openCode ? _roots.Append(openCode) : _roots;

    public bool AnyEnabledRootAvailable => _roots.Any(r => r.Enabled && r.Available) || OpenCodeRoot?.Available == true
        || _antigravity.Any(s => s.Available);

    public int TrackedFileCount => _files.Count;

    public void RefreshAvailability()
    {
        foreach (var root in _roots)
        {
            root.Available = SafeDirectoryExists(root.Path);
        }

        if (_openCode is not null)
        {
            OpenCodeRoot!.Available = _openCode.CheckExists();
        }
    }

    public void Dispose()
    {
        _backgroundStop.Cancel();
        if (_wholeReads.Count > 0)
        {
            try
            {
                Task.WaitAll([.. _wholeReads.Values.Select(r => r.Task)], TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }

        _wholeReads.Clear();
        _openCode?.Dispose();
    }

    /// <summary>Applies whole-file reads that finished in the background, each in one step; true when anything changed.</summary>
    public bool ApplyCompletedReads()
    {
        if (_wholeReads.Count == 0)
        {
            return false;
        }

        var changed = false;
        foreach (var (tracked, read) in _wholeReads.Where(r => r.Value.Task.IsCompleted).ToList())
        {
            _wholeReads.Remove(tracked);
            if (!_files.TryGetValue(tracked.Path, out var current) || !ReferenceEquals(current, tracked))
            {
                continue;
            }

            try
            {
                var prepared = read.Task.GetAwaiter().GetResult();
                changed |= TryCommit(tracked, prepared, seeded: false)
                    && (prepared.Result.BytesRead > 0 || prepared.Result.WasReset);
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            catch (Exception e)
            {
                HandleReadFailure(tracked, e);
                changed = true;
                continue;
            }

            if (read.Again)
            {
                StartBackgroundRead(tracked);
            }
        }

        return changed;
    }

    /// <summary>Reads the startup backlog of every enabled, available root (METRICS 5).</summary>
    public SeedReport Seed(long nowMs, long todayStartMs, CancellationToken cancellationToken, Action<double>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        RefreshAntigravity(todayStartMs, seeded: true);
        var plans = new List<ReadPlan>();
        var discovered = 0;
        foreach (var root in _roots)
        {
            if (!root.Enabled || root.Seeded)
            {
                continue;
            }

            root.Available = SafeDirectoryExists(root.Path);
            if (!root.Available)
            {
                continue;
            }

            var files = TranscriptDiscovery.Enumerate(root.Path, root.Naming, cancellationToken);
            discovered += files.Count;
            foreach (var file in files)
            {
                if (_files.ContainsKey(file.Path))
                {
                    continue;
                }

                var tracked = new TrackedFile(root, file.Path, _deduper);
                _files[file.Path] = tracked;
                if (PlanInitialRead(tracked, file, todayStartMs) is { } plan)
                {
                    plans.Add(plan);
                }
            }

            root.Seeded = true;
        }

        var (filesRead, bytesRead) = ReadPlans(plans, seeded: true, cancellationToken, progress);
        if (_openCode is { } openCode && !_openCodeSeeded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCodeRoot!.Available = openCode.CheckExists();
            if (OpenCodeRoot.Available)
            {
                discovered++;
                openCode.RefreshAvailability();
                ApplyOpenCode(openCode.Seed(todayStartMs), seeded: true);
                if (openCode.Status == OpenCodeStatus.Ready)
                {
                    filesRead++;
                    bytesRead += openCode.DatabaseBytes;
                }
            }

            _openCodeSeeded = true;
        }

        return new SeedReport(stopwatch.ElapsedMilliseconds, discovered, filesRead, bytesRead, _responsesSeen);
    }

    /// <summary>Handles paths reported by a watcher. Returns true when anything was read.</summary>
    public bool ProcessPaths(IEnumerable<string> paths, long nowMs, long todayStartMs)
    {
        var changed = false;
        var openCodeChanged = false;
        foreach (var path in paths)
        {
            if (_openCode?.IsWatchedPath(path) == true)
            {
                openCodeChanged = true;
                continue;
            }

            if (_files.TryGetValue(path, out var tracked))
            {
                if (!SafeFileExists(path))
                {
                    Remove(tracked);
                    changed = true;
                }
                else
                {
                    changed |= ReadLive(tracked);
                }

                continue;
            }

            foreach (var root in _roots)
            {
                if (root.Enabled && root.Seeded && TranscriptDiscovery.IsAcceptablePath(root.Path, path, root.Naming))
                {
                    changed |= AddRuntimeFile(root, path, todayStartMs);
                    break;
                }
            }
        }

        if (openCodeChanged && _openCodeSeeded && _openCode is { } openCode)
        {
            changed |= ApplyOpenCode(openCode.Refresh(), seeded: false);
        }

        return changed;
    }

    /// <summary>1-second fallback: stats files modified in the last 2 minutes.</summary>
    public bool PollRecent(long nowMs)
    {
        var antigravityChanged = RefreshAntigravity(_antigravityTodayStart, seeded: false);
        var cutoff = DateTime.UnixEpoch.AddMilliseconds(nowMs - RecentWriteWindowMs);
        var changed = (_openCodeSeeded && _openCode?.RefreshIfChanged() is { } update && ApplyOpenCode(update, seeded: false)) || antigravityChanged;
        List<TrackedFile>? due = null;
        foreach (var tracked in _files.Values)
        {
            if (tracked.KnownWriteUtc >= cutoff)
            {
                (due ??= []).Add(tracked);
            }
        }

        if (due is null)
        {
            return changed;
        }

        foreach (var tracked in due)
        {
            var info = new FileInfo(tracked.Path);
            if (!info.Exists)
            {
                Remove(tracked);
                changed = true;
            }
            else if (info.Length != tracked.KnownLength || info.LastWriteTimeUtc != tracked.KnownWriteUtc)
            {
                changed |= ReadLive(tracked);
            }
        }

        return changed;
    }

    /// <summary>10-second full rediscovery; also seeds roots that appeared after startup.</summary>
    public bool Rediscover(long nowMs, long todayStartMs, CancellationToken cancellationToken)
    {
        var changed = false;
        if (_openCode is { } openCode && _openCodeSeeded)
        {
            var available = openCode.CheckExists();
            changed |= available != OpenCodeRoot!.Available;
            OpenCodeRoot.Available = available;
            var update = openCode.Rediscover(nowMs, todayStartMs);
            changed |= ApplyOpenCode(update, seeded: update.Reset);
        }

        foreach (var root in _roots)
        {
            if (!root.Enabled)
            {
                continue;
            }

            var available = SafeDirectoryExists(root.Path);
            changed |= available != root.Available;
            root.Available = available;
            if (!available)
            {
                continue;
            }

            if (!root.Seeded)
            {
                Seed(nowMs, todayStartMs, cancellationToken);
                changed = true;
                continue;
            }

            var seen = new HashSet<string>(PathComparer);
            foreach (var file in TranscriptDiscovery.Enumerate(root.Path, root.Naming, cancellationToken))
            {
                seen.Add(file.Path);
                if (!_files.TryGetValue(file.Path, out var tracked))
                {
                    changed |= AddRuntimeFile(root, file.Path, todayStartMs, file);
                }
                else if (file.Length != tracked.KnownLength || file.LastWriteUtc != tracked.KnownWriteUtc)
                {
                    changed |= ReadLive(tracked);
                }
            }

            foreach (var tracked in _files.Values.Where(f => f.Root == root && !seen.Contains(f.Path)).ToList())
            {
                Remove(tracked);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Files of that source with at least one line today.</summary>
    public int SessionCount(SourceKind kind, long todayStartMs)
    {
        if (kind is SourceKind.Antigravity or SourceKind.AntigravityCli)
            return _antigravity.FirstOrDefault(s => s.Kind == kind)?.SessionCount(todayStartMs) ?? 0;
        if (kind == SourceKind.OpenCode)
        {
            return _openCode?.ActiveSessionCount(todayStartMs) ?? 0;
        }

        var count = 0;
        foreach (var tracked in _files.Values)
        {
            if (tracked.Parser.Source == kind && tracked.Parser.LastLineMs >= todayStartMs)
            {
                count++;
            }
        }

        return count;
    }

    public IReadOnlyList<MonitorWarning> Warnings(int unfinalizedToday = 0)
    {
        var warnings = new List<MonitorWarning>();
        if (_partialHistory)
        {
            warnings.Add(new MonitorWarning.PartialHistory());
        }

        var unreadable = UnreadableFileCount;
        if (unreadable > 0)
        {
            warnings.Add(new MonitorWarning.UnreadableFiles(unreadable));
        }

        var malformed = MalformedLineCount;
        if (malformed > 0)
        {
            warnings.Add(new MonitorWarning.MalformedLines((int)Math.Min(malformed, int.MaxValue)));
        }

        if (unfinalizedToday > 0)
        {
            warnings.Add(new MonitorWarning.UnfinalizedUsage(unfinalizedToday));
        }

        if (_openCode?.Status == OpenCodeStatus.Unsupported)
        {
            warnings.Add(new MonitorWarning.UnsupportedFormat(SourceKind.OpenCode));
        }

        foreach (var source in _antigravity.Where(s => s.Unsupported))
            warnings.Add(new MonitorWarning.UnsupportedFormat(source.Kind));

        return warnings;
    }

    public long MalformedLineCount =>
        _removedMalformedLines + _files.Values.Sum(f => f.Parser.MalformedLines) + (_openCode?.MalformedRows ?? 0) + _antigravity.Sum(s => s.Malformed);

    public long OversizedLineCount => _removedOversizedLines + _files.Values.Sum(f => f.Tailer.OversizedLines);

    public int UnreadableFileCount =>
        _files.Values.Count(f => f.Unreadable) + (_openCode?.Status == OpenCodeStatus.Unreadable ? 1 : 0) + _antigravity.Sum(s => s.Unreadable);

    public bool PartialHistory => _partialHistory;

    public (SpeedSnapshot Snapshot, IReadOnlyList<SpeedEvent> Events) BuildSnapshot(
        long nowMs, long todayStartMs, MonitorState state, bool observeLive)
    {
        _antigravityTodayStart = todayStartMs;
        ResolveParents();
        var frame = _engine.Compute(nowMs, todayStartMs, observeLive);
        var agents = frame.Agents
            .Select(a => new AgentStatus(
                a.Info.Id,
                a.Info.Source,
                a.Info.Project ?? string.Empty,
                a.Info.Model,
                a.Phase,
                a.Info.IsSubagent,
                a.Live,
                a.RecentSpeed,
                DateTimeOffset.FromUnixTimeMilliseconds(a.Info.LastLineMs ?? nowMs),
                a.ParentId))
            .ToList();

        var sources = Enum.GetValues<SourceKind>()
            .Select(kind => BuildSourceStatus(kind, todayStartMs, frame.SourceLive[(int)kind],
                frame.SourceToday[(int)kind], agents.Count(a => a.Source == kind && a.Phase == AgentPhase.Generating)))
            .ToList();

        var snapshot = new SpeedSnapshot(
            DateTimeOffset.FromUnixTimeMilliseconds(nowMs),
            state,
            frame.Live,
            frame.EstimatedShare,
            frame.PeakToday,
            frame.PeakTodayAtMs is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null,
            frame.AverageToday,
            frame.Today,
            sources,
            agents,
            frame.GeneratingCount,
            frame.ToolsCount,
            frame.History,
            frame.LastResponse,
            Warnings(frame.UnfinalizedToday));
        return (snapshot, frame.Events);
    }

    /// <summary>Snapshot while seeding: no measurements yet, only the state and the sources.</summary>
    public SpeedSnapshot BuildStartingSnapshot(long nowMs, long todayStartMs, double progress)
    {
        var sources = Enum.GetValues<SourceKind>()
            .Select(kind => BuildSourceStatus(kind, todayStartMs, Rate.Zero, TokenTotals.Zero, 0))
            .ToList();
        return SpeedSnapshot.Empty with
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(nowMs),
            State = MonitorState.Starting(Math.Clamp(progress, 0, 1)),
            Sources = sources,
        };
    }

    private SourceStatus BuildSourceStatus(SourceKind kind, long todayStartMs, Rate live, TokenTotals today, int activeAgents)
    {
        var enabled = _enabled.Contains(kind);
        string path;
        bool available, rootEnabled;
        if (kind is SourceKind.Antigravity or SourceKind.AntigravityCli)
        {
            var source = _antigravity.FirstOrDefault(s => s.Kind == kind);
            path = _antigravityRoots.GetValueOrDefault(kind, string.Empty);
            available = Directory.Exists(path);
            rootEnabled = source is not null;
        }
        else if (kind == SourceKind.OpenCode)
        {
            path = OpenCodeRootPath;
            available = OpenCodeRoot?.Available ?? SafeFileExists(OpenCodeDatabase.DatabasePath(OpenCodeRootPath));
            rootEnabled = OpenCodeRoot is not null;
        }
        else
        {
            var family = Families.Of(kind);
            var roots = _roots.Where(r => r.Family == family).ToList();
            path = roots.Count > 0 ? roots[0].Path : string.Empty;
            available = roots.Any(r => r.Available);
            rootEnabled = roots.Any(r => r.Enabled);
        }

        return new SourceStatus(
            kind,
            enabled,
            path,
            available,
            rootEnabled ? SessionCount(kind, todayStartMs) : 0,
            enabled ? live : Rate.Zero,
            enabled ? today : TokenTotals.Zero,
            enabled ? activeAgents : 0);
    }

    private ReadPlan? PlanInitialRead(TrackedFile tracked, DiscoveredFile file, long todayStartMs)
    {
        tracked.KnownLength = file.Length;
        tracked.KnownWriteUtc = file.LastWriteUtc;
        var modifiedMs = new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        if (modifiedMs < todayStartMs - BacklogWindowMs)
        {
            StartAtEnd(tracked, file);
            return null;
        }

        // A frame stream cannot be entered in the middle (METRICS 5.1).
        if (file.Length > LargeFileBytes && tracked.Tailer is ZstdFrameTailer)
        {
            _partialHistory = true;
            tracked.Tailer.StartAt(file.Length);
            return null;
        }

        if (file.Length > DocumentTailer.MaxBytes && tracked.Tailer is DocumentTailer)
        {
            _partialHistory = true;
            StartAtEnd(tracked, file);
            return null;
        }

        if (file.Length > LargeFileBytes && tracked.Tailer is FileTailer)
        {
            _partialHistory = true;
            return new ReadPlan(
                tracked, file.LastWriteUtc, file.Length - LargeFileTailBytes, StartAlignment.SkipToNextLine, file.Length);
        }

        return new ReadPlan(tracked, file.LastWriteUtc, 0, StartAlignment.None, file.Length);
    }

    private static void StartAtEnd(TrackedFile tracked, DiscoveredFile file)
    {
        if (tracked.Tailer is WholeFileTailer whole)
        {
            whole.StartAtEnd(file.Length, file.LastWriteUtc);
        }
        else
        {
            tracked.Tailer.StartAt(file.Length, StartAlignment.SkipPartialLine);
        }
    }

    private (int Files, long Bytes) ReadPlans(
        List<ReadPlan> plans, bool seeded, CancellationToken cancellationToken, Action<double>? progress)
    {
        plans.Sort(static (a, b) =>
        {
            var byTime = a.LastWriteUtc.CompareTo(b.LastWriteUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.File.Path, b.File.Path);
        });

        var totalBytes = Math.Max(1, plans.Sum(p => Math.Max(0, p.ExpectedLength - p.StartOffset)));
        var degree = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        var inFlight = new Queue<(ReadPlan Plan, Task<Prepared> Task)>();
        long doneBytes = 0, bytesRead = 0;
        var filesRead = 0;
        var next = 0;
        try
        {
            while (next < plans.Count || inFlight.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (next < plans.Count && inFlight.Count < degree)
                {
                    var plan = plans[next++];
                    plan.File.Tailer.StartAt(plan.StartOffset, plan.Align);
                    inFlight.Enqueue((plan, Task.Run(() => Prepare(plan.File, cancellationToken), cancellationToken)));
                }

                var (current, task) = inFlight.Dequeue();
                Prepared prepared;
                try
                {
                    prepared = task.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    HandleReadFailure(current.File, e);
                    continue;
                }

                if (!TryCommit(current.File, prepared, seeded))
                {
                    continue;
                }

                filesRead++;
                bytesRead += prepared.Result.BytesRead;
                doneBytes += Math.Max(0, current.ExpectedLength - current.StartOffset);
                progress?.Invoke((double)doneBytes / totalBytes);
            }
        }
        finally
        {
            if (inFlight.Count > 0)
            {
                try
                {
                    Task.WaitAll([.. inFlight.Select(i => i.Task)], TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                }
            }
        }

        return (filesRead, bytesRead);
    }

    private static Prepared Prepare(TrackedFile tracked, CancellationToken cancellationToken)
    {
        var batch = tracked.Parser.NewBatch();
        var result = tracked.Tailer.ReadAppended(batch, cancellationToken);
        return new Prepared(batch, result);
    }

    private bool TryCommit(TrackedFile tracked, Prepared prepared, bool seeded)
    {
        try
        {
            Commit(tracked, prepared, seeded);
            return true;
        }
        catch (Exception)
        {
            tracked.Unreadable = true;
            _collector.Items.Clear();
            return false;
        }
    }

    private void Commit(TrackedFile tracked, Prepared prepared, bool seeded)
    {
        if (prepared.Result.WasReset)
        {
            tracked.Parser.Reset();
        }

        _collector.Items.Clear();
        prepared.Batch.ApplyTo(_collector);
        _partialHistory |= tracked.Tailer is DocumentTailer { Oversized: true };
        tracked.KnownLength = prepared.Result.Length;
        tracked.KnownWriteUtc = prepared.Result.LastWriteUtc;
        tracked.Unreadable = false;
        PushAgent(tracked);
        foreach (var (response, isCompletion) in _collector.Items)
        {
            _responsesSeen++;
            if (_enabled.Contains(response.Source))
            {
                _engine.Upsert(response, isCompletion, seeded);
            }

            _observer?.OnResponse(response, isCompletion, seeded);
        }

        _collector.Items.Clear();
    }

    private bool ReadLive(TrackedFile tracked)
    {
        if (ReadWholeFilesInBackground && tracked.Tailer is WholeFileTailer)
        {
            StartBackgroundRead(tracked);
            return false;
        }

        try
        {
            var prepared = Prepare(tracked, CancellationToken.None);
            return TryCommit(tracked, prepared, seeded: false)
                && (prepared.Result.BytesRead > 0 || prepared.Result.WasReset);
        }
        catch (Exception e)
        {
            HandleReadFailure(tracked, e);
            return false;
        }
    }

    private void StartBackgroundRead(TrackedFile tracked)
    {
        if (_wholeReads.TryGetValue(tracked, out var running))
        {
            running.Again = true;
            return;
        }

        var token = _backgroundStop.Token;
        var before = BeforeBackgroundRead;
        var task = Task.Run(() =>
        {
            before?.Invoke(tracked.Path);
            return Prepare(tracked, token);
        }, token);
        _wholeReads[tracked] = new BackgroundRead(task);
        task.ContinueWith(
            static (_, state) => ((Action?)state)?.Invoke(),
            BackgroundReadCompleted,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool AddRuntimeFile(SourceRoot root, string path, long todayStartMs, DiscoveredFile? known = null)
    {
        DiscoveredFile file;
        if (known is { } discovered)
        {
            file = discovered;
        }
        else
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            file = new DiscoveredFile(info.FullName, info.Length, info.LastWriteTimeUtc);
        }

        if (_files.ContainsKey(file.Path))
        {
            return false;
        }

        var tracked = new TrackedFile(root, file.Path, _deduper);
        _files[file.Path] = tracked;
        if (PlanInitialRead(tracked, file, todayStartMs) is not { } plan)
        {
            return false;
        }

        tracked.Tailer.StartAt(plan.StartOffset, plan.Align);
        return ReadLive(tracked);
    }

    private void PushAgent(TrackedFile tracked)
    {
        var parser = tracked.Parser;
        if (parser.Version == tracked.PushedVersion)
        {
            return;
        }

        tracked.PushedVersion = parser.Version;
        if (parser.LastLineMs is null)
        {
            return;
        }

        if (!_enabled.Contains(parser.Source))
        {
            _engine.RemoveAgent(parser.AgentId);
            _linksChanged |= _links.Remove(parser.AgentId);
            return;
        }

        var link = AgentLink.Of(parser);
        if (!_links.TryGetValue(parser.AgentId, out var known) || known != link)
        {
            _links[parser.AgentId] = link;
            _linksChanged = true;
        }

        _engine.UpdateAgent(new AgentInfo(
            parser.AgentId,
            parser.Source,
            parser.Project,
            parser.Model,
            parser.IsSubagent,
            parser.Phase,
            parser.InFlightStartMs,
            parser.LastLineMs,
            _parents.GetValueOrDefault(parser.AgentId)));
    }

    private long _antigravityTodayStart;

    private bool RefreshAntigravity(long todayStart, bool seeded)
    {
        _antigravityTodayStart = todayStart;
        var changed = false;
        foreach (var source in _antigravity)
        {
            changed |= source.Refresh(todayStart, agent => _engine.UpdateAgent(agent), (response, completion) =>
            {
                _responsesSeen++;
                _engine.Upsert(response, completion, seeded);
                _observer?.OnResponse(response, completion, seeded);
            });
        }
        return changed;
    }

    /// <summary>Applies one read of the OpenCode database; true when anything changed.</summary>
    private bool ApplyOpenCode(OpenCodeUpdate update, bool seeded)
    {
        foreach (var id in update.Removed)
        {
            _engine.RemoveAgent(id);
            _linksChanged |= _links.Remove(id);
        }

        foreach (var state in update.Agents.Values)
        {
            PushOpenCodeAgent(state);
        }

        foreach (var (response, isCompletion) in update.Responses)
        {
            _responsesSeen++;
            _engine.Upsert(response, isCompletion, seeded);
            _observer?.OnResponse(response, isCompletion, seeded);
        }

        return !update.IsEmpty;
    }

    private void PushOpenCodeAgent(OpenCodeAgentState state)
    {
        if (state.LastLineMs is null)
        {
            return;
        }

        var link = new AgentLink(state.Id, LinkKind.OpenCodeSession, state.SessionId, state.ParentSessionId, state.CreatedMs);
        if (!_links.TryGetValue(state.Id, out var known) || known != link)
        {
            _links[state.Id] = link;
            _linksChanged = true;
        }

        _engine.UpdateAgent(new AgentInfo(
            state.Id,
            SourceKind.OpenCode,
            state.Project,
            state.Model,
            state.IsSubagent,
            state.Phase,
            state.InFlightStartMs,
            state.LastLineMs,
            _parents.GetValueOrDefault(state.Id)));
    }

    /// <summary>Re-resolves parent links over the engine's agents after any of them changed (METRICS 6.7).</summary>
    private void ResolveParents()
    {
        if (!_linksChanged)
        {
            return;
        }

        _linksChanged = false;
        var resolved = ParentLinks.Resolve(_links.Values, PathComparer);
        foreach (var id in _links.Keys)
        {
            var parent = resolved.GetValueOrDefault(id);
            if (!string.Equals(parent, _parents.GetValueOrDefault(id), StringComparison.Ordinal))
            {
                _engine.SetParent(id, parent);
            }
        }

        _parents.Clear();
        foreach (var (id, parent) in resolved)
        {
            _parents[id] = parent;
        }
    }

    private void HandleReadFailure(TrackedFile tracked, Exception error)
    {
        if (error is FileNotFoundException or DirectoryNotFoundException)
        {
            Remove(tracked);
        }
        else
        {
            tracked.Unreadable = true;
        }
    }

    /// <summary>The file no longer exists: its agent leaves the list, its responses stay in every total (METRICS 6.7).</summary>
    private void Remove(TrackedFile tracked)
    {
        if (_files.Remove(tracked.Path))
        {
            _removedMalformedLines += tracked.Parser.MalformedLines;
            _removedOversizedLines += tracked.Tailer.OversizedLines;
            _engine.RemoveAgent(tracked.Parser.AgentId);
            _linksChanged |= _links.Remove(tracked.Parser.AgentId);
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return path.Length > 0 && Directory.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private readonly record struct ReadPlan(
        TrackedFile File, DateTime LastWriteUtc, long StartOffset, StartAlignment Align, long ExpectedLength);

    private readonly record struct Prepared(ILineBatch Batch, TailReadResult Result);

    private sealed class BackgroundRead(Task<Prepared> task)
    {
        public Task<Prepared> Task { get; } = task;

        /// <summary>The file changed again while it was being read.</summary>
        public bool Again { get; set; }
    }

    private sealed class Collector : IResponseSink
    {
        public List<(ParsedResponse Response, bool IsCompletion)> Items { get; } = [];

        public void OnResponse(ParsedResponse response, bool isCompletion) => Items.Add((response, isCompletion));
    }
}
