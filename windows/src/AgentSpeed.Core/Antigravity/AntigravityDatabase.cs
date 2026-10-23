using AgentSpeed.Core.Engine;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Antigravity;

internal sealed record AntigravityRead(List<ParsedResponse> Responses, AgentInfo Agent, int Malformed);

internal static class AntigravityDatabase
{
    private const int MaxBlobBytes = 64 * 1024 * 1024;

    public static AntigravityRead Read(string path, SourceKind source)
    {
        var id = Path.GetFullPath(path);
        using var db = SqliteConnection.Open(path, SqliteOpenMode.ReadOnly, busyTimeoutMs: 100);
        // One consistent WAL-aware snapshot; no migration, journal-mode change or writes.
        db.Execute("BEGIN");
        var ends = new Dictionary<long, long>();
        var malformed = 0;
        long? lastActivity = null, inFlight = null;
        var phase = AgentPhase.Idle;
        using (var steps = db.Prepare("SELECT idx, step_type, status, metadata FROM steps ORDER BY idx"))
        {
            while (steps.Step())
            {
                try
                {
                    var blob = steps.GetBlob(3);
                    if (blob.Length > MaxBlobBytes) throw new FormatException("Metadata too large.");
                    var meta = new Proto(blob);
                    var created = new Proto(meta.Bytes(1)).Milliseconds();
                    var finished = new Proto(meta.Bytes(7)).Milliseconds();
                    var completed = new Proto(meta.Bytes(8)).Milliseconds();
                    if (finished is > 0) ends[steps.GetInt64(0)] = finished.Value;
                    lastActivity = completed ?? finished ?? created ?? lastActivity;
                    var type = steps.GetInt64(1);
                    var status = steps.GetInt64(2);
                    var active = status is 1 or 2 or 8 or 9 or 11;
                    phase = active ? (type is 14 or 15 ? AgentPhase.Generating : AgentPhase.Tools) : AgentPhase.Idle;
                    inFlight = phase == AgentPhase.Generating ? created : null;
                }
                catch (FormatException) { malformed++; }
            }
        }

        var responses = new List<ParsedResponse>();
        using var generations = db.Prepare("SELECT idx, data FROM gen_metadata ORDER BY idx");
        while (generations.Step())
        {
            try
            {
                var blob = generations.GetBlob(1);
                if (blob.Length > MaxBlobBytes) throw new FormatException("Metadata too large.");
                var generator = new Proto(blob);
                var chat = new Proto(generator.Bytes(1));
                var usage = new Proto(chat.Bytes(4));
                // A record without usage is an in-progress/retry record, never a zero-token request.
                if (usage.Data.IsEmpty) continue;
                var end = generator.Numbers(2).Where(ends.ContainsKey).Select(i => ends[i]).DefaultIfEmpty().Max();
                if (end <= 0) continue;
                long? ttft = chat.Has(11) ? new Proto(chat.Bytes(11)).Milliseconds() ?? 0 : null;
                long? streaming = chat.Has(12) ? new Proto(chat.Bytes(12)).Milliseconds() ?? 0 : null;
                long? start = ttft is { } first && streaming is { } rest && first + rest > 0 && first + rest <= end
                    ? end - first - rest : null;
                var cached = TokenCounts.Clamp(usage.Number(5));
                var cacheWrite = TokenCounts.Clamp(usage.Number(4));
                var counts = new TokenCounts(
                    TokenCounts.Clamp(TokenCounts.Clamp(usage.Number(2)) + cached + cacheWrite), cached,
                    cacheWrite, TokenCounts.Clamp(usage.Number(3)), TokenCounts.Clamp(usage.Number(9)));
                var model = chat.Text(19) ?? chat.Text(21);
                var key = $"{source}:{Path.GetFileNameWithoutExtension(path)}:{generations.GetInt64(0)}";
                responses.Add(new ParsedResponse(key, source, id, model, start, end, counts));
            }
            catch (FormatException) { malformed++; }
        }
        return new AntigravityRead(responses,
            new AgentInfo(id, source, null, responses.LastOrDefault()?.Model, false, phase, inFlight, lastActivity), malformed);
    }
}
