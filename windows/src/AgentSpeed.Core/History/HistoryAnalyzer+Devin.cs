using AgentSpeed.Core.Devin;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.History;

internal sealed partial class HistoryAnalyzer
{
    private sealed record PlannedDevinSession(string Id, List<ParsedResponse> Responses, long Size, long Modified, StoredFile? Previous);

    private void PlanDevin(Plan plan, SourceLocations locations, IReadOnlySet<SourceKind> enabled, bool zoneChanged, bool repricing,
        CancellationToken cancellationToken)
    {
        if (!enabled.Contains(SourceKind.Devin) || locations.DevinRoot.Length == 0) return;
        var location = DevinPaths.Resolve(locations.DevinRoot);
        if (!location.Exists) return;
        try
        {
            var source = new DevinSource(location, new Deduper());
            var update = source.Seed(MonitorCore.BacklogWindowMs);
            if (source.Unsupported) return;
            if (source.Unreadable)
            {
                plan.DevinFailed = true;
                return;
            }
            var removed = update.RemovedResponses.ToHashSet(StringComparer.Ordinal);
            var stamp = AntigravityStamp.Of(location.DatabasePath ?? string.Empty);
            var desktopStamps = DevinPaths.DesktopFiles(location).Select(AntigravityStamp.Of).OfType<AntigravityStamp>().ToArray();
            var size = (stamp?.Length ?? 0) + desktopStamps.Sum(item => item.Length);
            var modified = desktopStamps.Select(item => item.LastWriteUtc.Ticks).Append(stamp?.LastWriteUtc.Ticks ?? 0).Max();
            foreach (var agent in update.Agents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = agent.Info.Id;
                plan.Discovered++;
                var previous = _entries.GetValueOrDefault(id);
                if (previous is not null && !zoneChanged && previous.Version == _options.Version && previous.Family == "devin"
                    && previous.Size == size && previous.Modified == modified && !(repricing && previous.HasUnpriced)) continue;
                plan.DevinSessions.Add(new PlannedDevinSession(id,
                    [.. update.Responses.Select(item => item.Response).Where(response => response.AgentId == id && !removed.Contains(response.Key))],
                    size, modified, previous));
            }
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            plan.DevinFailed = true;
        }
    }

    private bool AnalyzeDevinSessions(List<PlannedDevinSession> sessions, KeyLedger ledger, DayLabeler labeler,
        List<(StoredFile, ulong[])> pending, Action<bool> flush, ref HistoryProgress state, ref int analyzed,
        Action<HistoryProgress>? progress, CancellationToken cancellationToken)
    {
        foreach (var session in sessions)
        {
            if (cancellationToken.IsCancellationRequested) return false;
            var responses = new Dictionary<string, ParsedResponse>(StringComparer.Ordinal);
            var completions = new Perf.PerfCompletionLog();
            foreach (var response in session.Responses)
            {
                if (ledger.TryClaim(response.Key, session.Id, out var owner) || string.Equals(owner, session.Id, StringComparison.Ordinal))
                {
                    responses[response.Key] = response;
                    completions.Upsert(response);
                }
            }
            var entry = new StoredFile
            {
                Id = session.Previous?.Id ?? 0, Path = session.Id, Family = "devin", Size = session.Size, Modified = session.Modified,
                Version = _options.Version, Days = DayAggregate.Contributions(responses.Values, SourceKind.Devin, labeler, Pricing),
                PerfRecords = PerfRecordsOf(completions, responses, session.Id, SourceKind.Devin, labeler),
            };
            _entries[session.Id] = entry;
            pending.Add((entry, ledger.FinishFile()));
            analyzed++;
            state = state with { FilesDone = state.FilesDone + 1 };
            flush(false);
            progress?.Invoke(state);
        }
        return true;
    }
}
