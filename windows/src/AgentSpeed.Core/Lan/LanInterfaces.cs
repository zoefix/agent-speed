using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AgentSpeed.Core.Lan;

/// <summary>The addresses DESIGN 14 shows for the server: local IPv4 addresses that are not loopback, on interfaces that are up.</summary>
public static class LanInterfaces
{
    public const int MaxShown = 3;

    /// <summary>In interface order, at most three; empty when there is none or the system cannot list them.</summary>
    public static IReadOnlyList<IPAddress> Addresses()
    {
        try
        {
            var routed = new List<IPAddress>();
            var other = new List<IPAddress>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var properties = adapter.GetIPProperties();
                var gateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                (gateway ? routed : other).AddRange(Pick(properties.UnicastAddresses.Select(u => u.Address)));
            }

            // Interfaces with a default route first (the network devices are on), then the others (virtual adapters).
            return routed.Concat(other).Distinct().Take(MaxShown).ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>The IPv4 addresses of one interface that are local and not loopback.</summary>
    public static IEnumerable<IPAddress> Pick(IEnumerable<IPAddress> addresses) =>
        addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && LanAddress.IsLocal(a));
}
