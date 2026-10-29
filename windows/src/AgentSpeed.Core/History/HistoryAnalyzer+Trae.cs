using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Trae;

namespace AgentSpeed.Core.History;

internal sealed partial class HistoryAnalyzer
{
    private readonly Dictionary<string, TraeTiming> _traeTimings = new(StringComparer.Ordinal);

    private void PlanTrae(Plan plan, SourceLocations locations, IReadOnlySet<SourceKind> enabled, bool zoneChanged, bool repricing,
        CancellationToken cancellationToken)
    {
        if (!enabled.Contains(SourceKind.Trae)) return;
        foreach (var root in locations.TraeStores ?? [])
        {
            var path = TraePaths.DatabasePath(root);
            if (TraeTiming.Stamp(path) is not { } stamp) continue;
            try
            {
                using var database = TraeDatabase.Open(path);
                if (!_traeTimings.TryGetValue(path, out var timing)) _traeTimings[path] = timing = new TraeTiming();
                foreach (var group in database.Read(cancellationToken: cancellationToken, timing: timing).GroupBy(row => row.SessionId))
                {
                    var id = path + "#" + group.Key;
                    plan.Discovered++;
                    var previous = _entries.GetValueOrDefault(id);
                    if (previous is not null && !zoneChanged && previous.Version == _options.Version && previous.Family == "trae"
                        && previous.Size == stamp.Size && previous.Modified == stamp.Fingerprint && !(repricing && previous.HasUnpriced)) continue;
                    plan.ZCodeSessions.Add(new PlannedZCodeSession(id, [.. group.Select(row => row.Response(path))],
                        stamp.Size, stamp.Fingerprint, previous, SourceKind.Trae));
                }
            }
            catch (Exception error) when (error is InvalidDataException or EntryPointNotFoundException) { }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException) { plan.ZCodeFailed = true; }
        }
    }
}
