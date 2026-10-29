using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.OpenClaw;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.History;

/// <summary>
/// OpenClaw agent databases (METRICS 4.14, 8): a database whose size and time (with its <c>-wal</c>) changed is read
/// completely in startup order; each transcript window is stored as its own entry, so a day's agents count windows, and
/// the database itself as an entry without contributions that remembers its identity. Windows that are gone keep their days.
/// </summary>
internal sealed partial class HistoryAnalyzer
{
    private const string OpenClawFamily = "openclaw";
    private const string OpenClawDatabaseFamily = "openclawDatabase";

    private sealed record PlannedOpenClawDatabase(string Path, AntigravityStamp Stamp, StoredFile? Previous, List<StoredFile> Windows);

    private void PlanOpenClaw(Plan plan, SourceLocations locations, IReadOnlySet<SourceKind> enabled, bool zoneChanged, bool repricing)
    {
        if (!enabled.Contains(SourceKind.OpenClaw) || locations.OpenClawRoots is not { Count: > 0 } roots)
        {
            return;
        }

        foreach (var path in OpenClawPaths.Databases(roots))
        {
            if (AntigravityStamp.Of(path) is not { } stamp)
            {
                continue;
            }

            plan.Discovered++;
            var prefix = path + "#";
            var windows = _entries.Values
                .Where(e => e.Family == OpenClawFamily && e.Path.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            var previous = _entries.GetValueOrDefault(path);
            if (previous is not null && !zoneChanged && previous.Version == _options.Version && previous.Family == OpenClawDatabaseFamily
                && previous.Size == stamp.Length && previous.Modified == stamp.LastWriteUtc.Ticks
                && !(repricing && windows.Exists(w => w.HasUnpriced)))
            {
                continue;
            }

            plan.OpenClawDatabases.Add(new PlannedOpenClawDatabase(path, stamp, previous, windows));
        }

        plan.OpenClawDatabases.Sort(static (a, b) =>
        {
            var byTime = a.Stamp.LastWriteUtc.CompareTo(b.Stamp.LastWriteUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Path, b.Path);
        });
    }

    private IEnumerable<long> OpenClawReplacedIds(Plan plan) =>
        plan.OpenClawDatabases.SelectMany(d => d.Windows.Select(w => w.Id).Append(d.Previous?.Id ?? 0));

    private bool AnalyzeOpenClaw(
        List<PlannedOpenClawDatabase> databases,
        KeyLedger ledger,
        DayLabeler labeler,
        List<(StoredFile, ulong[])> pending,
        Action<bool> flush,
        ref HistoryProgress state,
        ref int analyzed,
        ref long bytes,
        ref int unreadable,
        Action<HistoryProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var database in databases)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            state = state with { FilesDone = state.FilesDone + 1 };
            var written = new HashSet<string>(StringComparer.Ordinal);
            var sinks = new Dictionary<OpenClawParser, LatestResponses>();
            List<OpenClawParser>? parsers;
            try
            {
                parsers = OpenClawSource.ReadAll(
                    database.Path,
                    ledger,
                    parser => sinks[parser] = new LatestResponses(merge: false, parser.Completions),
                    parser =>
                    {
                        var previous = database.Windows.Find(w => w.Path == parser.AgentId);
                        var entry = new StoredFile
                        {
                            Id = previous?.Id ?? 0,
                            Path = parser.AgentId,
                            Family = OpenClawFamily,
                            Size = database.Stamp.Length,
                            Modified = database.Stamp.LastWriteUtc.Ticks,
                            Version = _options.Version,
                            Days = DayAggregate.Contributions(sinks[parser].Items.Values, SourceKind.OpenClaw, labeler, Pricing),
                            PerfRecords = PerfRecordsOf(parser.Completions, sinks[parser].Items, parser.AgentId, SourceKind.OpenClaw, labeler),
                        };
                        _entries[parser.AgentId] = entry;
                        pending.Add((entry, ledger.FinishFile()));
                        written.Add(parser.AgentId);
                    },
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ledger.AbandonFile();
                return false;
            }
            catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
            {
                // Unreadable: windows not written keep what was stored, with their keys; the database is read next time.
                ledger.AbandonFile();
                unreadable++;
                RestoreKeys(ledger, database, written);
                progress?.Invoke(state);
                continue;
            }

            RestoreKeys(ledger, database, written);
            if (parsers is not null)
            {
                analyzed++;
                bytes += database.Stamp.Length;
            }

            var marker = new StoredFile
            {
                Id = database.Previous?.Id ?? 0,
                Path = database.Path,
                Family = OpenClawDatabaseFamily,
                Size = database.Stamp.Length,
                Modified = database.Stamp.LastWriteUtc.Ticks,
                Version = _options.Version,
                Days = [],
            };
            _entries[database.Path] = marker;
            pending.Add((marker, []));
            flush(false);
            progress?.Invoke(state);
        }

        return true;
    }

    // Windows that are gone (pruned, archived) keep their days and the keys they own.
    private void RestoreKeys(KeyLedger ledger, PlannedOpenClawDatabase database, HashSet<string> written)
    {
        foreach (var window in database.Windows)
        {
            if (window.Id > 0 && !written.Contains(window.Path))
            {
                _store.LoadKeys(ledger, window.Id);
            }
        }
    }
}
