using AgentSpeed.Core.Quota.Network;

namespace AgentSpeed.Core.Quota.Cookies;

/// <summary>The browsers whose cookies the session sources read on Windows (QUOTAS 9.10), in the order they are tried.</summary>
public enum BrowserKind
{
    Chrome,
    Edge,
    Brave,
    Vivaldi,
    Chromium,
    Firefox,
}

/// <summary>A site's session as one browser profile holds it: the <c>Cookie</c> header and that browser's user agent (QUOTAS 9.9).</summary>
public sealed record BrowserSession(string Site, string Cookie, string UserAgent, BrowserKind Browser)
{
    /// <summary>The first 8 bytes of the SHA-256 of the header (QUOTAS 9.1): a new sign-in is a new account.</summary>
    public QuotaFingerprint Fingerprint => QuotaFingerprint.Of(Cookie);

    /// <summary>Never shows the header: it carries the session.</summary>
    public override string ToString() => $"BrowserSession({Site}, {Browser})";
}

/// <summary>Why no session was found, when one was looked for.</summary>
public enum BrowserSessionMiss
{
    None,

    /// <summary>No profile of any browser has an unexpired cookie of the site.</summary>
    NoSession,

    /// <summary>Some store that may hold the site's cookies is locked by its running browser and could not be copied.</summary>
    Locked,

    /// <summary>The site's cookies were found but are app-bound (<c>v20</c>) or otherwise undecryptable.</summary>
    Protected,
}

/// <summary>Reads a browser session for a site (QUOTAS 9.10): <c>BrowserCookieReader</c> on Windows, a fixture in tests.</summary>
public interface IBrowserSessionReader
{
    /// <summary>The session for the first of <paramref name="sites"/> any profile has; null with the reason otherwise.</summary>
    BrowserSession? Session(IReadOnlyList<string> sites, long nowMs, out BrowserSessionMiss miss);

    /// <summary>Forgets the browsers whose key could not be unwrapped (the user turned the switch off and on again).</summary>
    void ResetRefusals();
}
