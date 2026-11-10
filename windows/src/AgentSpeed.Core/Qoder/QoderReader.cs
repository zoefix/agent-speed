using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Qoder;

/// <summary>One chat session of a Qoder IDE database as the engine sees it (METRICS 4.18 <i>Agents and phase</i>).</summary>
/// <param name="CwdPath">The path of the session's <c>project_uri</c> (METRICS 10.6); null when it has none.</param>
/// <param name="ParentId">
/// <c>&lt;database path&gt;#&lt;parent_session_id&gt;</c> of a sub-agent session; it links only while that session is tracked (6.7).
/// </param>
internal sealed record QoderAgent(
    string Id, string SessionId, string Project, string? CwdPath, string? Model, string? ParentId, AgentPhase Phase,
    long? InFlightStartMs, long? LastLineMs, int MalformedRows)
{
    public bool IsSubagent => ParentId is not null;
}

/// <summary>What one read of a database changed.</summary>
internal sealed class QoderUpdate
{
    public List<(ParsedResponse Response, bool Completion)> Responses { get; } = [];

    /// <summary>Agent id → state, for agents whose state may have changed.</summary>
    public Dictionary<string, QoderAgent> Agents { get; } = new(StringComparer.Ordinal);

    public List<string> Removed { get; } = [];

    /// <summary>A startup read (the database is new, replaced or read again): it counts but raises no live event.</summary>
    public bool Seeded { get; set; }

    public bool Changed => Responses.Count > 0 || Agents.Count > 0 || Removed.Count > 0;
}

internal enum QoderStatus : byte
{
    Missing,
    Ready,
    Unsupported,
    Unreadable,
}

/// <summary>
/// The live state of one Qoder IDE database (METRICS 4.18): its chat sessions as agents, read incrementally by <c>rowid</c>, and
/// the turn records of the requests whose agents wait for tools. Not thread-safe: owned by the monitor thread.
/// </summary>
/// <param name="claims">Shared with the source's other databases: a session an earlier one lists is skipped here (METRICS 2.1).</param>
/// <param name="position">This database's position in the list, its claim owner.</param>
internal sealed class QoderReader(string path, SessionClaims claims, int position)
{
    /// <summary>A call is read once, so older emitted calls are forgotten once there are many.</summary>
    private const int EmittedLimit = 4096;
    private const long EmittedWindowMs = 2 * 3_600_000;

    private readonly Dictionary<string, QoderSession> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _listed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionState> _states = new(StringComparer.Ordinal);
    private Dictionary<string, ParsedResponse> _emitted = new(StringComparer.Ordinal);
    private AntigravityStamp? _stamp;
    private FileIdentity? _identity;
    private long? _cursor;
    private bool _needsSeed = true;

    public string Path { get; } = path;

    public QoderStatus Status { get; private set; } = QoderStatus.Missing;

    public bool Exists => Status != QoderStatus.Missing;

    public string WatchDirectory => Exists ? System.IO.Path.GetDirectoryName(Path) ?? string.Empty : string.Empty;

    public long DatabaseBytes => _stamp?.Length ?? 0;

    public int MalformedRows => _states.Values.Sum(s => s.Malformed);

    public IEnumerable<string> AgentIds => _states.Keys.Select(AgentId);

    public IEnumerable<QoderAgent> Agents => _states.Select(pair => pair.Value.Snapshot(AgentId(pair.Key), pair.Key));

    public string AgentId(string session) => Path + "#" + session;

    public bool IsWatchedPath(string changed) =>
        string.Equals(changed, Path, LocationPaths.Comparison) || string.Equals(changed, Path + QoderPaths.WalSuffix, LocationPaths.Comparison);

    /// <summary>Today's sessions: sessions with a row today, sub-agent sessions included (DESIGN 5).</summary>
    public int ActiveSessionCount(long todayStartMs) => _states.Values.Count(s => s.LastLineMs >= todayStartMs);

    public void Release() => claims.ReleaseAll(position);

