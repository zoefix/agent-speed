using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Locations;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Devin;

/// <summary>One session of a CLI database as the engine sees it (METRICS 4.17 <i>Agents and phase</i>).</summary>
/// <param name="CwdPath">The session's working directory (METRICS 10.6); null when empty.</param>
internal sealed record DevinAgent(
    string Id, string SessionId, string Project, string? CwdPath, string? Model, AgentPhase Phase, long? InFlightStartMs,
    long? LastLineMs, int MalformedRows);

/// <summary>What one read of a database changed.</summary>
internal sealed class DevinUpdate
{
    public List<(ParsedResponse Response, bool Completion)> Responses { get; } = [];

    /// <summary>Agent id → state, for agents whose state may have changed.</summary>
    public Dictionary<string, DevinAgent> Agents { get; } = new(StringComparer.Ordinal);

    public List<string> Removed { get; } = [];

    /// <summary>A startup read (the database is new, replaced or read again): it counts but raises no live event.</summary>
    public bool Seeded { get; set; }

    public bool Changed => Responses.Count > 0 || Agents.Count > 0 || Removed.Count > 0;
}

internal enum DevinStatus : byte
{
    Missing,
    Ready,
    Unsupported,
    Unreadable,
}

/// <summary>
/// The live state of one Devin CLI database (METRICS 4.17): its sessions as agents, read incrementally by <c>row_id</c>. Not
/// thread-safe: owned by the monitor thread.
/// </summary>
/// <param name="claims">Shared with the source's other databases: a session an earlier one reads is skipped here (METRICS 2.1).</param>
/// <param name="position">This database's position in the list, its claim owner.</param>
internal sealed class DevinCliReader(string path, SessionClaims claims, int position)
{
    /// <summary>Copies of a call are written together, so older emitted calls are forgotten once there are many.</summary>
    private const int EmittedLimit = 4096;
    private const long EmittedWindowMs = 2 * 3_600_000;

    private readonly Dictionary<string, DevinSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionState> _states = new(StringComparer.Ordinal);
    private Dictionary<string, ParsedResponse> _emitted = new(StringComparer.Ordinal);
    private AntigravityStamp? _stamp;
    private FileIdentity? _identity;
    private long? _cursor;
    private bool _needsSeed = true;

    public string Path { get; } = path;

    public DevinStatus Status { get; private set; } = DevinStatus.Missing;

    public bool Exists => Status != DevinStatus.Missing;

    public string WatchDirectory => Exists ? System.IO.Path.GetDirectoryName(Path) ?? string.Empty : string.Empty;

    public long DatabaseBytes => _stamp?.Length ?? 0;

    public int MalformedRows => _states.Values.Sum(s => s.Malformed);

    public IEnumerable<string> AgentIds => _states.Keys.Select(AgentId);

    public IEnumerable<DevinAgent> Agents => _states.Select(pair => pair.Value.Snapshot(AgentId(pair.Key), pair.Key));

    public string AgentId(string session) => Path + "#" + session;

    public bool IsWatchedPath(string changed) =>
        string.Equals(changed, Path, LocationPaths.Comparison) || string.Equals(changed, Path + DevinPaths.WalSuffix, LocationPaths.Comparison);

    /// <summary>Today's sessions: sessions with a row today (DESIGN 5).</summary>
    public int ActiveSessionCount(long todayStartMs) => _states.Values.Count(s => s.LastLineMs >= todayStartMs);

    public void Release() => claims.ReleaseAll(position);

    /// <summary>
    /// Reads what changed: the startup window when the database is new or was replaced (<see cref="DevinUpdate.Seeded"/>), else
    /// the rows above the highest <c>row_id</c> seen and the <c>sessions</c> table. Null when nothing changed.
    /// </summary>
    public DevinUpdate? Refresh(long todayStartMs, IDeduper deduper)
    {
        if (!DevinPaths.IsRegularFile(Path))
        {
            if (!Exists)
            {
                return null;
            }

            var gone = new DevinUpdate();
            gone.Removed.AddRange(AgentIds);
            Reset();
            Status = DevinStatus.Missing;
            return gone;
        }

        var stamp = AntigravityStamp.Of(Path);
        // The identity too: a file swapped in with the same size and time is still another database.
        var identity = ReadIdentity(Path);
        if (!_needsSeed && stamp == _stamp && identity == _identity && Status is DevinStatus.Ready or DevinStatus.Unsupported)
        {
            return null;
        }

        // Another file at the path (a new identity) or a database that shrank is read again like at startup.
        var replaced = _stamp is { } known && stamp is { } current
            && ((_identity is { } before && identity is { } after && before != after) || current.DatabaseLength < known.DatabaseLength);

        var update = new DevinUpdate();
        if (replaced)
        {
            update.Removed.AddRange(AgentIds);
            Reset();
        }

        try
        {
            using var database = DevinDatabase.Open(Path);
            var seeding = _needsSeed;
            var (sessions, rows, top) = database.Snapshot(() => (
                database.Sessions(),
                seeding ? database.RecentRows(todayStartMs - Monitoring.MonitorCore.BacklogWindowMs) : database.RowsAfter(_cursor ?? 0),
                database.MaxRowId()));
            Status = DevinStatus.Ready;
            _needsSeed = false;
            update.Seeded = seeding;
            ReadSessions(sessions, update);
            foreach (var row in rows)
            {
                Consume(row, deduper, update);
            }

            _cursor = Math.Max(Math.Max(_cursor ?? 0, top), rows.Count > 0 ? rows[^1].RowId : 0);
            if (_emitted.Count > EmittedLimit && rows.Count > 0 && rows[^1].TimeMs is { } newest)
            {
                _emitted = _emitted.Where(pair => pair.Value.EndMs >= newest - EmittedWindowMs)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            }

            _stamp = stamp;
            _identity = identity;
        }
        catch (InvalidDataException)
        {
            Status = DevinStatus.Unsupported;
            _stamp = stamp;
            _identity = identity;
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            Status = DevinStatus.Unreadable;
        }

        // Removed agents read again in the same pass stay.
        update.Removed.RemoveAll(update.Agents.ContainsKey);
        return update;
    }

