using System.Runtime.Versioning;
using System.Security.Cryptography;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Traffic.Engine;
using AgentSpeed.Core.Traffic.Windows;

namespace AgentSpeed.Core.Quota.Local;

/// <summary>
/// The Windows side of QUOTAS 9.4: this session's <c>language_server_*</c> and plain <c>language_server</c> processes
/// (their paths read once per process, their arguments only for a candidate), their listening TCP ports on loopback, the
/// daemon files while their pid runs, and whether a user's <c>agy</c> runs. Windows has no route to another process's
/// environment, so <c>agy</c> is found as a server through its daemon file alone. Agent Speed's own process and its
/// descendants (the usage report's <c>agy</c>) are never servers and never a running <c>agy</c> (METRICS 10.5).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AntigravityProcessLocator : IAntigravityLocator, IDisposable
{
    private const int MaxServers = 4;
    private const int MaxDepth = 64;

    private readonly string _home;
    private readonly ProcessIdentity? _app;
    private readonly ProcessReader _reader = new();
    private readonly List<ProcessEntry> _entries = new(512);
    private readonly Dictionary<int, ProcessEntry> _byPid = new(512);
    private readonly Dictionary<ProcessIdentity, string?> _paths = [];
    private readonly Lock _lock = new();
    private bool _agyRuns;

    public AntigravityProcessLocator(string home)
        : this(home, ProcessReader.Current())
    {
    }

    /// <param name="app">The process whose descendants are left out; null leaves none out.</param>
    internal AntigravityProcessLocator(string home, ProcessIdentity? app)
    {
        _home = home;
        _app = app;
    }

    public bool AgyRuns
    {
        get
        {
            lock (_lock)
            {
                return _agyRuns;
            }
        }
    }

    public IReadOnlyList<AntigravityServer> Servers()
    {
        lock (_lock)
        {
            _agyRuns = false;
            if (!_reader.List(_entries))
            {
                return [];
            }

            _byPid.Clear();
            foreach (var entry in _entries)
            {
                _byPid[entry.Pid] = entry;
            }

            var alive = new HashSet<ProcessIdentity>(_entries.Select(e => e.Identity));
            foreach (var gone in _paths.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                _paths.Remove(gone);
            }

            var found = new List<AntigravityServer>();
            var pids = new HashSet<int>();
            foreach (var entry in _entries)
            {
                if (IsOwn(entry))
                {
                    continue;
                }

                pids.Add(entry.Pid);
                if (!_paths.TryGetValue(entry.Identity, out var path))
                {
                    path = _reader.ExecutablePath(entry);
                    _paths[entry.Identity] = path;
                }

                if (path is null)
                {
                    continue;
                }

                _agyRuns |= Path.GetFileName(path).Equals("agy.exe", StringComparison.OrdinalIgnoreCase);
                if (found.Count >= MaxServers || !AntigravityRules.IsServerName(path))
                {
                    continue;
                }

                var arguments = _reader.Arguments(entry) ?? [];
                if (AntigravityRules.IsLanguageServer(path, arguments))
                {
                    found.Add(AntigravityRules.LanguageServer(entry.Pid, arguments, TcpListeners.Of(entry.Pid)));
                }
            }

            foreach (var file in AntigravityRules.DaemonFiles(_home))
            {
                var bytes = QuotaCredentials.ReadFile(file);
                if (bytes is null)
                {
                    continue;
                }

                try
                {
                    if (AntigravityRules.Daemon(bytes) is { } server && pids.Contains(server.Pid) && found.All(s => s.Pid != server.Pid))
                    {
                        found.Add(server);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            return found;
        }
    }

    /// <summary>The app itself or one of its descendants, by the table's ancestry rule (a parent never starts later).</summary>
    private bool IsOwn(ProcessEntry entry)
    {
        if (_app is not { } app)
        {
            return false;
        }

        var cursor = entry;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            if (cursor.Identity == app)
            {
                return true;
            }

            if (!_byPid.TryGetValue(cursor.ParentPid, out var parent) || parent.Pid == cursor.Pid || parent.StartUs > cursor.StartUs)
            {
                return false;
            }

            cursor = parent;
        }

        return false;
    }

    public void Dispose() => _reader.Dispose();
}
