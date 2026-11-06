using System.Security.Cryptography;
using System.Text;
using AgentSpeed.Core;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Probe;

/// <summary>
/// <c>--watch N --remote DESTINATION</c>: the remote hosts of a watch (REMOTE). The coordinator connects them over the system
/// <c>ssh</c>; with <c>--history</c> a history service syncs their days into a scratch (or the given) store.
/// </summary>
internal sealed class RemoteWatch : IDisposable
{
    private readonly RemoteCoordinator _coordinator;
    private readonly HistoryService? _history;
    private readonly HistoryHub? _historyHub;
    private readonly string? _scratch;

    public RemoteWatch(LiveMonitor monitor, ProbeOptions options)
    {
        RemoteLog.SetSink(message => Console.Error.WriteLine("remote: " + message));
        _coordinator = new RemoteCoordinator(monitor, "0.0.0", relays: typeof(RemoteWatch).Assembly);
        var document = new RemoteHostsDocument(options.Remotes.Select(d => new RemoteHost(d, StableId(d))));
        if (options.RemoteHistory)
        {
            var store = options.StorePath;
            if (store is null)
            {
                _scratch = Path.Combine(Path.GetTempPath(), "agent-speed-probe-" + Environment.ProcessId);
                Directory.CreateDirectory(_scratch);
                store = Path.Combine(_scratch, "history.sqlite");
            }

            _historyHub = new HistoryHub(context: null);
            var sync = new RemoteHistorySync();
            var names = document.DisplayNames();
            sync.SetHosts([.. document.Hosts.Select(h => (h.Id, names[h.Id]))]);
            _coordinator.AttachHistory(sync);
            _history = new HistoryService(_historyHub, options.Configuration, store, remote: sync);
            _history.Start();
        }

        _coordinator.Update(document, networkEnabled: true);
    }

    /// <summary>The same host id for a destination in every run, so a kept history store recognises its host.</summary>
    public static string StableId(string destination)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(destination)).AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString("D").ToUpperInvariant();
    }

    /// <summary>The synced days of every remote host: their count and today's output on each host.</summary>
    public string? HistorySummary()
    {
        if (_historyHub is null)
        {
            return null;
        }

        var remote = _historyHub.Days.Where(d => !d.IsLocal).ToList();
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return new JsonLine()
            .Add("state", _historyHub.State.Kind.ToString())
            .Add("hostDays", remote.Count)
            .Add("hostOutput", remote.Sum(d => d.Totals.Output))
            .Add("todayHostOutput", remote.Where(d => d.Day == today).Sum(d => d.Totals.Output))
            .Text;
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        _history?.Dispose();
        RemoteLog.SetSink(null);
        if (_scratch is not null)
        {
            try
            {
                Directory.Delete(_scratch, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>One host: its status, clock offset, live value, today's output and main sessions.</summary>
    public static string Host(HostStatus host) => new JsonLine()
        .Add("name", host.Name)
        .Add("status", host.Remote?.Status.Code ?? (host.IsLocal ? "local" : "unknown"))
        .Add("clockOffsetMs", host.ClockOffsetMs)
        .Add("live", host.Live.Output)
        .Add("todayOutput", host.Today.Output)
        .Add("todayRequests", host.Today.Requests)
        .Add("sessions", host.Sessions)
        .Text;
}
