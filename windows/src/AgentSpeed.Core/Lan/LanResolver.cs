using System.Net;
using System.Net.Sockets;

namespace AgentSpeed.Core.Lan;

/// <summary>Resolves a target's host name with the system resolver (LAN 5.1); an empty list when it does not resolve.</summary>
public interface ILanResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken token);
}

public sealed class SystemLanResolver : ILanResolver
{
    public static SystemLanResolver Instance { get; } = new();

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken token)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }
}

/// <summary>The checked address a target sends to (LAN 5.1): the host's own literal, or a resolution whose every address is local.</summary>
public static class LanTargetResolution
{
    public static async Task<(LanTargetStatusKind Status, IPAddress? Address)> ResolveAsync(ILanResolver resolver, LanTargetAddress address, bool udp,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(address);
        IReadOnlyList<IPAddress> addresses;
        if (address.Literal)
        {
            addresses = LiteralOf(address.Host) is { } literal ? [literal] : [];
        }
        else
        {
            addresses = await resolver.ResolveAsync(address.Host, token).ConfigureAwait(false);
        }

        var status = LanAddress.ResolveCheck(addresses, udp);
        if (status != LanTargetStatusKind.Ok)
        {
            return (status, null);
        }

        var chosen = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        return (status, chosen.IsIPv4MappedToIPv6 ? chosen.MapToIPv4() : chosen);
    }

    /// <summary>An IP literal as written (an IPv6 one may carry a numeric scope id after <c>%</c>).</summary>
    private static IPAddress? LiteralOf(string host)
    {
        var parts = host.Split('%', 2);
        if (!LanAddress.TryParseStrict(parts[0], out var address))
        {
            return null;
        }

        if (parts.Length == 2 && address.AddressFamily == AddressFamily.InterNetworkV6 && IPAddress.TryParse(host, out var scoped))
        {
            return scoped;
        }

        return address;
    }
}
