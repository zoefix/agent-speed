namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>A model's share of a window's priced spend (REPORTING-EXTENSIONS 11.2 <c>mix</c>).</summary>
public readonly record struct QuotaMixShare(string Model, double Share);

/// <summary>
/// The spend ledger of QUOTAS 10.3: rows per (account, source, price-table model, UTC minute of the end) with the cost,
/// unpriced requests, the tokens of priced and unpriced responses and their token counts. Sums read prefix sums cached per
/// (account, window name), so they cost two binary searches. Thread-safe for reads once filled.
/// </summary>
public sealed class SpendLedger
{
    private readonly record struct RowKey(string Account, SourceKind Source, string Model, long Minute);

    private sealed class Row
    {
        public long Cost;
        public long Unpriced;
        public long PricedTokens;
        public long UnpricedTokens;

        /// <summary>Null once a response recorded without counts joined the row (unmeasured).</summary>
        public QuotaTokenCounts? Counts = QuotaTokenCounts.Zero;

        /// <summary>The model is an id of the price table, for every response of the row.</summary>
        public bool PublicModel = true;
    }

    private sealed class Series(long[] minutes, long[] cost, long[] unpriced, long[] unpricedTokens, double? rate, Dictionary<string, (long[] Minutes, long[] Cost)> models,
        (long Minute, string? Model, QuotaTokenCounts? Counts)[] counted)
    {
        public long[] Minutes { get; } = minutes;

        public long[] Cost { get; } = cost;

        public long[] Unpriced { get; } = unpriced;

        public long[] UnpricedTokens { get; } = unpricedTokens;

        public double? Rate { get; } = rate;

        public Dictionary<string, (long[] Minutes, long[] Cost)> Models { get; } = models;

        /// <summary>Every row's token counts by minute, its model null when not the table's (QUOTAS 10.3 <c>T</c>).</summary>
        public (long Minute, string? Model, QuotaTokenCounts? Counts)[] Counted { get; } = counted;
    }

    private readonly Dictionary<RowKey, Row> _rows = [];
    private readonly Dictionary<(string Account, string Name), Series> _cache = [];
    private readonly object _gate = new();

    public int Count => _rows.Count;

    public static long MinuteOf(long ms) => FloorDiv(ms, EstimateLimits.Minute) * EstimateLimits.Minute;

