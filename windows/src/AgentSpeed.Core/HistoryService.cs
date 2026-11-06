using System.Diagnostics;
using System.Globalization;
using AgentSpeed.Core.History;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core;

/// <summary>
/// Keeps the daily usage history (METRICS 8) and the performance records (6.11) on a below-normal-priority background thread and
/// publishes them to a <see cref="HistoryHub"/>, with the remote hosts' days that <see cref="Remote"/> receives (REMOTE 8.3).
/// Runs at start, every 10 minutes, after local midnight and on <see cref="HistoryHub.Refresh"/>; <see cref="HistoryHub.Clear"/>
/// deletes the store (the hosts' rows with it), recomputes and asks connected hosts for their days again. Never touches the
/// live monitor; <see cref="PerfRecordsUpdated"/> hands it the records its baselines come from.
/// </summary>
public sealed class HistoryService : IDisposable
{
    private static readonly TimeSpan RunInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MidnightDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StateInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PartialDaysInterval = TimeSpan.FromSeconds(2);

    /// <summary>A run that takes longer than this shows its progress; quick periodic runs stay quiet.</summary>
    private static readonly TimeSpan QuietRunLimit = TimeSpan.FromSeconds(1);

    private const long BigRunBytes = 64L * 1024 * 1024;

    private readonly HistoryHub _hub;
    private readonly string _storePath;
    private readonly TimeZoneInfo? _zone;
    private readonly HistoryOptions _options;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Dictionary<string, double> _livePeaks = new(StringComparer.Ordinal);
    private MonitorConfiguration _configuration;
    private Thread? _thread;
    private CancellationTokenSource? _stop;
    private CancellationTokenSource? _run;
    private bool _refreshRequested;
    private bool _clearRequested;
    private bool _bindingSaveRequested;
    private bool _disposed;

    /// <summary>The state last published from the history thread, published again with changed remote rows.</summary>
    private HistoryState _lastState = HistoryState.NotLoaded;

    /// <param name="pricing">Shared with the live engine (METRICS 9.7); null uses the bundled table.</param>
    /// <param name="remote">The remote hosts' daily history; null creates one.</param>
    public HistoryService(HistoryHub hub, MonitorConfiguration configuration, string? storePath = null, PriceBook? pricing = null,
        RemoteHistorySync? remote = null)
        : this(hub, configuration, storePath, zone: null, options: null, pricing, remote)
    {
    }

    internal HistoryService(
        HistoryHub hub,
        MonitorConfiguration configuration,
        string? storePath,
        TimeZoneInfo? zone,
        HistoryOptions? options,
        PriceBook? pricing = null,
        RemoteHistorySync? remote = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(configuration);
        _hub = hub;
        _configuration = configuration;
        _storePath = string.IsNullOrWhiteSpace(storePath) ? DefaultStorePath : Path.GetFullPath(storePath);
        _zone = zone;
        _options = options ?? new HistoryOptions();
        Pricing = pricing ?? new PriceBook();
        Remote = remote ?? new RemoteHistorySync();
    }

    /// <summary>Shared with the live engine (METRICS 9.7).</summary>
    public PriceBook Pricing { get; }

    /// <summary>The remote hosts' daily history (REMOTE 8.3): the remote coordinator's history sink.</summary>
    public RemoteHistorySync Remote { get; }

    /// <summary>
    /// Called on the history thread with the stored performance records of the enabled sources (METRICS 6.11
    /// <i>Baseline</i>): once the store is loaded and after every completed run.
    /// </summary>
    public Action<IReadOnlyList<PerfRecord>>? PerfRecordsUpdated { get; set; }

