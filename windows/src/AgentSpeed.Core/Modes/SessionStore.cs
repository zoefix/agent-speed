using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.IO.Sqlite;

namespace AgentSpeed.Core;

/// <summary>
/// A session-current value in another program's database (EXECUTION-MODES, Current values). The file is opened read-only for
/// one short read and closed at once, and only for sessions not read yet or after the file or its <c>-wal</c> changed: no
/// connection is ever kept, and a file, table, column or row that is missing gives nothing.
/// </summary>
/// <param name="sql">One query with the session id as its parameter and the value as its first column.</param>
/// <param name="parse">The value's facts; it gets null for a NULL or a value that is no text.</param>
internal sealed class SessionStore(string path, string sql, Func<string?, ExecutionModes> parse)
{
    private readonly Dictionary<string, ExecutionModes> _values = new(StringComparer.Ordinal);
    private AntigravityStamp? _stamp;

    public string Path => path;

    public ExecutionModes Of(string sessionId) => _values.GetValueOrDefault(sessionId) ?? ExecutionModes.Empty;

    /// <summary>Reads what is due for <paramref name="sessions"/>; true when a value changed.</summary>
    public bool Refresh(IReadOnlyCollection<string> sessions)
    {
        if (ConfigFile.Stamp(path) is null || AntigravityStamp.Of(path) is not { } stamp)
        {
            var had = _values.Values.Any(v => !v.IsEmpty);
            _values.Clear();
            _stamp = null;
            return had;
        }

        var due = stamp != _stamp ? sessions : [.. sessions.Where(s => !_values.ContainsKey(s))];
        if (due.Count == 0)
        {
            return false;
        }

        var values = new Dictionary<string, ExecutionModes>(StringComparer.Ordinal);
        try
        {
            using var connection = SqliteReadOnly.Open(path, out _);
            foreach (var session in due)
            {
                using var query = connection.Prepare(sql);
                query.Bind(1, session);
                values[session] = parse(query.Step() && query.ColumnType(0) == SqliteNative.ColumnText ? query.GetString(0) : null);
            }
        }
        catch (SqliteException e) when (e.IsBusy)
        {
            return false;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            values = due.ToDictionary(s => s, _ => ExecutionModes.Empty, StringComparer.Ordinal);
        }

        _stamp = stamp;
        var changed = false;
        foreach (var (session, modes) in values)
        {
            changed |= !Of(session).Equals(modes);
            _values[session] = modes;
        }

        return changed;
    }
}