    /// <summary>Adds one response, or a minute's worth of them; a null cost is unpriced, null <paramref name="counts"/> unmeasured.</summary>
    public void Add(string account, SourceKind source, string model, long endMs, long? costMicroUsd, long requests = 1, long tokens = 0,
        QuotaTokenCounts? counts = null, bool publicModel = true)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
        {
            var key = new RowKey(account, source, model, MinuteOf(endMs));
            if (!_rows.TryGetValue(key, out var row))
            {
                row = new Row();
                _rows.Add(key, row);
            }

            if (costMicroUsd is { } cost)
            {
                row.Cost += cost;
                row.PricedTokens += tokens;
            }
            else
            {
                row.Unpriced += requests;
                row.UnpricedTokens += tokens;
            }

            row.Counts = QuotaTokenCounts.Merge(row.Counts, counts);
            row.PublicModel &= publicModel;
            _cache.Clear();
        }
    }

    public void Prune(long nowMs)
    {
        lock (_gate)
        {
            foreach (var key in _rows.Keys.Where(k => k.Minute < nowMs - EstimateLimits.KeepMs).ToList())
            {
                _rows.Remove(key);
            }

            _cache.Clear();
        }
    }

    /// <summary>Whether a price-table model id has each word of a window's name as a <c>-</c>-separated token.</summary>
    public static bool ModelMatches(string model, string? name)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        var tokens = model.ToLowerInvariant().Split('-');
        foreach (var part in name.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Array.IndexOf(tokens, part) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>C⁻: the spend of the minutes wholly inside [start, end], unpriced as 0, and its unpriced requests.</summary>
    public (long Cost, long Unpriced) Inner(string account, string? name, long start, long end)
    {
        var (cost, unpriced, _, _) = Range(account, name, CeilDiv(start, EstimateLimits.Minute) * EstimateLimits.Minute, MinuteOf(end) - EstimateLimits.Minute);
        return (cost, unpriced);
    }

    /// <summary>The spend of the minutes touching [start, end], unpriced as 0, and its unpriced requests.</summary>
    public (long Cost, long Unpriced) Outer(string account, string? name, long start, long end)
    {
        var (cost, unpriced, _, _) = Range(account, name, MinuteOf(start), MinuteOf(end));
        return (cost, unpriced);
    }

    /// <summary>C⁺: <see cref="Outer"/> with each unpriced token at the account's highest priced rate; ∞ without one.</summary>
    public double OuterHigh(string account, string? name, long start, long end)
    {
        var (cost, unpriced, tokens, rate) = Range(account, name, MinuteOf(start), MinuteOf(end));
        if (unpriced == 0)
        {
            return cost;
        }

        return rate is { } r ? cost + tokens * r : double.PositiveInfinity;
    }

    /// <summary>The models of the most priced spend in the minutes touching [start, end], their shares to three decimals.</summary>
    public IReadOnlyList<QuotaMixShare> Mix(string account, string? name, long start, long end, int top = 3)
    {
        var series = SeriesOf(account, name);
        var first = MinuteOf(start);
        var last = MinuteOf(end);
        var totals = new List<(string Model, long Cost)>();
        long whole = 0;
        foreach (var (model, (minutes, cost)) in series.Models)
        {
            var sum = Sum(minutes, cost, first, last);
            if (sum > 0)
            {
                totals.Add((model, sum));
                whole += sum;
            }
        }

        if (whole <= 0)
        {
            return [];
        }

        totals.Sort((a, b) => a.Cost != b.Cost ? b.Cost.CompareTo(a.Cost) : string.CompareOrdinal(a.Model, b.Model));
        var shares = new QuotaMixShare[Math.Min(top, totals.Count)];
        for (var i = 0; i < shares.Length; i++)
        {
            shares[i] = new QuotaMixShare(totals[i].Model, EstimateMath.RoundHalfAway((double)totals[i].Cost / whole, 3));
        }

        return shares;
    }

    /// <summary>
    /// <c>T</c>: the token counts of the rows <see cref="Outer"/> sums (REPORTING-EXTENSIONS 12 <c>tokenUsage</c>); null when
    /// a row is unmeasured, past 16 models or an unsafe total.
    /// </summary>
    public QuotaTokenUsage? TokenUsage(string account, string? name, long start, long end)
    {
        var counted = SeriesOf(account, name).Counted;
        var first = MinuteOf(start);
        var last = MinuteOf(end);
        var low = 0;
        var high = counted.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (counted[mid].Minute < first)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        var total = QuotaTokenCounts.Zero;
        var models = new Dictionary<string, (long Input, long Output)>(StringComparer.Ordinal);
        (long Input, long Output) others = default;
        for (; low < counted.Length && counted[low].Minute <= last; low++)
        {
            if (counted[low].Counts is not { } counts)
            {
                return null;
            }

            total = total.Adding(counts);
            if (counted[low].Model is { } model)
            {
                var sums = models.GetValueOrDefault(model);
                models[model] = (QuotaTokenCounts.Add(sums.Input, counts.Input), QuotaTokenCounts.Add(sums.Output, counts.Output));
            }
            else
            {
                others = (QuotaTokenCounts.Add(others.Input, counts.Input), QuotaTokenCounts.Add(others.Output, counts.Output));
            }
        }

        return QuotaTokenUsage.Of(total, models, others);
    }

    private (long Cost, long Unpriced, long Tokens, double? Rate) Range(string account, string? name, long firstMinute, long lastMinute)
    {
        var series = SeriesOf(account, name);
        if (lastMinute < firstMinute)
        {
            return (0, 0, 0, series.Rate);
        }

        var i = EstimateMath.BisectLeft(series.Minutes, firstMinute);
        var j = EstimateMath.BisectRight(series.Minutes, lastMinute);
        return (series.Cost[j] - series.Cost[i], series.Unpriced[j] - series.Unpriced[i], series.UnpricedTokens[j] - series.UnpricedTokens[i], series.Rate);
    }

    private static long Sum(long[] minutes, long[] cumulative, long firstMinute, long lastMinute)
    {
        if (lastMinute < firstMinute)
        {
            return 0;
        }

        return cumulative[EstimateMath.BisectRight(minutes, lastMinute)] - cumulative[EstimateMath.BisectLeft(minutes, firstMinute)];
    }

    private Series SeriesOf(string account, string? name)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_gate)
        {
            var key = (account, name ?? "");
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var perMinute = new SortedDictionary<long, (long Cost, long Unpriced, long Tokens)>();
            var rates = new Dictionary<string, (long Cost, long Tokens)>(StringComparer.Ordinal);
            var perModel = new Dictionary<string, SortedDictionary<long, long>>(StringComparer.Ordinal);
            var counted = new List<(long Minute, string? Model, QuotaTokenCounts? Counts)>();
            foreach (var (k, row) in _rows)
            {
                if (!string.Equals(k.Account, account, StringComparison.Ordinal) || !ModelMatches(k.Model, name))
                {
                    continue;
                }

                counted.Add((k.Minute, row.PublicModel ? k.Model : null, row.Counts));

                perMinute.TryGetValue(k.Minute, out var v);
                perMinute[k.Minute] = (v.Cost + row.Cost, v.Unpriced + row.Unpriced, v.Tokens + row.UnpricedTokens);
                rates.TryGetValue(k.Model, out var r);
                rates[k.Model] = (r.Cost + row.Cost, r.Tokens + row.PricedTokens);
                if (!perModel.TryGetValue(k.Model, out var byMinute))
                {
                    byMinute = [];
                    perModel.Add(k.Model, byMinute);
                }

                byMinute.TryGetValue(k.Minute, out var c);
                byMinute[k.Minute] = c + row.Cost;
            }

            var n = perMinute.Count;
            var minutes = new long[n];
            var cost = new long[n + 1];
            var unpriced = new long[n + 1];
            var tokens = new long[n + 1];
            var i = 0;
            foreach (var (minute, v) in perMinute)
            {
                minutes[i] = minute;
                cost[i + 1] = cost[i] + v.Cost;
                unpriced[i + 1] = unpriced[i] + v.Unpriced;
                tokens[i + 1] = tokens[i] + v.Tokens;
                i++;
            }

            double? rate = null;
            foreach (var (c, t) in rates.Values)
            {
                if (t > 0 && (rate is null || (double)c / t > rate))
                {
                    rate = (double)c / t;
                }
            }

            var models = new Dictionary<string, (long[] Minutes, long[] Cost)>(StringComparer.Ordinal);
            foreach (var (model, byMinute) in perModel)
            {
                var m = new long[byMinute.Count];
                var sums = new long[byMinute.Count + 1];
                var j = 0;
                foreach (var (minute, c) in byMinute)
                {
                    m[j] = minute;
                    sums[j + 1] = sums[j] + c;
                    j++;
                }

                models.Add(model, (m, sums));
            }

            counted.Sort(static (a, b) => a.Minute.CompareTo(b.Minute));
            var series = new Series(minutes, cost, unpriced, tokens, rate, models, [.. counted]);
            _cache[key] = series;
            return series;
        }
    }

    private static long FloorDiv(long a, long b) => a / b - (a % b != 0 && (a < 0) != (b < 0) ? 1 : 0);

    private static long CeilDiv(long a, long b) => -FloorDiv(-a, b);
}
