using AgentSpeed.Core.Engine;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Parsing;
using System.Globalization;

namespace AgentSpeed.Core.Antigravity;

/// <summary>WAL-aware polling of one client's conversation databases. Retains metrics only.</summary>
internal sealed class AntigravitySource(SourceKind kind, string root)
{
    private readonly Dictionary<string, Stamp> _stamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ParsedResponse> _responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentInfo> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AgentInfo> _publishedAgents = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Activity> _activity = new(StringComparer.Ordinal);
    private Stamp? _activityStamp;
    private bool _activityUnreadable;
    private readonly Dictionary<string, int> _malformed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unsupported = new(StringComparer.OrdinalIgnoreCase);

    public SourceKind Kind { get; } = kind;
    public string Root { get; } = root;
    public bool Available => Directory.Exists(Root);
    public int Unreadable => _unreadable.Count + (_activityUnreadable ? 1 : 0);
    public bool Unsupported => _unsupported.Count > 0;
    public int Malformed => _malformed.Values.Sum();
    public int SessionCount(long since) => _agents.Values.Count(a => a.LastLineMs >= since);

    public bool Refresh(long todayStart, Action<AgentInfo> onAgent, Action<ParsedResponse, bool> onResponse)
    {
        var changed = false;
        RefreshActivity();
        foreach (var path in Paths(Root))
        {
            try
            {
                var stamp = FileStamp(path);
                if (_stamps.TryGetValue(path, out var old) && old == stamp) continue;
                // Historic closed databases belong to HistoryAnalyzer, not the startup speed backlog.
                if (!_stamps.ContainsKey(path) && stamp.ModifiedMs < todayStart - 15 * 60_000) continue;
                var read = AntigravityDatabase.Read(path, Kind);
                _agents[path] = read.Agent;
                foreach (var response in read.Responses)
                {
                    if (response.EndMs < todayStart - 15 * 60_000) continue;
                    var previous = _responses.GetValueOrDefault(response.Key);
                    if (previous == response) continue;
                    _responses[response.Key] = response;
                    onResponse(response, previous is null);
                    changed = true;
                }
                _malformed[path] = read.Malformed;
                _unreadable.Remove(path);
                _unsupported.Remove(path);
                // Use the stamp BEFORE reading: a concurrent commit is picked up on the next poll.
                _stamps[path] = stamp;
            }
            catch (SqliteException e)
            {
                if (e.PrimaryCode == SqliteNative.Error) _unsupported.Add(path);
                else _unreadable.Add(path);
                changed = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _unreadable.Add(path);
                changed = true;
            }
        }

        // Summary writes can precede planner/database writes. Publish this overlay even when the
        // conversation DB/WAL stamp did not change, including the transition back to idle.
        foreach (var (path, recorded) in _agents)
        {
            var agent = WithActivity(recorded, _activity.GetValueOrDefault(Path.GetFileNameWithoutExtension(path)));
            if (_publishedAgents.GetValueOrDefault(path) == agent) continue;
            _publishedAgents[path] = agent;
            onAgent(agent);
            changed = true;
        }
        return changed;
    }

    private void RefreshActivity()
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Root));
        var path = parent is null ? string.Empty : Path.Combine(parent, "conversation_summaries.db");
        if (!File.Exists(path))
        {
            _activity.Clear();
            _activityStamp = null;
            _activityUnreadable = false;
            return;
        }
        try
        {
            var stamp = FileStamp(path);
            if (_activityStamp == stamp) return;
            using var db = SqliteConnection.Open(path, SqliteOpenMode.ReadOnly, busyTimeoutMs: 100);
            using var rows = db.Prepare("SELECT conversation_id, status, last_modified_time, last_user_input_time, killed FROM conversation_summaries");
            var next = new Dictionary<string, Activity>(StringComparer.Ordinal);
            while (rows.Step())
            {
                if (rows.GetString(0) is not { } id || ParseTime(rows.GetString(2)) is not { } updated) continue;
                next[id] = new Activity(rows.GetString(1) == "CASCADE_RUN_STATUS_RUNNING", rows.GetInt64(4) != 0,
                    updated, ParseTime(rows.GetString(3)) ?? updated);
            }
            _activity = next;
            _activityStamp = stamp;
            _activityUnreadable = false;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            // An unreadable activity source must not leave a cached "generating" estimate running.
            _activity.Clear();
            _activityStamp = null;
            _activityUnreadable = true;
        }
    }

    private static AgentInfo WithActivity(AgentInfo agent, Activity? activity)
    {
        if (activity is null) return agent;
        if (activity.Killed || !activity.Running && activity.UpdatedMs >= (agent.LastLineMs ?? 0))
            return agent with { Phase = AgentPhase.Idle, InFlightStartMs = null, LastLineMs = activity.UpdatedMs };
        if (!activity.Running || agent.Phase != AgentPhase.Idle) return agent;
        var start = Math.Max(agent.LastLineMs ?? 0, activity.InputMs);
        return agent with
        {
            Phase = AgentPhase.Generating,
            InFlightStartMs = start,
            LastLineMs = Math.Max(agent.LastLineMs ?? 0, activity.UpdatedMs),
        };
    }

    private static long? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time.ToUnixTimeMilliseconds() : null;

    private sealed record Activity(bool Running, bool Killed, long UpdatedMs, long InputMs);

    public static IEnumerable<string> Paths(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetFiles(root, "*.db") : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static Stamp FileStamp(string path)
    {
        var db = new FileInfo(path);
        var wal = new FileInfo(path + "-wal");
        return new Stamp(db.Length, db.LastWriteTimeUtc.Ticks, wal.Exists ? wal.Length : 0, wal.Exists ? wal.LastWriteTimeUtc.Ticks : 0);
    }

    internal readonly record struct Stamp(long Size, long Modified, long WalSize, long WalModified)
    {
        public long ModifiedMs => new DateTimeOffset(Math.Max(Modified, WalModified), TimeSpan.Zero).ToUnixTimeMilliseconds();
    }
}
