using System.Text.Json;
using AgentSpeed.Core.Perf;

namespace AgentSpeed.Core.History;

internal sealed partial class HistoryStore
{
    /// <summary>
    /// The performance records of every stored file (METRICS 6.11 <i>Store</i>), created whenever the store is opened; the
    /// schema version does not change for it, so older builds still open the file and ignore it. The retired task score's
    /// records are dropped on every open (analysis version 9).
    /// </summary>
    private const string PerfRecordsSchema = """
        DROP TABLE IF EXISTS task_records;
        CREATE TABLE IF NOT EXISTS perf_records (
            file_id INTEGER NOT NULL, day TEXT NOT NULL, source TEXT NOT NULL, model TEXT NOT NULL,
            n INTEGER NOT NULL, output_sum INTEGER NOT NULL, duration_ms_sum INTEGER NOT NULL,
            latency_bins TEXT NOT NULL, reason_n INTEGER NOT NULL, reason_output INTEGER NOT NULL,
            reason_duration_ms_sum INTEGER NOT NULL, cache_n INTEGER NOT NULL, cache_input INTEGER NOT NULL,
            cache_cached INTEGER NOT NULL, tool_n INTEGER NOT NULL, tool_ln_sum REAL NOT NULL,
            tool_failed INTEGER NOT NULL, active_ms INTEGER NOT NULL, version INTEGER NOT NULL,
            PRIMARY KEY (file_id, day, source, model)) WITHOUT ROWID;
        """;

    /// <summary>Reads the rows whose version is their file's (METRICS 6.11 <i>Reading</i>); a malformed row is left out.</summary>
    private void LoadPerfRecords(Dictionary<long, StoredFile> files)
    {
        var byFile = new Dictionary<long, List<PerfRecord>>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var rows = _connection.Prepare(
            "SELECT p.file_id, p.day, p.source, p.model, p.n, p.output_sum, p.duration_ms_sum, p.latency_bins, p.reason_n, " +
            "p.reason_output, p.reason_duration_ms_sum, p.cache_n, p.cache_input, p.cache_cached, p.tool_n, p.tool_ln_sum, " +
            "p.tool_failed, p.active_ms FROM perf_records p JOIN files f ON f.id = p.file_id WHERE p.version = f.version"))
        {
            while (rows.Step())
            {
                if (!files.ContainsKey(rows.GetInt64(0))
                    || rows.GetString(1) is not { } day
                    || SourceNames.Parse(rows.GetString(2)) is not { } source
                    || ParseBins(rows.GetUtf8(7)) is not { } bins)
                {
                    continue;
                }

                var sums = new PerfSums
                {
                    N = rows.GetInt64(4),
                    OutputSum = rows.GetInt64(5),
                    DurationMsSum = rows.GetInt64(6),
                    ReasonN = rows.GetInt64(8),
                    ReasonOutput = rows.GetInt64(9),
                    ReasonDurationMsSum = rows.GetInt64(10),
                    CacheN = rows.GetInt64(11),
                    CacheInput = rows.GetInt64(12),
                    CacheCached = rows.GetInt64(13),
                    ToolN = rows.GetInt64(14),
                    ToolLnSum = rows.GetDouble(15),
                    ToolFailed = rows.GetInt64(16),
                    ActiveMs = rows.GetInt64(17),
                };
                bins.CopyTo(sums.LatencyBins);
                var model = rows.GetString(3) ?? string.Empty;
                var record = new PerfRecord(Shared(day, names), source, Shared(model, names), sums);
                var id = rows.GetInt64(0);
                if (!byFile.TryGetValue(id, out var list))
                {
                    byFile[id] = list = [];
                }

                list.Add(record);
            }
        }

        foreach (var (id, list) in byFile)
        {
            files[id].PerfRecords = [.. list];
        }
    }

    /// <summary>Replaces a file's rows by its new ones, inside the caller's transaction (METRICS 6.11 <i>Writes</i>).</summary>
    private void SavePerfRecords(StoredFile file)
    {
        using var delete = _connection.Prepare("DELETE FROM perf_records WHERE file_id = ?1");
        delete.Bind(1, file.Id).Run();
        if (file.PerfRecords.Count == 0)
        {
            return;
        }

        using var insert = _connection.Prepare(
            "INSERT INTO perf_records (file_id, day, source, model, n, output_sum, duration_ms_sum, latency_bins, reason_n, " +
            "reason_output, reason_duration_ms_sum, cache_n, cache_input, cache_cached, tool_n, tool_ln_sum, tool_failed, active_ms, " +
            "version) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19)");
        foreach (var record in file.PerfRecords)
        {
            var s = record.Sums;
            insert.Bind(1, file.Id).Bind(2, record.Day).Bind(3, SourceNames.Of(record.Source)).Bind(4, record.Model)
                .Bind(5, s.N).Bind(6, s.OutputSum).Bind(7, s.DurationMsSum).Bind(8, BinsJson(s.Bins)).Bind(9, s.ReasonN)
                .Bind(10, s.ReasonOutput).Bind(11, s.ReasonDurationMsSum).Bind(12, s.CacheN).Bind(13, s.CacheInput)
                .Bind(14, s.CacheCached).Bind(15, s.ToolN).Bind(16, s.ToolLnSum).Bind(17, s.ToolFailed).Bind(18, s.ActiveMs)
                .Bind(19, file.Version).Run();
        }
    }

    /// <summary>The 64 counts as a JSON array of integers.</summary>
    private static string BinsJson(IReadOnlyList<long> bins) =>
        "[" + string.Join(',', bins.Select(b => b.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";

    private static long[]? ParseBins(ReadOnlySpan<byte> json)
    {
        var bins = new long[LatencyBins.Count];
        var reader = new Utf8JsonReader(json);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                return null;
            }

            var count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.Number || count >= bins.Length || !reader.TryGetInt64(out var value) || value < 0)
                {
                    return null;
                }

                bins[count++] = value;
            }

            return count == bins.Length ? bins : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Shared(string value, Dictionary<string, string> names)
    {
        if (names.TryGetValue(value, out var shared))
        {
            return shared;
        }

        names[value] = value;
        return value;
    }
}
