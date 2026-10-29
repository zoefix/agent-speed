using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Perf;

namespace AgentSpeed.Core.History;

/// <summary>
/// Performance records (METRICS 6.11): every analysed file stores its agents' records with its contributions, from their
/// completions in the order they complete, each response with its final values.
/// </summary>
internal sealed partial class HistoryAnalyzer
{
    /// <summary>Every valid stored performance record of every file, by day, source and model.</summary>
    public IReadOnlyList<PerfRecord> PerfRecords()
    {
        var records = new List<PerfRecord>();
        foreach (var entry in _entries.Values)
        {
            records.AddRange(entry.PerfRecords);
        }

        records.Sort(PerfRecord.Compare);
        return records.AsReadOnly();
    }

    /// <param name="onlyAgent">Only the responses of this agent (a Hermes Agent log holds several).</param>
    private static IReadOnlyList<PerfRecord> PerfRecordsOf(PerfCompletionLog? completions, IReadOnlyDictionary<string, ParsedResponse> latest,
        string agentId, SourceKind source, DayLabeler labeler, string? onlyAgent = null)
    {
        if (completions is not { Count: > 0 })
        {
            return [];
        }

        var recorder = new PerfRecorder(labeler.Label);
        foreach (var completion in completions.Resolve(latest))
        {
            if (onlyAgent is null || completion.Response is not { } response || string.Equals(response.AgentId, onlyAgent, StringComparison.Ordinal))
            {
                recorder.Add(agentId, source, completion);
            }
        }

        return recorder.Records();
    }
}
