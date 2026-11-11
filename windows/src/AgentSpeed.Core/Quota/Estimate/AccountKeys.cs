using System.Security.Cryptography;
using System.Text;

namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>Account keys and plan strings (QUOTAS 10.1, 10.2). A key is never logged or reported.</summary>
public static class AccountKeys
{
    public const string CodexVendor = "codex";
    public const string CodexChainVendor = "codex-chain";
    public const string ClaudeVendor = "claude";
    public const string DesktopVendor = "claudeDesktop";
    public const int SaltBytes = 32;

    private static readonly string[] PersonalClaudePlans = ["claude_free", "claude_pro", "claude_max", "claude_ultra"];

    /// <summary>The first 16 hex digits of HMAC-SHA256(salt, "&lt;vendor&gt;:&lt;id&gt;").</summary>
    public static string Key(ReadOnlySpan<byte> salt, string vendor, string id)
    {
        ArgumentNullException.ThrowIfNull(vendor);
        ArgumentNullException.ThrowIfNull(id);
        var message = Encoding.UTF8.GetBytes(vendor + ":" + id);
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(salt, message, hash);
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