    /// <summary>
    /// Reads what changed: the startup window when the database is new or was replaced (<see cref="QoderUpdate.Seeded"/>), else
    /// the rows above the highest <c>rowid</c> seen, the sessions not known yet and the turn records of waiting requests. Null when
    /// nothing changed.
    /// </summary>
    public QoderUpdate? Refresh(long todayStartMs, IDeduper deduper)
    {
        if (!QoderPaths.IsRegularFile(Path))
        {
            if (!Exists)
            {
                return null;
            }

            var gone = new QoderUpdate();
            gone.Removed.AddRange(AgentIds);
            Reset();
            Status = QoderStatus.Missing;
            return gone;
        }

        var stamp = AntigravityStamp.Of(Path);
        // The identity too: a file swapped in with the same size and time is still another database.
        var identity = ReadIdentity(Path);
        if (!_needsSeed && stamp == _stamp && identity == _identity && Status is QoderStatus.Ready or QoderStatus.Unsupported)
        {
            return null;
        }

        // Another file at the path (a new identity) or a database that shrank is read again like at startup.
        var replaced = _stamp is { } known && stamp is { } current
            && ((_identity is { } before && identity is { } after && before != after) || current.DatabaseLength < known.DatabaseLength);

        var update = new QoderUpdate();
        if (replaced)
        {
            update.Removed.AddRange(AgentIds);
            Reset();
        }

        try
        {
            using var database = QoderDatabase.Open(Path);
            var seeding = _needsSeed;
            var read = database.Snapshot(() => Read(database, seeding, todayStartMs));
            Status = QoderStatus.Ready;
            _needsSeed = false;
            update.Seeded = seeding;
            if (read.Sessions is { } sessions)
            {
                ReadSessions(sessions);
            }

            foreach (var row in read.Rows)
            {
                Consume(row, read, deduper, update);
            }

            Finish(read.Finished, update);
            _cursor = Math.Max(Math.Max(_cursor ?? 0, read.Top), read.Rows.Count > 0 ? read.Rows[^1].RowId : 0);
            if (_emitted.Count > EmittedLimit && read.Rows.Count > 0 && read.Rows[^1].TimeMs is { } newest)
            {
                _emitted = _emitted.Where(pair => pair.Value.EndMs >= newest - EmittedWindowMs)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            }

            _stamp = stamp;
            _identity = identity;
        }
        catch (InvalidDataException)
        {
            Status = QoderStatus.Unsupported;
            _stamp = stamp;
            _identity = identity;
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            Status = QoderStatus.Unreadable;
        }

        // Removed agents read again in the same pass stay.
        update.Removed.RemoveAll(update.Agents.ContainsKey);
        return update;
    }

    private sealed record Batch(
        List<QoderRow> Rows, List<QoderSession>? Sessions, long Top, Dictionary<string, long?> Previous, Dictionary<string, long> Finished);

