using AgentSpeed.Core.Traffic.Engine;
using AgentSpeed.Core.Traffic.Monitor;

namespace AgentSpeed.Core.Traffic.Linux;

internal sealed class MachineCounters : IMachineCounterReader
{
    public IReadOnlyList<string> CountedNames { get; private set; } = [];
    public bool Read(Dictionary<ulong, TrafficCounter> counters)
    {
        counters.Clear();
        var names = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateDirectories("/sys/class/net"))
            {
                try
                {
                    // Count physical interfaces only: avoids loopback, bridges, VPNs and veth duplicates.
                    if (!Directory.Exists(Path.Combine(path, "device")) || File.ReadAllText(Path.Combine(path, "operstate")).Trim() != "up") continue;
                    var id = ulong.Parse(File.ReadAllText(Path.Combine(path, "ifindex")));
                    var rx = long.Parse(File.ReadAllText(Path.Combine(path, "statistics/rx_bytes")));
                    var tx = long.Parse(File.ReadAllText(Path.Combine(path, "statistics/tx_bytes")));
                    counters[id] = new(rx, tx);
                    names.Add(Path.GetFileName(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { }
            }
            CountedNames = names;
            return true;
        }
        catch (IOException) { return false; }
    }
}