    private void Reset()
    {
        Release();
        _sessions.Clear();
        _states.Clear();
        _emitted = new Dictionary<string, ParsedResponse>(StringComparer.Ordinal);
        _cursor = null;
        _stamp = null;
        _identity = null;
        _needsSeed = true;
    }

    private void ReadSessions(List<DevinSession> rows, DevinUpdate update)
    {
        var next = new Dictionary<string, DevinSession>(StringComparer.Ordinal);
        foreach (var session in rows)
        {
            if (claims.Claim(session.Id, position))
            {
                next[session.Id] = session;
            }
        }

        foreach (var id in _sessions.Keys.Where(id => !next.ContainsKey(id)))
        {
            claims.Release(id, position);
        }

        foreach (var (id, session) in next)
        {
            if (_states.TryGetValue(id, out var state) && (!_sessions.TryGetValue(id, out var known) || known != session))
            {
                state.Apply(session);
                update.Agents[AgentId(id)] = state.Snapshot(AgentId(id), id);
            }
        }

        _sessions.Clear();
        foreach (var (id, session) in next)
        {
            _sessions[id] = session;
        }
    }

    private void Consume(DevinRow row, IDeduper deduper, DevinUpdate update)
    {
        if (row.SessionId is not { } id || !_sessions.TryGetValue(id, out var session))
        {
            return;
        }

        if (!_states.TryGetValue(id, out var state))
        {
            state = new SessionState();
            state.Apply(session);
            _states[id] = state;
        }

        var agentId = AgentId(id);
        if (row.TimeMs is not { } time || row.Message is not { } message)
        {
            state.Malformed++;
            state.Note(row.TimeMs);
        }
        else
        {
            state.Note(time);
            if (DevinCalls.Response(message, id, row.RowId, time, session.Model, agentId) is { } response)
            {
                var claimed = deduper.TryClaim(response.Key, Path, out var owner);
                if (claimed || string.Equals(owner, Path, StringComparison.Ordinal))
                {
                    state.Called(response.Model, session);
                    if (!_emitted.TryGetValue(response.Key, out var previous) || previous != response)
                    {
                        _emitted[response.Key] = response;
                        update.Responses.Add((response, claimed));
                    }
                }
            }

            // A stored copy never moves the phase back: a row older than the one that set it changes nothing.
            if (message.Phase is { } phase && time >= (state.PhaseRowMs ?? long.MinValue))
            {
                state.PhaseRowMs = time;
                state.Phase = phase;
                state.InFlightStartMs = phase == AgentPhase.Generating ? time : null;
            }
        }

        update.Agents[agentId] = state.Snapshot(agentId, id);
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

    private sealed class SessionState
    {
        private bool _called;
        private string? _callModel;

        public string Project { get; private set; } = string.Empty;

        public string? CwdPath { get; private set; }

        public string? Model { get; private set; }

        public AgentPhase Phase { get; set; } = AgentPhase.Idle;

        public long? PhaseRowMs { get; set; }

        public long? InFlightStartMs { get; set; }

        public long? LastLineMs { get; private set; }

        public int Malformed { get; set; }

        /// <summary>
        /// Project and working directory from <c>working_directory</c>; a session without one is named by its id, never by the
        /// database's folder (METRICS 4.17).
        /// </summary>
        public void Apply(DevinSession session)
        {
            var name = session.WorkingDirectory is { } directory ? PathText.Basename(directory) : string.Empty;
            Project = name.Length > 0 ? name : session.Id;
            CwdPath = session.WorkingDirectory;
            Model = _called ? _callModel ?? session.Model : session.Model;
        }

        /// <summary>The latest call's model, else the session model.</summary>
        public void Called(string? model, DevinSession session)
        {
            _called = true;
            _callModel = model;
            Model = model ?? session.Model;
        }

        public void Note(long? ms)
        {
            if (ms is { } time && (LastLineMs is not { } last || time > last))
            {
                LastLineMs = time;
            }
        }

        public DevinAgent Snapshot(string id, string session) =>
            new(id, session, Project, CwdPath, Model, Phase, InFlightStartMs, LastLineMs, Malformed);
    }
}
