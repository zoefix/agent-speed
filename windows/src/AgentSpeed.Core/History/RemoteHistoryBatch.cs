using System.Text;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.History;

/// <summary>The bounds of one host's stored rows (REMOTE 8.3 <i>Bounds</i>).</summary>
internal sealed record RemoteHistoryLimits(int Days = 100_000, int Perf = 1_000_000);

/// <summary>A <c>historyPerf</c> record of a batch, with its exact JSON text.</summary>
internal sealed record RemoteBatchPerf(string Model, string Json, PerfSums Record)
{
    public int ByteCount { get; } = Encoding.UTF8.GetByteCount(Model) + Encoding.UTF8.GetByteCount(Json);
}

/// <summary>A <c>historyDay</c> of a batch with the records sent right after it, by model, each model once.</summary>
internal sealed class RemoteBatchDay(string day, SourceKind source, RemoteDayAggregate aggregate, string aggregateJson, string digest)
{
    private readonly int _aggregateBytes = Encoding.UTF8.GetByteCount(aggregateJson);

    public string Day { get; } = day;

    public SourceKind Source { get; } = source;

    public RemoteDayAggregate Aggregate { get; } = aggregate;

    public string AggregateJson { get; } = aggregateJson;

    public string Digest { get; } = digest;

    public List<RemoteBatchPerf> Perf { get; } = [];

    public DayKey Key => new(Day, Source);

    public int ByteCount => _aggregateBytes + Perf.Sum(p => p.ByteCount);
}

/// <summary>
/// A host's <c>historyDay</c> lines, each with the <c>historyPerf</c> lines right after it (REMOTE 8.2), received since the host's
/// last <c>historyDone</c>. Kept in memory until it commits, except that a large batch moves its complete days to the store's
/// staging tables; a broken connection drops it (REMOTE 8.3).
/// </summary>
internal sealed class RemoteHistoryBatch(string host, int connection)
{
    /// <summary>Records accepted per day and source (the relay's list limit).</summary>
    public const int PerfLimit = RemoteLimits.HistoryList;

    private readonly List<RemoteBatchDay> _days = [];
    private readonly HashSet<DayKey> _keys = [];
    private readonly Dictionary<DayKey, int> _staged = [];

    public string Host { get; } = host;

    public int Connection { get; } = connection;

    /// <summary>In arrival order; a day and source sent again replaces its earlier entry.</summary>
    public IReadOnlyList<RemoteBatchDay> Days => _days;

    /// <summary>The JSON text held, for the memory bound.</summary>
    public long ByteCount { get; private set; }

    /// <summary>The distinct days of the batch, staged ones included (a day sent again replaces its earlier version).</summary>
    public int DayCount { get; private set; }

    /// <summary>The records the batch's days keep, staged ones included.</summary>
    public int PerfCount { get; private set; }

    public bool IsStaged => _staged.Count > 0;

    public void Add(RemoteHistoryDay line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var day = new RemoteBatchDay(line.Day, line.Source, line.Aggregate, line.AggregateJson, line.Digest);
        if (!_keys.Add(day.Key) && _days.FindIndex(d => d.Key == day.Key) is var index and >= 0)
        {
            ByteCount -= _days[index].ByteCount;
            PerfCount -= _days[index].Perf.Count;
            _days.RemoveAt(index);
        }
        else if (_staged.TryGetValue(day.Key, out var records))
        {
            PerfCount -= records;
        }
        else
        {
            DayCount++;
        }

        _days.Add(day);
        ByteCount += day.ByteCount;
    }

    /// <summary>A record belongs to the day sent right before it; any other is ignored.</summary>
    public void Add(RemoteHistoryPerf line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (_days.Count == 0 || _days[^1] is not { } last || last.Day != line.Day || last.Source != line.Source)
        {
            return;
        }

        var perf = new RemoteBatchPerf(line.Model, line.RecordJson, line.Record);
        var index = last.Perf.FindIndex(p => p.Model == perf.Model);
        if (index >= 0)
        {
            ByteCount -= last.Perf[index].ByteCount;
            last.Perf[index] = perf;
        }
        else if (last.Perf.Count < PerfLimit)
        {
            last.Perf.Add(perf);
            PerfCount++;
        }
        else
        {
            return;
        }

        ByteCount += perf.ByteCount;
    }

    /// <summary>Removes and returns every day but the last, which may still get records; they count as staged.</summary>
    public IReadOnlyList<RemoteBatchDay> TakeCompleted()
    {
        if (_days.Count <= 1)
        {
            return [];
        }

        var completed = _days.GetRange(0, _days.Count - 1);
        _days.RemoveRange(0, completed.Count);
        _keys.Clear();
        _keys.Add(_days[0].Key);
        ByteCount = _days[0].ByteCount;
        foreach (var day in completed)
        {
            _staged[day.Key] = day.Perf.Count;
        }

        return completed;
    }

    public bool Exceeds(RemoteHistoryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return DayCount > limits.Days || PerfCount > limits.Perf;
    }
}
