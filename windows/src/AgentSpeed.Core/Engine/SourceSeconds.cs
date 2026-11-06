namespace AgentSpeed.Core.Engine;

/// <summary>
/// Today's measured output per (host, source) cell in one-second buckets (METRICS 6.6) and the measured peak of any subset
/// of cells (METRICS 6.8, DESIGN 13). A cell gets buckets only once it has measured data today. This computer's cells take
/// the first slots in source order, so a subset of local data sums exactly as the per-source buckets always did.
/// </summary>
/// <remarks>
/// Each requested subset caches the peak trailing average per chunk. A change marks the chunks whose windows it touched in
/// the cached subsets that contain its cell, so a subset is rescanned only after one of its cells changed, and then only
/// those chunks.
/// </remarks>
internal sealed class SourceSeconds
{
    /// <summary>Subsets beyond this are dropped oldest first; at most eight orbs and eight boards ask at a time.</summary>
    public const int MaxCachedSets = 32;

    private const int ChunkSeconds = SecondSeries.ChunkSeconds;

    private static readonly int SourceCount = Enum.GetValues<SourceKind>().Length;

    private readonly List<HostSource> _cells = [.. Enum.GetValues<SourceKind>().Select(HostSource.Local)];
    private readonly Dictionary<HostSource, int> _slots = [];
    private readonly List<SecondSeries?> _series = [.. Enumerable.Repeat<SecondSeries?>(null, SourceCount)];
    private readonly List<SetPeak> _sets = [];
    private double[]?[] _members = new double[SourceCount][];

    public SourceSeconds()
    {
        for (var i = 0; i < _cells.Count; i++)
        {
            _slots[_cells[i]] = i;
        }
    }

    /// <summary>Sources whose buckets are allocated on any host.</summary>
    public SourceSet Allocated
    {
        get
        {
            var set = SourceSet.Empty;
            for (var i = 0; i < _series.Count; i++)
            {
                if (_series[i] is not null)
                {
                    set = set.With(_cells[i].Source);
                }
            }

            return set;
        }
    }

    /// <summary>Chunks rescanned to answer peak requests (tests).</summary>
    public long ChunkScans { get; private set; }

    public void Clear()
    {
        for (var i = 0; i < _series.Count; i++)
        {
            _series[i] = null;
        }

        _sets.Clear();
    }

    /// <summary>Adds (positive) or removes (negative) measured output of this computer's <paramref name="source"/>.</summary>
    public void AddInterval(SourceKind source, long startMs, long endMs, double amount, long floorMs) =>
        AddInterval(HostSource.Local(source), startMs, endMs, amount, floorMs);

    /// <summary>Adds (positive) or removes (negative) measured output of one cell, like the all-sources buckets.</summary>
    public void AddInterval(HostSource cell, long startMs, long endMs, double amount, long floorMs)
    {
        var from = Math.Max(startMs, floorMs);
        if (endMs <= startMs || amount == 0 || from >= endMs)
        {
            return;
        }

        if (!_slots.TryGetValue(cell, out var slot))
        {
            if (amount < 0)
            {
                return;
            }

            slot = _cells.Count;
            _cells.Add(cell);
            _slots[cell] = slot;
            _series.Add(null);
            if (_members.Length < _cells.Count)
            {
                Array.Resize(ref _members, _cells.Count * 2);
            }
        }

        var series = _series[slot];
        if (series is null)
        {
            if (amount < 0)
            {
                return;
            }

            series = _series[slot] = new SecondSeries();
        }

        series.AddInterval(startMs, endMs, amount, floorMs);

        // Trailing windows that include a changed second end up to two seconds after it.
        var firstChunk = SecondSeries.ChunkOf(SpeedMath.FloorDiv(from, 1000));
        var lastChunk = SecondSeries.ChunkOf(SpeedMath.FloorDiv(endMs - 1, 1000) + 2);
        foreach (var set in _sets)
        {
            if (!set.Key.Contains(cell))
            {
                continue;
            }

            for (var chunk = firstChunk; chunk <= lastChunk; chunk++)
            {
                set.Dirty.Add(chunk);
            }
        }
    }

    /// <summary>The same for <paramref name="sources"/> on every host.</summary>
    public double MeasuredPeak(SourceSet sources) => MeasuredPeak(new SubsetKey(sources));

    /// <summary>Maximum over today of the 3-bucket trailing average of the members' summed buckets.</summary>
    public double MeasuredPeak(SubsetKey key)
    {
        var set = Find(key) ?? Cache(key);
        if (set.Dirty.Count == 0)
        {
            return set.Peak;
        }

        foreach (var chunk in set.Dirty)
        {
            if (ChunkPeak(key, chunk) is { } peak)
            {
                set.ChunkPeaks[chunk] = peak;
            }
            else
            {
                set.ChunkPeaks.Remove(chunk);
            }

            ChunkScans++;
        }

        set.Dirty.Clear();
        var best = 0.0;
        foreach (var peak in set.ChunkPeaks.Values)
        {
            best = Math.Max(best, peak);
        }

        set.Peak = best;
        return best;
    }

    private SetPeak? Find(SubsetKey key)
    {
        foreach (var set in _sets)
        {
            if (set.Key == key)
            {
                return set;
            }
        }

        return null;
    }

    private SetPeak Cache(SubsetKey key)
    {
        if (_sets.Count >= MaxCachedSets)
        {
            _sets.RemoveAt(0);
        }

        var set = new SetPeak(key);
        for (var slot = 0; slot < _cells.Count; slot++)
        {
            if (key.Contains(_cells[slot]) && _series[slot] is { } series)
            {
                set.Dirty.UnionWith(series.ChunkKeys);
            }
        }

        _sets.Add(set);
        return set;
    }

    /// <summary>
    /// Peak of the trailing averages that end inside <paramref name="chunk"/>; null when no member has data there (such
    /// windows never exceed the window that ends at the last second with data).
    /// </summary>
    private double? ChunkPeak(SubsetKey key, long chunk)
    {
        var count = 0;
        double previous2 = 0, previous1 = 0;
        for (var slot = 0; slot < _cells.Count; slot++)
        {
            if (!key.Contains(_cells[slot]) || _series[slot] is not { } series)
            {
                continue;
            }

            if (series.TryGetChunk(chunk, out var values))
            {
                _members[count++] = values;
            }

            if (series.TryGetChunk(chunk - 1, out var before))
            {
                previous2 += Math.Max(0, before[ChunkSeconds - 2]);
                previous1 += Math.Max(0, before[ChunkSeconds - 1]);
            }
        }

        if (count == 0)
        {
            return null;
        }

        var best = 0.0;
        for (var i = 0; i < ChunkSeconds; i++)
        {
            var value = 0.0;
            for (var m = 0; m < count; m++)
            {
                value += Math.Max(0, _members[m]![i]);
            }

            var average = (previous2 + previous1 + value) / 3;
            if (average > best)
            {
                best = average;
            }

            previous2 = previous1;
            previous1 = value;
        }

        Array.Clear(_members, 0, count);
        return best;
    }

    private sealed class SetPeak(SubsetKey key)
    {
        public SubsetKey Key { get; } = key;

        /// <summary>Peak trailing average per chunk with data.</summary>
        public Dictionary<long, double> ChunkPeaks { get; } = [];

        /// <summary>Chunks whose peak is out of date.</summary>
        public HashSet<long> Dirty { get; } = [];

        public double Peak { get; set; }
    }
}
