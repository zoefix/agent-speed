using AgentSpeed.Core.Quota.Estimate;

namespace AgentSpeed.Core.History;

internal sealed partial class HistoryStore
{
    /// <summary>
    /// The quota estimate's share of every stored file (QUOTAS 10.3): its spend within 8 days per minute and its Codex token
    /// count lines, written with the file's contributions. Created whenever the store is opened, like <c>perf_records</c>;
    /// older builds ignore it. Numbers, model ids and plan names only.
    /// </summary>
    private const string EstimateSchema = """
        CREATE TABLE IF NOT EXISTS estimate_files (file_id INTEGER PRIMARY KEY, version INTEGER NOT NULL, data BLOB NOT NULL);
        """;

    /// <summary>Every stored file's share with the version it was made with, by path; a row that does not parse counts as missing.</summary>
    public Dictionary<string, (int Version, EstimateFileShare Share)> EstimateShares(StringComparer pathComparer)
    {
        var shares = new Dictionary<string, (int, EstimateFileShare)>(pathComparer);
        using var rows = _connection.Prepare("SELECT f.path, e.version, e.data FROM estimate_files e JOIN files f ON f.id = e.file_id");
        while (rows.Step())
        {
            if (rows.GetString(0) is { } path && EstimateFileShare.Decode(rows.GetBlob(2), path) is { } share)
            {
                shares[path] = ((int)rows.GetInt64(1), share);
            }
        }

        return shares;
    }

    /// <summary>Replaces a file's share inside the caller's transaction; a file without one keeps none.</summary>
    private void SaveEstimate(StoredFile file)
    {
        using var delete = _connection.Prepare("DELETE FROM estimate_files WHERE file_id = ?1");
        delete.Bind(1, file.Id).Run();
        if (file.Estimate is not { } share)
        {
            return;
        }

        using var insert = _connection.Prepare("INSERT INTO estimate_files (file_id, version, data) VALUES (?1, ?2, ?3)");
        insert.Bind(1, file.Id).Bind(2, EstimateFileShare.Version).BindBlob(3, share.IsEmpty ? Array.Empty<byte>() : share.Encode()).Run();
    }
}
