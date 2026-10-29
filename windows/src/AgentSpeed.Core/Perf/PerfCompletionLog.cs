using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Work;

namespace AgentSpeed.Core.Perf;

/// <summary>
/// The order in which one agent's responses become measured and its tool calls get their first result (METRICS 6.11
/// <i>Completion order</i>), for a whole-file read. Responses are logged by key and read back with their final values.
/// Not thread-safe.
/// </summary>
internal sealed class PerfCompletionLog
{
    private readonly List<(string? Key, WorkAction Action)> _entries = [];
    private readonly HashSet<string> _measured = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public void Add(in WorkAction action) => _entries.Add((null, action));

    /// <summary>Notes an upsert: the first one that makes the response measured is its completion.</summary>
    public void Upsert(ParsedResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Final && SpeedMath.TryGetSpeedInterval(response, out _) && _measured.Add(response.Key))
        {
            _entries.Add((response.Key, default));
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _measured.Clear();
    }

    /// <summary>The completions in order, each response with its final value from <paramref name="latest"/>.</summary>
    public IEnumerable<PerfCompletion> Resolve(IReadOnlyDictionary<string, ParsedResponse> latest)
    {
        ArgumentNullException.ThrowIfNull(latest);
        foreach (var (key, action) in _entries)
        {
            if (key is null)
            {
                yield return PerfCompletion.Of(action);
            }
            else if (latest.TryGetValue(key, out var response))
            {
                yield return PerfCompletion.Of(response);
            }
        }
    }
}
