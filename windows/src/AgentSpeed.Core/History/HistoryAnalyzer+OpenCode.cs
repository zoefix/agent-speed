using System.Globalization;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.OpenCode;

namespace AgentSpeed.Core.History;

internal sealed partial class HistoryAnalyzer
{
    /// <summary>Each OpenCode database's identity is stored under its own key, <c>opencode:&lt;database path&gt;</c> (METRICS 2.1).</summary>
    private const string OpenCodeMetaPrefix = "opencode:";

    /// <summary>
    /// OpenCode's databases in list order (METRICS 2.1, 8): changed sessions of changed databases; a session an earlier database
    /// of the list provides is left out. An unchanged database is opened only when a later one changed, to learn its sessions.
    /// </summary>
    private void PlanOpenCode(Plan plan, IReadOnlyList<string> roots, bool zoneChanged, bool repricing, string zoneId)
    {
        var reprices = repricing && _entries.Values.Any(e => e.Family == OpenCodeFamily && e.HasUnpriced);
        var listed = new List<(string Path, string MetaKey, string Identity, bool Unchanged)>();
        foreach (var path in roots.Select(OpenCodeDatabase.DatabasePath).Where(p => p.Length > 0))
        {
            var database = new FileInfo(path);
            if (!database.Exists)
            {
                continue;
            }

            var wal = new FileInfo(path + "-wal");
            var identity = string.Join('|',
                path,
                string.Create(CultureInfo.InvariantCulture, $"{database.Length}:{database.LastWriteTimeUtc.Ticks}"),
                wal.Exists ? string.Create(CultureInfo.InvariantCulture, $"{wal.Length}:{wal.LastWriteTimeUtc.Ticks}") : "-",
                string.Create(CultureInfo.InvariantCulture, $"v{_options.Version}"),
                zoneId);
            var metaKey = OpenCodeMetaPrefix + path;
            var unchanged = !zoneChanged && !reprices && string.Equals(_store.Meta(metaKey), identity, StringComparison.Ordinal);
            listed.Add((path, metaKey, identity, unchanged));
        }

        plan.Discovered += listed.Count;
        var lastChanged = listed.FindLastIndex(d => !d.Unchanged);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index <= lastChanged; index++)
        {
            var (path, metaKey, identity, unchanged) = listed[index];
            OpenCodeDatabase opened;
            try
            {
                opened = OpenCodeDatabase.Open(path);
            }
            catch (OpenCodeOpenException e)
            {
                plan.OpenCodeFailures += e.Status != OpenCodeStatus.Unsupported ? 1 : 0;
                continue;
            }

            try
            {
                var infos = opened.Sessions();
                var earlier = new HashSet<string>(seen, StringComparer.Ordinal);
                seen.UnionWith(infos.Select(info => info.Id));
                if (unchanged)
                {
                    opened.Dispose();
                    continue;
                }

                var activity = opened.SessionActivity();
                var sessions = new List<PlannedSession>();
                foreach (var info in infos.Where(info => !earlier.Contains(info.Id)).OrderBy(info => info.Id, StringComparer.Ordinal))
                {
                    var (rows, lastUpdated) = activity.GetValueOrDefault(info.Id);
                    var modified = Math.Max(info.UpdatedMs, lastUpdated);
                    var previous = _entries.GetValueOrDefault(OpenCodeRules.AgentId(info.Id));
                    if (previous is not null && !zoneChanged && previous.Version == _options.Version
                        && previous.Size == rows && previous.Modified == modified && !(repricing && previous.HasUnpriced))
                    {
                        continue;
                    }

                    sessions.Add(new PlannedSession(info, rows, modified, previous));
                }

                plan.OpenCode.Add(new PlannedOpenCodeDatabase(opened, sessions, metaKey, identity));
            }
            catch (SqliteException)
            {
                opened.Dispose();
                plan.OpenCodeFailures++;
            }
        }
    }
}
