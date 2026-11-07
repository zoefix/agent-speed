using System.Net;
using System.Net.Sockets;

namespace AgentSpeed.Core.Lan;

/// <summary>Local addresses (LAN 2.3) and the resolution check of LAN 5.1.</summary>
public static class LanAddress
{
    /// <summary>Private, link-local and loopback IPv4; <c>::1</c>, <c>fe80::/10</c>, <c>fc00::/7</c>; mapped IPv4 by its IPv4 rule.</summary>
    /// <param name="udp">A UDP target's address: <c>255.255.255.255</c> is local too.</param>
    public static bool IsLocal(IPAddress address, bool udp = false)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return IsLocal(address.MapToIPv4(), udp);
            }

            Span<byte> v6 = stackalloc byte[16];
            address.TryWriteBytes(v6, out _);
            if (IPAddress.IPv6Loopback.Equals(new IPAddress(v6)))
            {
                return true;
            }

            return (v6[0] == 0xFE && (v6[1] & 0xC0) == 0x80) || (v6[0] & 0xFE) == 0xFC;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> v4 = stackalloc byte[4];
        address.TryWriteBytes(v4, out _);
        if (udp && v4[0] == 255 && v4[1] == 255 && v4[2] == 255 && v4[3] == 255)
        {
            return true;
        }

        return v4[0] == 10
            || (v4[0] == 172 && (v4[1] & 0xF0) == 16)
            || (v4[0] == 192 && v4[1] == 168)
            || (v4[0] == 169 && v4[1] == 254)
            || v4[0] == 127;
    }

    /// <summary>The text form of an address (a scope id after <c>%</c> is ignored); false when it is not an address.</summary>
    public static bool IsLocal(string address, bool udp = false)
    {
        ArgumentNullException.ThrowIfNull(address);
        var text = address.Split('%', 2)[0];
        return TryParseStrict(text, out var ip) && IsLocal(ip, udp);
    }

    /// <summary>
    /// An IPv4 address in dotted-quad form (four decimal parts of 0–255 without leading zeros) or an IPv6 address; not the
    /// lenient forms <see cref="IPAddress.TryParse(string, out IPAddress)"/> also accepts (<c>1</c>, <c>1.2.3</c>, <c>0x7f.1</c>).
    /// </summary>
    public static bool TryParseStrict(string text, out IPAddress address)
    {
        address = IPAddress.None;
        if (text.Contains(':', StringComparison.Ordinal))
        {
            if (text.AsSpan().IndexOfAny("%[]") >= 0 || !IPAddress.TryParse(text, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            address = v6;
            return true;
        }

        if (!IsDottedQuad(text))
        {
            return false;
        }

        address = IPAddress.Parse(text);
        return true;
    }

    public static bool IsDottedQuad(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length is < 1 or > 3 || !part.All(char.IsAsciiDigit) || (part.Length > 1 && part[0] == '0') || int.Parse(part, System.Globalization.CultureInfo.InvariantCulture) > 255)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>LAN 5.1 after resolution: every address local, or <c>notLocal</c>; no address at all is <c>notFound</c>.</summary>
    public static LanTargetStatusKind ResolveCheck(IReadOnlyCollection<IPAddress> addresses, bool udp)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0)
        {
            return LanTargetStatusKind.NotFound;
        }

        return addresses.All(a => IsLocal(a, udp)) ? LanTargetStatusKind.Ok : LanTargetStatusKind.NotLocal;
    }

    /// <summary>The address as the server keys its peers: an IPv4-mapped address in its IPv4 form.</summary>
    public static string PeerKey(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (plain.AddressFamily == AddressFamily.InterNetworkV6 && plain.ScopeId != 0)
        {
            plain = new IPAddress(plain.GetAddressBytes());
        }

        return plain.ToString();
    }
}