    /// <summary>
    /// Loads the day bindings and table copies of the store into <paramref name="book"/> (a quick read that never changes
    /// the store), before the live engine prices anything (METRICS 9.7). Returns the dates whose binding was repaired.
    /// </summary>
    public static IReadOnlyList<string> RestorePricing(PriceBook book, string? storePath = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        var (bindings, tables) = HistoryStore.StoredPricing(
            string.IsNullOrWhiteSpace(storePath) ? DefaultStorePath : Path.GetFullPath(storePath));
        return book.Restore(bindings, tables);
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\AgentSpeed\history.sqlite</c>; the developer variable <c>AGENT_SPEED_HISTORY_FILE</c> (a fully
    /// qualified local path) points a test session at its own store so it never writes into the user's history.
    /// </summary>
    public static string DefaultStorePath =>
        Environment.GetEnvironmentVariable("AGENT_SPEED_HISTORY_FILE") is { Length: > 0 } custom && Path.IsPathFullyQualified(custom)
            && !custom.StartsWith(@"\\", StringComparison.Ordinal)
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSpeed", "history.sqlite");

    public string StorePath => _storePath;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null)
            {
                return;
            }

            var stop = new CancellationTokenSource();
            _stop = stop;
            _hub.RefreshHandler = Refresh;
            _hub.ClearHandler = Clear;
            Pricing.Bound += RequestBindingSave;
            var zone = _zone;
            var ledger = new RemoteHistoryLedger(_options.RemoteLimits, zone: () => RemoteZone.Current(zone).Id);
            var remote = new RemoteHistoryWorker(Signal, Remote.Request, ledger);
            _thread = new Thread(() => Run(stop.Token, remote))
            {
                IsBackground = true,
                Name = "Agent Speed history",
                Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();
            Remote.Attach(remote);
        }
    }

    public void Stop()
    {
        Thread? thread;
        CancellationTokenSource? stop;
        lock (_gate)
        {
            thread = _thread;
            stop = _stop;
            _thread = null;
            _stop = null;
            if (thread is not null)
            {
                Remote.Attach(null);
            }

            Pricing.Bound -= RequestBindingSave;
            CancelRun();
            if (_hub.RefreshHandler == Refresh)
            {
                _hub.RefreshHandler = null;
            }

            if (_hub.ClearHandler == Clear)
            {
                _hub.ClearHandler = null;
            }
        }

        if (thread is null || stop is null)
        {
            return;
        }

        stop.Cancel();
        Signal();
        if (thread != Thread.CurrentThread && thread.Join(TimeSpan.FromSeconds(10)))
        {
            stop.Dispose();
        }
    }

    /// <summary>Applies new settings; the history is analysed again in the background when anything changed.</summary>
    public void Update(MonitorConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_gate)
        {
            if (configuration.Equals(_configuration))
            {
                return;
            }

            _configuration = configuration;
            _refreshRequested = true;
            CancelRun();
        }

