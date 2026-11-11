using System.Security.Cryptography;
using System.Text;

namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// Account keys, report tags and plan strings (QUOTAS 10.1, 10.2, REPORTING-EXTENSIONS 11.2). A key is never logged or
/// reported.
/// </summary>
public static class AccountKeys
{
    public const string CodexVendor = "codex";
    public const string CodexChainVendor = "codex-chain";
    public const string ClaudeVendor = "claude";
    public const string DesktopVendor = "claudeDesktop";
    public const string PairVendor = "pair";
    public const string HostVendor = "host";

    /// <summary>The machine of this computer's own pairs; a host's pairs name the host's id.</summary>
    public const string LocalMachine = "local";

    /// <summary>The text whose SHA-256 is the account salt.</summary>
    public const string SaltText = "agent-speed quota account v1";

    public const int SaltBytes = 32;

    private const string ReportTagDomain = "agent-speed quota account|";

    private static readonly byte[] AccountSalt = SHA256.HashData(Encoding.ASCII.GetBytes(SaltText));

    private static readonly string[] PersonalClaudePlans = ["claude_free", "claude_pro", "claude_max", "claude_ultra"];

    /// <summary>
    /// The account salt: the same in every install, so that one account has one key on all of the user's computers and one
    /// report tag per service. Remote hosts receive it (REMOTE 11.1); a reporting service never does.
    /// </summary>
    public static ReadOnlySpan<byte> Salt => AccountSalt;

    /// <summary>The first 16 hex digits of HMAC-SHA256(salt, "&lt;vendor&gt;:&lt;id&gt;").</summary>
    public static string Key(ReadOnlySpan<byte> salt, string vendor, string id)
    {
        ArgumentNullException.ThrowIfNull(vendor);
        ArgumentNullException.ThrowIfNull(id);
        return Hmac(salt, vendor + ":" + id);
    }

    /// <summary>
    /// The key of an account without an id: the pair (this computer, <see cref="LocalMachine"/>, or one of its hosts, source),
    /// keyed with the install id so that it stays apart between computers.
    /// </summary>
    public static string PairKey(ReadOnlySpan<byte> salt, string install, string machine, string source)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(source);
        return Key(salt, PairVendor, install + ":" + machine + ":" + source);
    }

    /// <summary>
    /// REPORTING-EXTENSIONS 11.2 <c>account</c>: an account's tag for one service, keyed by the service's api origin so that
    /// services cannot match their tags.
    /// </summary>
    public static string ReportTag(string origin, string key)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(key);
        return Hmac(Encoding.UTF8.GetBytes(ReportTagDomain + origin), key);
    }

    private static string Hmac(ReadOnlySpan<byte> secret, string text)
    {
        var message = Encoding.UTF8.GetBytes(text);
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(secret, message, hash);
        return Convert.ToHexStringLower(hash[..8]);
    }

    /// <summary>A plan or tier of 1–64 characters of <c>[a-z0-9_.-]</c>, else null.</summary>
    public static string? CleanPlan(string? value)
    {
        if (value is not { Length: >= 1 and <= 64 })
        {
            return null;
        }

        foreach (var c in value)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.' or '-'))
            {
                return null;
            }
        }

        return value;
    }

    public static bool IsPersonalClaudePlan(string? plan) => plan is not null && PersonalClaudePlans.Contains(plan, StringComparer.Ordinal);

    /// <summary>A plausible key: 16 lowercase hex digits (REMOTE 11.1).</summary>
    public static bool IsKey(string? value)
    {
        if (value is not { Length: 16 })
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
