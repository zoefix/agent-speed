using AgentSpeed.Core.Engine;
using AgentSpeed.Core.OpenClaw;

namespace AgentSpeed.Core.Monitoring;

internal sealed partial class MonitorCore
{
    private readonly Dictionary<string, int> _openClawPushed = new(StringComparer.Ordinal);
    private readonly List<SourceRoot> _openClawWatchRoots = [];
    private OpenClawSource? _openClaw;
    private IReadOnlyList<string> _openClawRootPaths = [];
    private bool _openClawAutomatic;
    private bool _codexAutomatic;
    private bool _openClawSeeded;

    /// <summary>OpenClaw's automatic state folders as rediscovery reads them again (METRICS 4.14); tests replace it.</summary>
    internal Func<IReadOnlyList<string>> AutomaticOpenClawRoots { get; set; } = static () => LiveMonitor.DefaultOpenClawRoots;

    internal OpenClawSource? OpenClaw => _openClaw;

    private void InitializeOpenClaw(SourceLocations locations)
    {
        _openClawRootPaths = locations.OpenClawRoots ?? [];
        _openClawAutomatic = locations.OpenClawAutomatic;
        _codexAutomatic = locations.CodexAutomatic;
        if (_enabled.Contains(SourceKind.OpenClaw) && _openClawRootPaths.Count > 0)
        {
            _openClaw = new OpenClawSource(_openClawRootPaths, _openClawAutomatic ? () => CurrentOpenClawRoots() : null, _deduper);
            RefreshOpenClawWatchRoots();
        }
    }

    private IReadOnlyList<string> CurrentOpenClawRoots() =>
        _openClawAutomatic ? SourceLocations.NormalizeAll(AutomaticOpenClawRoots()) : _openClawRootPaths;

    /// <summary>One watched folder per state folder's <c>agents</c> folder (or the chosen agent folder or database folder).</summary>
    private void RefreshOpenClawWatchRoots()
    {
        if (_openClaw is null)
        {
            return;
        }

        var wanted = _openClaw.WatchDirectories();
        _openClawWatchRoots.RemoveAll(root => !wanted.Contains(root.Path, PathComparer));
        foreach (var path in wanted)
        {
            if (!_openClawWatchRoots.Exists(root => PathComparer.Equals(root.Path, path)))
            {
                _openClawWatchRoots.Add(new SourceRoot(RootFamily.OpenClaw, path, enabled: true));
            }
        }

        foreach (var root in _openClawWatchRoots)
        {
            root.Available = SafeDirectoryExists(root.Path);
        }
    }

    private void SeedOpenClaw(long todayStartMs, CancellationToken cancellationToken, ref int discovered, ref int filesRead, ref long bytesRead)
    {
        if (_openClaw is not { } openClaw || _openClawSeeded)
        {
            return;
        }

        var update = openClaw.Seed(todayStartMs, cancellationToken);

        discovered += openClaw.DatabasePaths.Count;
        filesRead += update.DatabasesRead;
        bytesRead += update.BytesRead;
        ApplyOpenClaw(update, seeded: true);
        _openClawSeeded = true;
    }

    private bool RefreshOpenClaw(bool discover)
    {
        if (_openClaw is not { } openClaw || !_openClawSeeded)
        {
            return false;
        }

        var changed = ApplyOpenClaw(openClaw.Refresh(discover), seeded: false);
        if (discover)
        {
            RefreshOpenClawWatchRoots();
        }

        return changed;
    }

    /// <summary>
    /// Agent-scoped Codex homes of agent databases that appeared while running join the Codex roots (METRICS 2, 4.14),
    /// unless a Codex folder is chosen.
    /// </summary>
    private void AddNewCodexHomeRoots()
    {
        if (!_codexAutomatic)
        {
            return;
        }

        var known = new HashSet<string>(_roots.Where(r => r.Family == RootFamily.Codex).Select(r => Path.TrimEndingDirectorySeparator(r.Path)), PathComparer);
        var databases = OpenClawPaths.Databases(CurrentOpenClawRoots());
        foreach (var root in SourceLocations.NormalizeAll(OpenClawPaths.CodexRoots(databases)))
        {
            if (known.Add(Path.TrimEndingDirectorySeparator(root)))
            {
                _roots.Add(new SourceRoot(RootFamily.Codex, root, Families.IsEnabled(RootFamily.Codex, _enabled)));
            }
        }
    }

    /// <summary>Applies one read of the OpenClaw databases; true when anything changed.</summary>
    private bool ApplyOpenClaw(OpenClawUpdate update, bool seeded)
    {
        foreach (var id in update.Removed)
        {
            _openClawPushed.Remove(id);
            _engine.RemoveAgent(id);
            _linksChanged |= _links.Remove(id);
        }

        foreach (var parser in _openClaw?.Parsers ?? [])
        {
            PushOpenClawAgent(parser);
        }

        long? liveNowMs = seeded ? null : Clock();
        foreach (var (response, isCompletion, agentId, call) in update.Completions)
        {
            if (response is null)
            {
                if (liveNowMs is { } now)
                {
                    _engine.CountCall(agentId!, SourceKind.OpenClaw, call, now);
                }

                continue;
            }

            _responsesSeen++;
            _engine.Upsert(response, isCompletion, seeded, liveNowMs);
            _observer?.OnResponse(response, isCompletion, seeded);
        }

        return update.Changed || update.Responses.Count > 0 || update.Removed.Count > 0;
    }

    private void PushOpenClawAgent(OpenClawParser parser)
    {
        if (_openClawPushed.TryGetValue(parser.AgentId, out var pushed) && pushed == parser.Version)
        {
            return;
        }

        _openClawPushed[parser.AgentId] = parser.Version;
        if (parser.LastLineMs is null)
        {
            return;
        }

        var link = AgentLink.Of(parser);
        if (!_links.TryGetValue(parser.AgentId, out var known) || known != link)
        {
            _links[parser.AgentId] = link;
            _linksChanged = true;
        }

        _engine.UpdateAgent(new AgentInfo(
            parser.AgentId,
            SourceKind.OpenClaw,
            parser.Project,
            parser.Model,
            parser.IsSubagent,
            parser.Phase,
            parser.InFlightStartMs,
            parser.LastLineMs,
            _parents.GetValueOrDefault(parser.AgentId)) { Modes = parser.Modes, Work = parser.WorkActions });
    }

    private void RepushOpenClawAgents()
    {
        _openClawPushed.Clear();
        foreach (var parser in _openClaw?.Parsers ?? [])
        {
            PushOpenClawAgent(parser);
        }
    }

    /// <summary>The first state folder (or the setting) and whether any of them exists (METRICS 2, detection).</summary>
    private (string Path, bool Available) OpenClawStatus() =>
        (_openClawRootPaths.Count > 0 ? _openClawRootPaths[0] : string.Empty, _openClawRootPaths.Any(OpenClawPaths.Exists));
}
