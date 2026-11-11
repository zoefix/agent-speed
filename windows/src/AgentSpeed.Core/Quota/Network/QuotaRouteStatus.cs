namespace AgentSpeed.Core.Quota.Network;

/// <summary>What a quota source's status line says (DESIGN 5, <i>額度, phase 2</i>; REMOTE 11.1 <c>status</c>).</summary>
public enum QuotaStatusKind
{
    /// <summary>The last request (or read) succeeded at <see cref="QuotaRouteStatus.AtMs"/>.</summary>
    Ok,
    NoCredential,

    /// <summary>No browser on this computer has the site's session.</summary>
    NoBrowser,

    /// <summary>Windows: the only stores with the site's cookies are held locked by their running browser (QUOTAS 9.10).</summary>
    BrowserLocked,

    /// <summary>Windows: the site's cookies are app-bound (<c>v20</c>, Chrome 127+), which a user program cannot decrypt.</summary>
    BrowserProtected,

    /// <summary>The token's own expiry has passed: the agent has to renew it.</summary>
    Expired,

    /// <summary>The vendor answered 401 or 403; nothing is asked until the credential changes.</summary>
    Denied,
    Failed,
    NetworkOff,

    /// <summary>Nothing asks yet: the agent is not running and the dashboard is closed.</summary>
    NotRunning,

    /// <summary>A remote host has no CA certificates (REMOTE 11.4).</summary>
    NoCertificates,
}

/// <summary>A status line's state: its kind, the time of an <see cref="QuotaStatusKind.Ok"/> and the host a refusal names.</summary>
public readonly record struct QuotaRouteStatus(QuotaStatusKind Kind, long AtMs = 0, string? Host = null)
{
    public static QuotaRouteStatus Ok(long atMs) => new(QuotaStatusKind.Ok, atMs);

    public static QuotaRouteStatus Denied(string host) => new(QuotaStatusKind.Denied, Host: host);

    public static QuotaRouteStatus Of(QuotaStatusKind kind) => new(kind);

    /// <summary>Shown in the warning style; a success, a pause or the network switch are not problems.</summary>
    public bool IsProblem => Kind is not (QuotaStatusKind.Ok or QuotaStatusKind.NotRunning or QuotaStatusKind.NetworkOff);
}
