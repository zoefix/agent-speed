namespace AgentSpeed.Core.Work;

/// <summary>
/// The actions of one file (METRICS 6.10): the last <see cref="MaxActions"/> in file order (every one for a whole-file
/// read); results find their call by id, and only the first result of a call counts. Not thread-safe.
/// </summary>
/// <param name="capacity">The number of actions kept; null keeps every action.</param>
internal sealed class WorkLog(int? capacity = WorkLog.MaxActions)
{
    public const int MaxActions = 64;

    private readonly List<(string CallId, WorkAction Action)> _entries = new(Math.Min(capacity ?? 16, MaxActions) + 1);
    private readonly Dictionary<string, long> _sequenceById = new(StringComparer.Ordinal);
    private long _firstSequence;
    private WorkAction[]? _actions;

    /// <summary>Called with a call when its first result is read.</summary>
    public Action<WorkAction>? Completed { get; init; }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<WorkAction> Actions => _actions ??= _entries.ConvertAll(e => e.Action).ToArray();

    /// <summary>Logs a line's calls, then its results; true when the log changed.</summary>
    /// <param name="model">The agent's model after the line was read: the model of the response that issued its calls.</param>
    /// <param name="seeded">The line was read while seeding.</param>
    public bool Apply(in LineTools tools, long atMs, string? model = null, bool seeded = false)
    {
        var changed = false;
        foreach (var call in tools.Calls ?? [])
        {
            changed |= Call(call, atMs, model, seeded);
        }

        foreach (var outcome in tools.Outcomes ?? [])
        {
            changed |= Outcome(outcome, atMs);
        }

        return changed;
    }

    /// <summary>Adds a call unless its id is already logged or it names no tool; true when the log changed.</summary>
    public bool Call(in ToolCall call, long atMs, string? model = null, bool seeded = false)
    {
        if (call.Tool is not { } tool || _sequenceById.ContainsKey(call.Id))
        {
            return false;
        }

        _sequenceById[call.Id] = _firstSequence + _entries.Count;
        _entries.Add((call.Id, new WorkAction(atMs, tool, call.Digest) { Model = model, Seeded = seeded }));
        if (capacity is { } cap && _entries.Count > cap)
        {
            _sequenceById.Remove(_entries[0].CallId);
            _entries.RemoveAt(0);
            _firstSequence++;
        }

        _actions = null;
        return true;
    }

    /// <summary>
    /// The first result of a logged call sets its result time and outcome (unknown stays unknown); a later result, or one
    /// whose id names no call, changes nothing. True when the log changed.
    /// </summary>
    public bool Outcome(in ToolOutcome outcome, long atMs)
    {
        if (!_sequenceById.TryGetValue(outcome.Id, out var sequence))
        {
            return false;
        }

        var index = (int)(sequence - _firstSequence);
        var (id, action) = _entries[index];
        if (action.ResultMs is not null)
        {
            return false;
        }

        action = action with { ResultMs = atMs, Failed = outcome.Failed };
        _entries[index] = (id, action);
        _actions = null;
        Completed?.Invoke(action);
        return true;
    }

    public void Clear()
    {
        _entries.Clear();
        _sequenceById.Clear();
        _firstSequence = 0;
        _actions = null;
    }
}
