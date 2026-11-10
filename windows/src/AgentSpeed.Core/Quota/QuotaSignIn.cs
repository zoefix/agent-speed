using System.Text.Json;

namespace AgentSpeed.Core.Quota;

/// <summary>
/// The signed-in markers of QUOTAS 6: Codex's <c>auth.json</c> and Claude Code's <c>.claude.json</c>. Only the named
/// top-level keys are looked at; every other value is skipped unread, and nothing is copied, kept or logged. A file is
/// read again only when its time or size changes, and at most once a minute. Not thread-safe.
/// </summary>
public sealed class QuotaSignIn
{
    public const long RereadMs = 60_000;
    private const long MaxFileBytes = 64L * 1024 * 1024;

    private sealed class Marker(string path, Func<ReadOnlySpan<byte>, bool> test)
    {
        public string Path { get; } = path;

        public Func<ReadOnlySpan<byte>, bool> Test { get; } = test;

        public DateTime? Modified { get; set; }

        public long Length { get; set; } = -1;

        public long ReadAtMs { get; set; } = long.MinValue;

        public bool SignedIn { get; set; }
    }

    private readonly Marker _codex;
    private readonly Marker _claude;
    private readonly Func<long> _nowMs;

    public QuotaSignIn(string codexAuthFile, string claudeStateFile, Func<long>? nowMs = null)
    {
        _codex = new Marker(codexAuthFile, IsCodexSignedIn);
        _claude = new Marker(claudeStateFile, IsClaudeSignedIn);
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary><c>$CODEX_HOME/auth.json</c> (else <c>~/.codex</c>) and <c>$CLAUDE_CONFIG_DIR/.claude.json</c> (else <c>~/.claude.json</c>).</summary>
    public static QuotaSignIn ForThisComputer(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codex = environment("CODEX_HOME") is { Length: > 0 } codexHome ? codexHome : Path.Combine(home, ".codex");
        var claude = environment("CLAUDE_CONFIG_DIR") is { Length: > 0 } config ? Path.Combine(config, ".claude.json") : Path.Combine(home, ".claude.json");
        return new QuotaSignIn(Path.Combine(codex, "auth.json"), claude);
    }

    public bool Codex => Check(_codex);

    public bool ClaudeCode => Check(_claude);

    private bool Check(Marker marker)
    {
        var info = new FileInfo(marker.Path);
        if (!info.Exists)
        {
            marker.Modified = null;
            marker.Length = -1;
            marker.SignedIn = false;
            return false;
        }

        var now = _nowMs();
        var changed = marker.Modified != info.LastWriteTimeUtc || marker.Length != info.Length;
        if (!changed || (marker.ReadAtMs != long.MinValue && now - marker.ReadAtMs < RereadMs))
        {
            // Unchanged, or changed again within the minute: the next check after it reads the new version.
            return marker.SignedIn;
        }

        marker.Modified = info.LastWriteTimeUtc;
        marker.Length = info.Length;
        marker.ReadAtMs = now;
        try
        {
            if (info.Length > MaxFileBytes)
            {
                marker.SignedIn = false;
                return false;
            }

            using var stream = new FileStream(marker.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            marker.SignedIn = marker.Test(bytes);
            Array.Clear(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            marker.SignedIn = false;
        }

        return marker.SignedIn;
    }

    /// <summary>An object with <c>auth_mode == "chatgpt"</c> or a <c>tokens</c> object.</summary>
    internal static bool IsCodexSignedIn(ReadOnlySpan<byte> json)
    {
        var found = false;
        Scan(json, (ref Utf8JsonReader reader) =>
        {
            if (reader.ValueTextEquals("auth_mode"u8))
            {
                reader.Read();
                found |= reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("chatgpt"u8);
            }
            else if (reader.ValueTextEquals("tokens"u8))
            {
                reader.Read();
                found |= reader.TokenType == JsonTokenType.StartObject;
            }
            else
            {
                reader.Read();
            }
        });
        return found;
    }

    /// <summary>An object with an <c>oauthAccount</c> object.</summary>
    internal static bool IsClaudeSignedIn(ReadOnlySpan<byte> json)
    {
        var found = false;
        Scan(json, (ref Utf8JsonReader reader) =>
        {
            var named = reader.ValueTextEquals("oauthAccount"u8);
            reader.Read();
            found |= named && reader.TokenType == JsonTokenType.StartObject;
        });
        return found;
    }

    private delegate void PropertyVisitor(ref Utf8JsonReader reader);

    /// <summary>Visits each top-level property name (the visitor reads its value token); values are skipped after it.</summary>
    private static void Scan(ReadOnlySpan<byte> json, PropertyVisitor visit)
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
                visit(ref reader);
                reader.Skip();
            }
        }
        catch (JsonException)
        {
            // Unreadable counts as not signed in.
        }
    }
}
