using System.Text;

namespace AgentSpeed.Core.Tasks;

/// <summary>
/// The task performance of a set of records (METRICS 6.11 <i>Comparison</i>). Each value is null when its denominator
/// is 0 or there is no value to take a quantile of.
/// </summary>
public record TaskAggregate
{
    public TaskAggregate(int tasks, int scoredTasks, double? medianScore, double? lowerQuartile, double? upperQuartile, int[] histogram,
        TaskNumerators numerators, double? successRate, double? stallRate, double? fixFailureRate, double? recoverAttempts,
        double? timeToFirstPassMs)
    {
        Successes = numerators.Successes;
        Stalls = numerators.Stalls;
        FixFailures = numerators.FixFailures;
        FixAttempts = numerators.FixAttempts;
        Recovered = numerators.Recovered;
        Tasks = tasks;
        ScoredTasks = scoredTasks;
        MedianScore = medianScore;
        MedianShown = medianScore is { } median ? TaskRounding.HalfAwayFromZero(median) : null;
        LowerQuartile = lowerQuartile;
        UpperQuartile = upperQuartile;
        Histogram = histogram;
        SuccessRate = successRate;
        StallRate = stallRate;
        FixFailureRate = fixFailureRate;
        RecoverAttempts = recoverAttempts;
        TimeToFirstPassMs = timeToFirstPassMs;
    }

    public int Tasks { get; }

    /// <summary>Records with a score; the scores below are theirs, unrounded.</summary>
    public int ScoredTasks { get; }

    public double? MedianScore { get; }

    /// <summary><see cref="MedianScore"/> rounded half away from zero.</summary>
    public int? MedianShown { get; }

    public double? LowerQuartile { get; }

    public double? UpperQuartile { get; }

    /// <summary>Ten counts: bins <c>[0, 10)</c> … <c>[80, 90)</c> and <c>[90, 100]</c>.</summary>
    public int[] Histogram { get; }

    /// <summary>Scored records that ended <c>passing</c>: the numerator of <see cref="SuccessRate"/>.</summary>
    public long Successes { get; }

    /// <summary>Records with a stall: the numerator of <see cref="StallRate"/>.</summary>
    public long Stalls { get; }

    /// <summary>Σ fix failures: the numerator of <see cref="FixFailureRate"/>.</summary>
    public long FixFailures { get; }

    /// <summary>Σ fix attempts: the denominator of <see cref="FixFailureRate"/>.</summary>
    public long FixAttempts { get; }

    /// <summary>Σ recovered episodes: the denominator of <see cref="RecoverAttempts"/>.</summary>
    public long Recovered { get; }

    /// <summary><c>success</c> records ÷ scored tasks.</summary>
    public double? SuccessRate { get; }

    /// <summary><c>stalled</c> records ÷ tasks.</summary>
    public double? StallRate { get; }

    /// <summary>Σ fix failures ÷ Σ fix attempts.</summary>
    public double? FixFailureRate { get; }

    /// <summary>Σ recover attempts ÷ Σ recovered: the mean attempts of a recovered episode.</summary>
    public double? RecoverAttempts { get; }

    /// <summary>The median of <c>firstPassMs − startMs</c> over records with a pass.</summary>
    public double? TimeToFirstPassMs { get; }
}

/// <summary>The counts behind a comparison row's rates (METRICS 6.11 <i>Comparison</i>).</summary>
public readonly record struct TaskNumerators(long Successes, long Stalls, long FixFailures, long FixAttempts, long Recovered);

/// <summary>One source's row (<c>bySource</c>).</summary>
public sealed record TaskSourceRow : TaskAggregate
{
    internal TaskSourceRow(SourceKind source, TaskAggregate aggregate)
        : base(aggregate)
    {
        Source = source;
    }

    public SourceKind Source { get; }
}

/// <summary>One model's row (<c>byModel</c>): the model id (empty for unknown) and the sources with its records, in source order.</summary>
public sealed record TaskModelRow : TaskAggregate
{
    internal TaskModelRow(string model, IReadOnlyList<SourceKind> sources, TaskAggregate aggregate)
        : base(aggregate)
    {
        Model = model;
        Sources = sources;
    }

    public string Model { get; }

    public IReadOnlyList<SourceKind> Sources { get; }
}

/// <summary>The comparison over a date range: per source in source order, and per model by tasks descending then id.</summary>
public sealed record TaskComparisonResult(IReadOnlyList<TaskSourceRow> BySource, IReadOnlyList<TaskModelRow> ByModel);

/// <summary><c>compare</c> (METRICS 6.11 <i>Comparison</i>).</summary>
public static class TaskComparison
{
    /// <summary>The source order of METRICS 1.</summary>
    private static readonly SourceKind[] SourceOrder =
    [
        SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.ClaudeDesktop, SourceKind.DeepSeek, SourceKind.OpenCode, SourceKind.Gemini,
        SourceKind.Omp, SourceKind.Antigravity, SourceKind.WorkBuddy, SourceKind.WorkBuddyAi, SourceKind.ZCode, SourceKind.Kimi,
        SourceKind.GrokBuild, SourceKind.Pi, SourceKind.OpenClaw, SourceKind.Hermes, SourceKind.Droid, SourceKind.Trae,
    ];

