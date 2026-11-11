using System.Text.Json;

namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// The sign-in ids of QUOTAS 10.1 as account keys: Codex's <c>auth.json</c> <c>tokens.account_id</c> (nothing else of it) and
/// Claude Code's <c>.claude.json</c> <c>oauthAccount</c> (organization and account uuids, organization type and rate-limit
/// tier). Only those values are read; every other value, the tokens among them, is skipped unread. Ids become keys at once
/// and are neither kept nor logged. A file is read again only when its time or size changes, at most once a minute. Not
/// thread-safe.
/// </summary>
public sealed class EstimateSignIn
{
    private const long MaxFileBytes = 64L * 1024 * 1024;

    private sealed class Watched(string path)
    {
        public string Path { get; } = path;

        public DateTime? Modified { get; set; }

        public long Length { get; set; } = -1;

        public long ReadAtMs { get; set; } = long.MinValue;
    }

    private readonly byte[] _salt;
    private readonly Watched _codex;
    private readonly Watched _claude;

    public EstimateSignIn(ReadOnlySpan<byte> salt, string codexAuthFile, string claudeStateFile)
    {
        ArgumentNullException.ThrowIfNull(codexAuthFile);
        ArgumentNullException.ThrowIfNull(claudeStateFile);
        _salt = salt.ToArray();
        _codex = new Watched(codexAuthFile);
        _claude = new Watched(claudeStateFile);
    }

    /// <summary>The key of the Codex sign-in as last read; null while signed out or unreadable.</summary>
    public string? CodexKey { get; private set; }

    /// <summary>The Claude CLI's profile as last read, at the time it was read; null while signed out or unreadable.</summary>
    public ClaudeProfile? ClaudeProfile { get; private set; }

    /// <summary>Reads <c>auth.json</c> again when it changed; true when <see cref="CodexKey"/> may have changed.</summary>
    public bool RefreshCodex(long nowMs)
    {
        if (Changed(_codex, nowMs) is not { } bytes)
        {
            return false;
        }

        try
        {
            CodexKey = CodexAccountId(bytes) is { } id ? AccountKeys.Key(_salt, AccountKeys.CodexVendor, id) : null;
        }
        finally
        {
            Array.Clear(bytes);
        }

        return true;
    }

    /// <summary>Reads <c>.claude.json</c> again when it changed; true when <see cref="ClaudeProfile"/> may have changed.</summary>
    public bool RefreshClaude(long nowMs)
    {
        if (Changed(_claude, nowMs) is not { } bytes)
        {
            return false;
        }

        try
        {
            ClaudeProfile = ClaudeAccount(bytes) is { } a
                ? new ClaudeProfile(nowMs, AccountKeys.Key(_salt, AccountKeys.ClaudeVendor, a.Org + ":" + a.Account),
                    AccountKeys.Key(_salt, AccountKeys.DesktopVendor, a.Org), AccountKeys.CleanPlan(a.Type), AccountKeys.CleanPlan(a.Tier))
                : null;
        }
        finally
        {
            Array.Clear(bytes);
        }

        return true;
    }

    /// <summary>The file's bytes when it changed and may be read again now; null otherwise (a vanished file reads as empty).</summary>
    private static byte[]? Changed(Watched file, long nowMs)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(file.Path);
            if (!info.Exists)
            {
                if (file.Length == -1 && file.Modified is null && file.ReadAtMs != long.MinValue)
                {
                    return null;
                }

                file.Modified = null;
                file.Length = -1;
                file.ReadAtMs = nowMs;
                return [];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var changed = file.Modified != info.LastWriteTimeUtc || file.Length != info.Length;
        if (!changed || (file.ReadAtMs != long.MinValue && nowMs - file.ReadAtMs < QuotaSignIn.RereadMs))
        {
            return null;
        }

        file.Modified = info.LastWriteTimeUtc;
        file.Length = info.Length;
        file.ReadAtMs = nowMs;
        if (info.Length > MaxFileBytes)
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary><c>tokens.account_id</c> of a top-level <c>tokens</c> object; every other value is skipped.</summary>
    internal static string? CodexAccountId(ReadOnlySpan<byte> json)
    {
        string? id = null;
        Visit(json, "tokens"u8, (ref Utf8JsonReader reader) =>
        {
            if (reader.ValueTextEquals("account_id"u8))
            {
                reader.Read();
                id = Text(ref reader, 256);
            }
            else
            {
                reader.Read();
            }
        });
        return id;
    }

    /// <summary>The uuids, type and tier of a top-level <c>oauthAccount</c> object; null without both uuids.</summary>
    internal static (string Org, string Account, string? Type, string? Tier)? ClaudeAccount(ReadOnlySpan<byte> json)
    {
        string? org = null, account = null, type = null, tier = null;
        Visit(json, "oauthAccount"u8, (ref Utf8JsonReader reader) =>
        {
            ref string? target = ref org;
            var known = true;
            if (reader.ValueTextEquals("organizationUuid"u8))
            {
                target = ref org;
            }
            else if (reader.ValueTextEquals("accountUuid"u8))
            {
                target = ref account;
            }
            else if (reader.ValueTextEquals("organizationType"u8))
            {
                target = ref type;
            }
            else if (reader.ValueTextEquals("organizationRateLimitTier"u8))
            {
                target = ref tier;
            }
            else
            {
                known = false;
            }

            reader.Read();
            if (known)
            {
                target = Text(ref reader, 256);
            }
        });
        return org is not null && account is not null ? (org, account, type, tier) : null;
    }

    private delegate void MemberVisitor(ref Utf8JsonReader reader);

    /// <summary>Calls <paramref name="visit"/> on each member name of the top-level object <paramref name="name"/>; the visitor reads its value token.</summary>
    private static void Visit(ReadOnlySpan<byte> json, ReadOnlySpan<byte> name, MemberVisitor visit)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 512, CommentHandling = JsonCommentHandling.Skip });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var wanted = reader.ValueTextEquals(name);
                reader.Read();
                if (!wanted || reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    visit(ref reader);
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            // Unreadable counts as signed out.
        }
    }

    /// <summary>A string of 1–<paramref name="maxCharacters"/> characters at the reader's token, else null.</summary>
    private static string? Text(ref Utf8JsonReader reader, int maxCharacters)
    {
        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > maxCharacters * 6)
        {
            return null;
        }

        return reader.GetString() is { Length: > 0 } text && text.Length <= maxCharacters ? text : null;
    }
}
