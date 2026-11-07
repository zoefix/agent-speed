using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentSpeed.Core.Lan;

public enum LanTargetKind
{
    Udp,
    Http,
}

/// <summary>Why an address does not parse (LAN 5.1); the sheet maps <c>Port</c> and <c>Https</c> to their own sentences.</summary>
public enum LanAddressError
{
    Empty,
    Spaces,
    Port,
    Host,
    Https,
    Scheme,
    Fragment,
    UserInfo,
}

/// <summary>A parsed target address: the host as written, whether it is an IP literal, the port, and for HTTP the Host header and target.</summary>
public sealed partial record LanTargetAddress(string Host, bool Literal, int Port, string? HostHeader = null, string? Target = null)
{
    /// <summary>LAN 5.1: the parts of <paramref name="address"/> for <paramref name="kind"/>, or the reason it is invalid.</summary>
    public static LanTargetAddress? Parse(LanTargetKind kind, string? address, out LanAddressError error)
    {
        error = default;
        if (address is null || address.Length == 0 || address != LanText.Trim(address))
        {
            error = address is null || LanText.Trim(address).Length == 0 ? LanAddressError.Empty : LanAddressError.Spaces;
            return null;
        }

        if (address.Any(c => c <= ' ' || c == '\u007f'))
        {
            error = LanAddressError.Spaces;
            return null;
        }

        return kind == LanTargetKind.Udp ? ParseUdp(address, out error) : ParseHttp(address, out error);
    }

    public static LanTargetAddress? Parse(LanTargetKind kind, string? address) => Parse(kind, address, out _);

    /// <summary>A host the user wrote is a broadcast (DESIGN 14): <c>255.255.255.255</c> or an IPv4 address ending in <c>.255</c>.</summary>
    public bool IsBroadcast => Literal && LanAddress.IsDottedQuad(Host) && Host.EndsWith(".255", StringComparison.Ordinal);

    private static LanTargetAddress? ParseUdp(string address, out LanAddressError error)
    {
        var (hostText, portText) = SplitHostPort(address);
        if (portText is null or "!")
        {
            error = LanAddressError.Port;
            return null;
        }

        if (!ParseHost(hostText, out var host, out var literal))
        {
            error = LanAddressError.Host;
            return null;
        }

        if (ParsePort(portText, null) is not { } port)
        {
            error = LanAddressError.Port;
            return null;
        }

        error = default;
        return new LanTargetAddress(host, literal, port);
    }

    private static LanTargetAddress? ParseHttp(string address, out LanAddressError error)
    {
        if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            error = LanAddressError.Https;
            return null;
        }

        if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            error = LanAddressError.Scheme;
            return null;
        }

        var rest = address[7..];
        if (rest.Contains('#', StringComparison.Ordinal))
        {
            error = LanAddressError.Fragment;
            return null;
        }

        var cut = rest.IndexOfAny(['/', '?']);
        if (cut < 0)
        {
            cut = rest.Length;
        }

        var authority = rest[..cut];
        var target = rest[cut..];
        if (authority.Contains('@', StringComparison.Ordinal))
        {
            error = LanAddressError.UserInfo;
            return null;
        }

        var (hostText, portText) = SplitHostPort(authority);
        if (portText == "!")
        {
            error = LanAddressError.Port;
            return null;
        }

        if (!ParseHost(hostText, out var host, out var literal))
        {
            error = LanAddressError.Host;
            return null;
        }

        if ((portText is null ? 80 : portText.Length == 0 ? null : ParsePort(portText, 80)) is not { } port)
        {
            error = LanAddressError.Port;
            return null;
        }

        if (target.Length == 0 || target[0] == '?')
        {
            target = "/" + target;
        }

        error = default;
        return new LanTargetAddress(host, literal, port, authority, target);
    }

    /// <summary>The host and port text of an authority: a null port when none is written, <c>!</c> when what follows cannot be one.</summary>
    private static (string Host, string? Port) SplitHostPort(string authority)
    {
        if (authority.StartsWith('['))
        {
            var end = authority.IndexOf(']', StringComparison.Ordinal);
            if (end < 0)
            {
                return (authority, null);
            }

            var after = authority[(end + 1)..];
            if (after.Length == 0)
            {
                return (authority, null);
            }

            return after[0] == ':' ? (authority[..(end + 1)], after[1..]) : (authority, "!");
        }

        if (authority.Count(c => c == ':') > 1)
        {
            return (authority, "!");
        }

        var colon = authority.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? (authority, null) : (authority[..colon], authority[(colon + 1)..]);
    }

    private static bool ParseHost(string text, out string host, out bool literal)
    {
        host = text;
        literal = false;
        if (text.StartsWith('['))
        {
            if (!text.EndsWith(']'))
            {
                return false;
            }

            var inner = text[1..^1];
            if (!LanAddress.TryParseStrict(inner.Split('%', 2)[0], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return false;
            }

            host = inner;
            literal = true;
            return true;
        }

        if (text.Length is 0 or > 253)
        {
            return false;
        }

        var labels = text == "." ? [string.Empty] : text.TrimEnd('.').Split('.');
        if (labels.All(label => label.Length > 0 && label.All(char.IsAsciiDigit)))
        {
            literal = true;
            return LanAddress.IsDottedQuad(text);
        }

        return labels.All(label => Label().IsMatch(label));
    }

    private static int? ParsePort(string text, int? fallback)
    {
        if (text.Length == 0 && fallback is not null)
        {
            return fallback;
        }

        if (text.Length is 0 or > 5 || !text.All(char.IsAsciiDigit) || (text.Length > 1 && text[0] == '0'))
        {
            return null;
        }

        var value = int.Parse(text, CultureInfo.InvariantCulture);
        return value is >= 1 and <= 65535 ? value : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Label();
}