        Signal();
    }

    /// <summary>
    /// Records the live peak of today from a running snapshot (call it for live snapshots, not previews); stored with
    /// the next run.
    /// </summary>
    public void ObserveLive(SpeedSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.State.Kind != MonitorStateKind.Running || snapshot.Timestamp == DateTimeOffset.MinValue)
        {
            return;
        }

        var peak = snapshot.PeakToday.Output;
        if (!double.IsFinite(peak) || peak <= 0)
        {
            return;
        }

        var day = TimeZoneInfo.ConvertTime(snapshot.Timestamp, _zone ?? TimeZoneInfo.Local)
            .Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (_livePeaks)
        {
            if (peak > _livePeaks.GetValueOrDefault(day))
            {
                _livePeaks[day] = peak;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
        _wake.Dispose();
    }

    private void Refresh()
    {
        lock (_gate)
        {
            _refreshRequested = true;
        }

        Signal();
    }

    private void Clear()
    {
        lock (_gate)
        {
            _clearRequested = true;
            CancelRun();
        }

        Signal();
    }

    /// <summary>Saves new day bindings (the live engine binds today) soon, once per burst.</summary>
    private void RequestBindingSave()
    {
        lock (_gate)
        {
            if (_bindingSaveRequested)
            {
                return;
            }

            _bindingSaveRequested = true;
        }

        Signal();
    }

    private void CancelRun()
    {
        try
        {
            _run?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Signal()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Run(CancellationToken stopToken, RemoteHistoryWorker remote)
    {
        HistoryStore? store = null;
        HistoryAnalyzer? analyzer = null;
        var first = true;
        var resync = false;
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                bool clear;
                MonitorConfiguration configuration;
                lock (_gate)
                {
                    clear = _clearRequested;
                    _clearRequested = false;
                    _refreshRequested = false;
                    configuration = _configuration;
                }

                var nextDelay = RunInterval;
                try
                {
                    if (clear)
                    {
                        store?.Dispose();
                        store = null;
                        analyzer = null;
                        remote.Ledger.Reset();
                        resync = true;
                        Pricing.ClearBindings();
                        lock (HistoryStore.FileGate)
                        {
                            HistoryStore.Delete(_storePath);
                        }

                        lock (_livePeaks)
                        {
                            _livePeaks.Clear();
                        }

                        first = true;
                    }

                    if (analyzer is null)
                    {
                        store = HistoryStore.Open(_storePath);
                        analyzer = new HistoryAnalyzer(store, _options, Pricing);
                        remote.Ledger.Load(store);
                        if (resync)
                        {
                            resync = false;
                            remote.Resync();
                        }
                    }

                    var completed = Analyze(analyzer, configuration, first, remote, stopToken);
                    first &= !completed;
                }
                catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a configuration change or a clear; the next run starts right away.
                }
                catch (Exception e)
                {
                    // Never let the background thread take the app down; report and retry later.
                    store?.Dispose();
                    store = null;
                    analyzer = null;
                    Publish(HistoryState.Failed(FailureMessage(e)));
                    nextDelay = RetryDelay;
                }

                var current = analyzer;
                var currentStore = store;
                WaitForNextRun(nextDelay, stopToken, () =>
                {
                    SaveBindings(current);
                    ApplyRemote(remote, currentStore, current);
                });
            }
        }
        finally
        {
            if (analyzer is not null)
            {
                try
                {
                    analyzer.RecordPeaks(TakeLivePeaks());
                }
                catch (SqliteException)
                {
                }
            }

            store?.Dispose();
        }
    }

    /// <summary>One analysis run; false when it was cancelled before finishing.</summary>
    private bool Analyze(HistoryAnalyzer analyzer, MonitorConfiguration configuration, bool first, RemoteHistoryWorker remote,
        CancellationToken stopToken)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        lock (_gate)
        {
            if (_clearRequested || !_configuration.Equals(configuration))
            {
                return false;
            }

            _run = run;
        }

        try
        {
            var enabled = configuration.EnabledSources;
            var zone = _zone ?? TimeZoneInfo.Local;
            if (first)
            {
                PublishAll(HistoryState.Analyzing(0, 0, 0), analyzer, enabled, remote);
            }

            var started = Stopwatch.GetTimestamp();
            var lastState = TimeSpan.Zero;
            var lastDays = TimeSpan.Zero;
            HistoryReport report;
            report = analyzer.Run(SourceLocations.Resolve(configuration), enabled, zone, run.Token, progress =>
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                var visible = first || progress.BytesTotal >= BigRunBytes || elapsed >= QuietRunLimit;
                if (!visible || elapsed - lastState < StateInterval)
                {
                    return;
                }

                lastState = elapsed;
                var state = HistoryState.Analyzing(progress.Fraction, progress.FilesDone, progress.FilesTotal);
                if (elapsed - lastDays >= PartialDaysInterval)
                {
                    lastDays = elapsed;
                    Publish(state, Days(analyzer, enabled, remote), HostNames(remote));
                }
                else
                {
                    Publish(state);
                }
            });

            analyzer.RecordPeaks(TakeLivePeaks());
            if (report.Cancelled)
            {
                return false;
            }

            PublishAll(HistoryState.Ready(DateTimeOffset.Now), analyzer, enabled, remote);
            if (report.BytesAnalyzed >= BigRunBytes)
            {
                // A large analysis decoded gigabytes; return the freed memory instead of keeping it committed.
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }

            return true;
        }
        finally
        {
            lock (_gate)
            {
                _run = null;
            }
        }
    }

    /// <summary>
    /// The days with the performance records of the enabled sources, which also go to the live monitor; every host's days and
    /// records with them (REMOTE 8.3).
    /// </summary>
    private void PublishAll(HistoryState state, HistoryAnalyzer analyzer, IReadOnlySet<SourceKind> enabled, RemoteHistoryWorker remote)
    {
        IReadOnlyList<PerfRecord> local = [.. analyzer.PerfRecords().Where(r => enabled.Contains(r.Source))];
        var records = RemoteHistoryMerge.Perf(local, remote.Ledger.Rows.PerfRecords(enabled));
        _lastState = state;
        _hub.Publish(state, Days(analyzer, enabled, remote), records, HostNames(remote));
        PerfRecordsUpdated?.Invoke(records);
    }

    private void Publish(HistoryState state, IReadOnlyList<DailyUsage>? days = null, IReadOnlyDictionary<string, string>? hostNames = null)
    {
        _lastState = state;
        _hub.Publish(state, days, hostNames);
    }

    /// <summary>The local days and every host's of the enabled sources; remote rows carry the day's live peak too.</summary>
    private IReadOnlyList<DailyUsage> Days(HistoryAnalyzer analyzer, IReadOnlySet<SourceKind> enabled, RemoteHistoryWorker remote)
    {
        Dictionary<string, double> live;
        lock (_livePeaks)
        {
            live = new Dictionary<string, double>(_livePeaks, StringComparer.Ordinal);
        }

        return RemoteHistoryMerge.Days(analyzer.Days(enabled, live), remote.Ledger.Rows.Usage(enabled, day => analyzer.Peak(day, live)));
    }

    private static Dictionary<string, string> HostNames(RemoteHistoryWorker remote) =>
        new(remote.Ledger.Rows.Names, StringComparer.Ordinal);

    /// <summary>
    /// The queued remote work, between analysis runs (REMOTE 8.3): changed rows publish the days again with the last state, and
    /// the <c>historyTooLarge</c> hosts with them.
    /// </summary>
    private void ApplyRemote(RemoteHistoryWorker remote, HistoryStore? store, HistoryAnalyzer? analyzer)
    {
        RemoteHistoryDrain drain;
        try
        {
            drain = remote.Drain(analyzer is null ? null : store);
        }
        catch (Exception e)
        {
            // Never let the background thread take the app down; the next sync sends the days again.
            Trace.TraceWarning("Remote history could not be applied: {0}", e.GetType().Name);
            return;
        }

        if (drain.Changed && analyzer is not null)
        {
            IReadOnlySet<SourceKind> enabled;
            lock (_gate)
            {
                enabled = _configuration.EnabledSources;
            }

            PublishAll(_lastState, analyzer, enabled, remote);
        }

        if (drain.TooLargeChanged)
        {
            _hub.PublishRemoteTooLarge(remote.Ledger.TooLarge.ToHashSet(StringComparer.Ordinal));
        }
    }

    private Dictionary<string, double> TakeLivePeaks()
    {
        lock (_livePeaks)
        {
            return new Dictionary<string, double>(_livePeaks, StringComparer.Ordinal);
        }
    }

    private void SaveBindings(HistoryAnalyzer? analyzer)
    {
        lock (_gate)
        {
            if (!_bindingSaveRequested)
            {
                return;
            }

            _bindingSaveRequested = false;
        }

        try
        {
            analyzer?.SaveBindings();
        }
        catch (SqliteException)
        {
            // Kept in memory; the next analysis run saves them again.
        }
    }

    private void WaitForNextRun(TimeSpan interval, CancellationToken stopToken, Action onWake)
    {
        var zone = _zone ?? TimeZoneInfo.Local;
        var due = Stopwatch.GetTimestamp();
        while (!stopToken.IsCancellationRequested)
        {
            onWake();
            lock (_gate)
            {
                if (_refreshRequested || _clearRequested)
                {
                    return;
                }
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var midnight = LocalDay.StartMs(LocalDay.StartMs(now, zone) + 36 * 3_600_000L, zone) - now;
            var untilMidnight = TimeSpan.FromMilliseconds(Math.Max(0, midnight)) + MidnightDelay;
            var remaining = interval - Stopwatch.GetElapsedTime(due);
            var wait = remaining < untilMidnight ? remaining : untilMidnight;
            if (wait <= TimeSpan.Zero)
            {
                return;
            }

            // Wake at least every minute so clock and time zone changes are noticed.
            if (WaitHandle.WaitAny([_wake, stopToken.WaitHandle], wait < TimeSpan.FromMinutes(1) ? wait : TimeSpan.FromMinutes(1))
                == WaitHandle.WaitTimeout && wait <= TimeSpan.FromMinutes(1))
            {
                return;
            }
        }
    }

    private static string FailureMessage(Exception error) => error switch
    {
        SqliteException { IsBusy: true } => "The history store is in use.",
        SqliteException => "The history store could not be opened.",
        _ => "The history store is not accessible.",
    };
}
