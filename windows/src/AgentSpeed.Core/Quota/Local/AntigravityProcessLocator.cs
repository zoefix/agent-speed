using System.Runtime.Versioning;
using System.Security.Cryptography;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Traffic.Engine;
using AgentSpeed.Core.Traffic.Windows;

namespace AgentSpeed.Core.Quota.Local;

/// <summary>
/// The Windows side of QUOTAS 9.4: this session's <c>language_server_*</c> processes (their paths read once per process,
/// their arguments only for a candidate), their listening TCP ports on loopback, and the daemon files while their pid
/// runs. Windows has no route to another process's environment, so <c>agy</c> is found through its daemon file alone.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AntigravityProcessLocator : IAntigravityLocator, IDisposable
{
    private const int MaxServers = 4;

    private readonly string _home;
    private readonly ProcessReader _reader = new();
    private readonly List<ProcessEntry> _entries = new(512);
    private readonly Dictionary<ProcessIdentity, string?> _paths = [];
    private readonly Lock _lock = new();

    public AntigravityProcessLocator(string home) => _home = home;

    public IReadOnlyList<AntigravityServer> Servers()
    {
        lock (_lock)
        {
            if (!_reader.List(_entries))
            {
                return [];
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
                pids.Add(entry.Pid);
                if (found.Count >= MaxServers)
                {
                    continue;
                }

                if (!_paths.TryGetValue(entry.Identity, out var path))
                {
                    path = _reader.ExecutablePath(entry);
                    _paths[entry.Identity] = path;
                }

                if (path is null || !Path.GetFileName(path).StartsWith("language_server_", StringComparison.OrdinalIgnoreCase))
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

    public void Dispose() => _reader.Dispose();
}
