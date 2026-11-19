using System.Globalization;
using System.Text.Json;
using AgentSpeed.Core.Harnesses;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.Parsing;
using static AgentSpeed.Core.Harnesses.HarnessJson;

namespace AgentSpeed.Core.OpenBitFun;

internal sealed record OpenBitFunUsage(
    string Session, string Turn, string Round, string Attempt, string? Model, string? Provider, TokenCounts? Tokens, long RecordedMs);

internal sealed record OpenBitFunUsageSnapshot(IReadOnlyList<OpenBitFunUsage> Records, long Signature, DateTime LastWriteUtc)
{
    public static OpenBitFunUsageSnapshot Empty { get; } = new([], 0, default);
}

// The daily ledger is shared by every session. Decode each changed day once, keeping only measurement fields.
internal sealed class OpenBitFunUsageLedger(string root)
{
    public static OpenBitFunUsageLedger Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "openbitfun", "data", "token_usage", "records"));

    private sealed record Day(long Length, DateTime LastWriteUtc, ILookup<string, OpenBitFunUsage> Sessions);

    private readonly object _gate = new();
    private readonly SortedDictionary<string, Day> _days = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OpenBitFunUsageSnapshot> _sessions = new(StringComparer.Ordinal);
    private long _lastScan;

    public OpenBitFunUsageSnapshot ForSession(string session)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (_lastScan != 0 && now - _lastScan < 1000)
                return _sessions.GetValueOrDefault(session, OpenBitFunUsageSnapshot.Empty);
            _lastScan = now;
            var directory = new DirectoryInfo(root);
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                _days.Clear();
                _sessions.Clear();
                return OpenBitFunUsageSnapshot.Empty;
            }

            var affected = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in directory.EnumerateFiles("????-??-??.json", new EnumerationOptions
                { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) continue;
                seen.Add(file.FullName);
                if (_days.TryGetValue(file.FullName, out var previous)
                    && previous.Length == file.Length && previous.LastWriteUtc == file.LastWriteTimeUtc) continue;

                try
                {
                    using var document = HarnessJson.ReadMetadata(file.FullName, DocumentTailer.MaxBytes);
                    if (document is null || Get(document.RootElement, "records") is not { ValueKind: JsonValueKind.Array } records) continue;
                    List<OpenBitFunUsage> entries = [];
                    foreach (var row in records.EnumerateArray())
                    {
                        var details = Get(row, "token_details");
                        var context = Get(details, "openbitfunModelRequest");
                        if (Milliseconds(Get(context, "version")) != 1
                            || String(row, "session_id") is not { } sessionId
                            || String(row, "turn_id") is not { } turn
                            || String(context, "roundId") is not { } round
                            || String(context, "attemptId") is not { } attempt) continue;
                        var output = Count(row, "output_tokens");
                        TokenCounts? tokens = Get(context, "usageKnown").ValueKind == JsonValueKind.True
                            ? new(Count(row, "input_tokens"), Count(row, "cached_tokens"), Count(row, "cache_write_tokens"),
                                output, Math.Min(output, Count(details, "reasoningTokenCount"))) : null;
                        entries.Add(new(sessionId, turn, round, attempt,
                            String(row, "effective_model_name") ?? String(context, "responseModel") ?? String(context, "requestedModel"),
                            String(Get(context, "channel"), "protocol"), tokens, Timestamp(Get(row, "timestamp")) ?? 0));
                    }
                    var sessions = entries.ToLookup(row => row.Session, StringComparer.Ordinal);
                    foreach (var group in sessions)
                        if (previous is null || !group.SequenceEqual(previous.Sessions[group.Key])) affected.Add(group.Key);
                    if (previous is not null)
                        foreach (var group in previous.Sessions)
                            if (!sessions.Contains(group.Key)) affected.Add(group.Key);
                    _days[file.FullName] = new(file.Length, file.LastWriteTimeUtc, sessions);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    // A file being replaced keeps its last complete snapshot and is retried at the next scan.
                }
            }
            foreach (var removed in _days.Keys.Where(path => !seen.Contains(path)).ToArray())
            {
                affected.UnionWith(_days[removed].Sessions.Select(group => group.Key));
                _days.Remove(removed);
            }
            foreach (var owner in affected)
            {
                var rows = _days.Values.SelectMany(day => day.Sessions[owner]).ToArray();
                if (rows.Length == 0) { _sessions.Remove(owner); continue; }
                ulong hash = 0;
                foreach (var row in rows)
                {
                    foreach (var value in new[] { row.Turn, row.Round, row.Attempt, row.Model ?? "", row.Provider ?? "" })
                        hash = unchecked((hash ^ KeyHash.Fnv1a(value)) * 1_099_511_628_211UL);
                    if (row.Tokens is { } tokens)
                        foreach (var count in new[] { tokens.Input, tokens.CachedInput, tokens.CacheWriteInput, tokens.Output, tokens.Reasoning })
                            hash = unchecked((hash ^ (ulong)count) * 1_099_511_628_211UL);
                }
                var newest = rows.Max(row => row.RecordedMs);
                _sessions[owner] = new(rows, unchecked((long)hash), DateTimeOffset.FromUnixTimeMilliseconds(newest).UtcDateTime);
            }
            return _sessions.GetValueOrDefault(session, OpenBitFunUsageSnapshot.Empty);
        }
    }
}