    private static readonly double[] HistogramEdges = [10, 20, 30, 40, 50, 60, 70, 80, 90];

    /// <summary>
    /// The complete records whose <see cref="TaskRecord.Day"/> lies in <c>[first, last]</c> (compared as strings), of every
    /// source or only of <paramref name="source"/>.
    /// </summary>
    public static TaskComparisonResult Compare(IEnumerable<TaskRecord> records, string first, string last, SourceKind? source = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(last);
        var chosen = new List<TaskRecord>();
        foreach (var record in records)
        {
            if (record.Complete && string.CompareOrdinal(first, record.Day) <= 0 && string.CompareOrdinal(record.Day, last) <= 0
                && (source is null || record.Source == source))
            {
                chosen.Add(record);
            }
        }

        var bySource = new List<TaskSourceRow>();
        foreach (var kind in SourceOrder)
        {
            var part = chosen.FindAll(r => r.Source == kind);
            if (part.Count > 0)
            {
                bySource.Add(new TaskSourceRow(kind, Aggregate(part)));
            }
        }

        var models = new List<string>();
        var parts = new Dictionary<string, List<TaskRecord>>(StringComparer.Ordinal);
        foreach (var record in chosen)
        {
            var model = string.IsNullOrEmpty(record.Model) ? string.Empty : record.Model;
            if (!parts.TryGetValue(model, out var part))
            {
                part = [];
                parts[model] = part;
                models.Add(model);
            }

            part.Add(record);
        }

        var byModel = new List<TaskModelRow>(models.Count);
        foreach (var model in models)
        {
            var part = parts[model];
            var sources = new List<SourceKind>();
            foreach (var kind in SourceOrder)
            {
                if (part.Exists(r => r.Source == kind))
                {
                    sources.Add(kind);
                }
            }

            byModel.Add(new TaskModelRow(model, sources.AsReadOnly(), Aggregate(part)));
        }

        byModel.Sort(static (a, b) => a.Tasks != b.Tasks ? b.Tasks.CompareTo(a.Tasks) : Utf8Compare(a.Model, b.Model));
        return new TaskComparisonResult(bySource.AsReadOnly(), byModel.AsReadOnly());
    }

    /// <summary>
    /// <c>q(p)</c>: over the values sorted ascending, <c>h = (n − 1)·p</c>, <c>i = ⌊h⌋</c>; <c>v[i]</c> when <c>h = i</c>, else
    /// <c>v[i] + (h − i)·(v[i + 1] − v[i])</c>. Null for no values.
    /// </summary>
    public static double? Quantile(IReadOnlyList<double> values, double p)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = new double[values.Count];
        for (var k = 0; k < sorted.Length; k++)
        {
            sorted[k] = values[k];
        }

        Array.Sort(sorted);
        var h = (sorted.Length - 1) * p;
        var i = (int)Math.Floor(h);
        var fraction = h - i;
        return fraction == 0 ? sorted[i] : sorted[i] + (fraction * (sorted[i + 1] - sorted[i]));
    }

    /// <summary>Ten bins: a score's bin is the number of the edges 10, 20, … 90 it reaches, compared exactly.</summary>
    public static int[] Histogram(IEnumerable<double> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        var bins = new int[HistogramEdges.Length + 1];
        foreach (var score in scores)
        {
            var bin = 0;
            foreach (var edge in HistogramEdges)
            {
                if (score >= edge)
                {
                    bin++;
                }
            }

            bins[bin]++;
        }

        return bins;
    }

    private static TaskAggregate Aggregate(List<TaskRecord> records)
    {
        var scores = new List<double>();
        var successes = 0;
        var stalled = 0;
        long fixAttempts = 0;
        long fixFailures = 0;
        long recovered = 0;
        long recoverAttempts = 0;
        var firstPass = new List<double>();
        foreach (var record in records)
        {
            if (record.Score is { } score)
            {
                scores.Add(score);
                if (record.Success)
                {
                    successes++;
                }
            }

            if (record.Stalled)
            {
                stalled++;
            }

            fixAttempts += record.Counts.FixAttempts;
            fixFailures += record.Counts.FixFailures;
            recovered += record.Counts.Recovered;
            recoverAttempts += record.Counts.RecoverAttempts;
            if (record.FirstPassMs is { } pass)
            {
                firstPass.Add(pass - record.StartMs);
            }
        }

        return new TaskAggregate(
            records.Count,
            scores.Count,
            Quantile(scores, 0.5),
            Quantile(scores, 0.25),
            Quantile(scores, 0.75),
            Histogram(scores),
            new TaskNumerators(successes, stalled, fixFailures, fixAttempts, recovered),
            scores.Count > 0 ? (double)successes / scores.Count : null,
            records.Count > 0 ? (double)stalled / records.Count : null,
            fixAttempts > 0 ? (double)fixFailures / fixAttempts : null,
            recovered > 0 ? (double)recoverAttempts / recovered : null,
            Quantile(firstPass, 0.5));
    }

    /// <summary>Model ids ordered by their UTF-8 bytes.</summary>
    private static int Utf8Compare(string a, string b) =>
        Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));
}
