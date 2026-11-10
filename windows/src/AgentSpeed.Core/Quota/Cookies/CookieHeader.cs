namespace AgentSpeed.Core.Quota.Cookies;

/// <summary>
/// The <c>Cookie</c> header of one site (QUOTAS 9.10): every row whose host is the site or one of its subdomains and that
/// has not expired, joined in database order.
/// </summary>
internal static class CookieHeader
{
    public static bool Matches(string host, string site)
    {
        var bare = host.StartsWith('.') ? host[1..] : host;
        return string.Equals(bare, site, StringComparison.Ordinal) || bare.EndsWith("." + site, StringComparison.Ordinal);
    }

    public static bool IsLive(BrowserCookie cookie, long nowS) => cookie.ExpiresS == 0 || cookie.ExpiresS > nowS;

    /// <summary>The rows a header would use.</summary>
    public static List<BrowserCookie> Usable(IEnumerable<BrowserCookie> cookies, string site, long nowS) =>
        [.. cookies.Where(cookie => Matches(cookie.Host, site) && IsLive(cookie, nowS))];

    /// <summary>
    /// A name or value that could break the header is never sent: a control character, <c>;</c> or <c>=</c> in the name,
    /// an empty name. Non-ASCII is left out too, since the Windows HTTP stack refuses it in a header.
    /// </summary>
    public static bool IsSendable(BrowserCookie cookie) =>
        cookie.Name.Length > 0
        && !cookie.Name.Any(c => IsUnsafe(c) || c is ';' or '=')
        && !cookie.Value.Any(IsUnsafe);

    /// <summary>Null when no row is left.</summary>
    public static string? Join(IEnumerable<BrowserCookie> cookies, string site, long nowS)
    {
        var parts = Usable(cookies, site, nowS).Where(IsSendable).Select(cookie => cookie.Name + "=" + cookie.Value).ToList();
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static bool IsUnsafe(char c) => c is < (char)0x20 or > (char)0x7E;
}