    /// <summary>
    /// One snapshot: the rows; the <c>chat_session</c> rows at startup and when a row names a session not listed so far; the time of
    /// the row just below a newly read session's first row (its first call starts there, even before the startup window); and the
    /// turn records of the requests the rows and the waiting agents name.
    /// </summary>
    private Batch Read(QoderDatabase database, bool seeding, long todayStartMs)
    {
        var rows = seeding ? database.RecentRows(todayStartMs - Monitoring.MonitorCore.BacklogWindowMs) : database.RowsAfter(_cursor ?? 0);
        var sessions = seeding || rows.Exists(row => row.SessionId is { } id && !_listed.Contains(id)) ? database.Sessions() : null;
        var listed = sessions is null ? _listed : sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var first = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.SessionId is { } id && listed.Contains(id) && !_states.ContainsKey(id))
            {
                first.TryAdd(id, row.RowId);
            }
        }

        var requests = rows.Where(row => row.Role == QoderRole.Assistant && row.RequestId is not null).Select(row => row.RequestId!)
            .Concat(_states.Values.Select(state => state.PendingRequest).OfType<string>());
        return new Batch(rows, sessions, database.MaxRowId(), database.PreviousTimes(first), database.FinishedAt(requests));
    }

    private void Reset()
    {
        Release();
        _sessions.Clear();
        _listed.Clear();
        _states.Clear();
        _emitted = new Dictionary<string, ParsedResponse>(StringComparer.Ordinal);
        _cursor = null;
        _stamp = null;
        _identity = null;
        _needsSeed = true;
    }

    /// <summary>The listed sessions this database reads: claimed in list order; a known session keeps what was read first.</summary>
    private void ReadSessions(List<QoderSession> sessions)
    {
        _listed.Clear();
        var next = new Dictionary<string, QoderSession>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            _listed.Add(session.Id);
            if (!next.ContainsKey(session.Id) && claims.Claim(session.Id, position))
            {
                next[session.Id] = _sessions.TryGetValue(session.Id, out var known) ? known : session;
            }
        }

        foreach (var id in _sessions.Keys.Where(id => !next.ContainsKey(id)))
        {
            claims.Release(id, position);
        }

        _sessions.Clear();
        foreach (var (id, session) in next)
        {
            _sessions[id] = session;
        }
    }

    private void Consume(QoderRow row, Batch batch, IDeduper deduper, QoderUpdate update)
    {
        if (row.SessionId is not { } id || !_sessions.TryGetValue(id, out var session))
        {
            return;
        }

        if (!_states.TryGetValue(id, out var state))
        {
            state = new SessionState(session, Path, batch.Previous.GetValueOrDefault(id));
            _states[id] = state;
        }

        var agentId = AgentId(id);
        var start = state.PreviousMs;
        state.PreviousMs = row.TimeMs;
        state.Note(row.TimeMs);
        if (row.IsMalformed)
        {
            state.Malformed++;
        }
        else if (row.Role == QoderRole.Assistant)
        {
            var time = row.TimeMs!.Value;
            if (QoderCalls.Response(row, id, time, start, agentId) is { } response)
            {
                var claimed = deduper.TryClaim(response.Key, Path, out var owner);
                if (claimed || string.Equals(owner, Path, StringComparison.Ordinal))
                {
                    state.Called(response.Model);
                    if (!_emitted.TryGetValue(response.Key, out var previous) || previous != response)
                    {
                        _emitted[response.Key] = response;
                        update.Responses.Add((response, claimed));
                    }
                }
            }

            var finished = row.RequestId is { } request && batch.Finished.TryGetValue(request, out var at) && at >= time;
            state.SetPhase(finished ? AgentPhase.Idle : AgentPhase.Tools, time, finished ? null : row.RequestId);
        }
        else if (row.Role is QoderRole.User or QoderRole.Tool)
        {
            state.SetPhase(AgentPhase.Generating, row.TimeMs!.Value, null);
        }

        update.Agents[agentId] = state.Snapshot(agentId, id);
    }

    /// <summary>A turn that finished since its assistant row was read moves the agent to <c>idle</c> (METRICS 4.18 <i>Phase</i>).</summary>
    private void Finish(Dictionary<string, long> finished, QoderUpdate update)
    {
        foreach (var (id, state) in _states)
        {
            if (state.PendingRequest is { } request && finished.TryGetValue(request, out var at) && at >= state.PhaseRowMs)
            {
                state.Finished();
                update.Agents[AgentId(id)] = state.Snapshot(AgentId(id), id);
            }
        }
    }

    private static FileIdentity? ReadIdentity(string file)
    {
        try
        {
            using var handle = TranscriptTailer.Open(file);
            return FileIdentity.From(handle);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <param name="previousMs">The time of the row just below the session's first row read; null when there is none or it has no time.</param>
    private sealed class SessionState(QoderSession session, string database, long? previousMs)
    {
        /// <summary>The last path component of the project path, else the session id (never the database's folder).</summary>
        public string Project { get; } = session.ProjectPath is { } project && PathText.Basename(project) is { Length: > 0 } name ? name : session.Id;

        public string? CwdPath { get; } = session.ProjectPath;

        public string? ParentId { get; } = session.ParentSessionId is { } parent ? database + "#" + parent : null;

        public string? Model { get; private set; }

        public AgentPhase Phase { get; private set; } = AgentPhase.Idle;

        /// <summary>The time of the row that last set the phase.</summary>
        public long? PhaseRowMs { get; private set; }

        public long? InFlightStartMs { get; private set; }

        /// <summary>The request of the assistant row that set <see cref="AgentPhase.Tools"/>, until its turn record says it finished.</summary>
        public string? PendingRequest { get; private set; }

        public long? LastLineMs { get; private set; }

        /// <summary>The time of the session's latest row read: the start of its next call.</summary>
        public long? PreviousMs { get; set; } = previousMs;

        public int Malformed { get; set; }

        /// <summary>The model of the latest call that has one.</summary>
        public void Called(string? model) => Model = model ?? Model;

        /// <summary>A row whose time is earlier than the one that last set the phase changes nothing (a stored copy never moves it back).</summary>
        public void SetPhase(AgentPhase phase, long time, string? request)
        {
            if (PhaseRowMs is { } since && time < since)
            {
                return;
            }

            Phase = phase;
            PhaseRowMs = time;
            InFlightStartMs = phase == AgentPhase.Generating ? time : null;
            PendingRequest = phase == AgentPhase.Tools ? request : null;
        }

        public void Finished()
        {
            Phase = AgentPhase.Idle;
            InFlightStartMs = null;
            PendingRequest = null;
        }

        public void Note(long? ms)
        {
            if (ms is { } time && (LastLineMs is not { } last || time > last))
            {
                LastLineMs = time;
            }
        }

        public QoderAgent Snapshot(string id, string sessionId) =>
            new(id, sessionId, Project, CwdPath, Model, ParentId, Phase, InFlightStartMs, LastLineMs, Malformed);
    }
}
