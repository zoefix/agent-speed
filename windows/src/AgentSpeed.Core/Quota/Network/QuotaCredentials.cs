using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Quota.Vendors;

namespace AgentSpeed.Core.Quota.Network;

/// <summary>A token read from an agent's sign-in file, for one request only. <see cref="ToString"/> never shows it.</summary>
public sealed class QuotaToken(string value, long? expiresAtMs = null)
{
    public string Value { get; } = value;

    /// <summary>Null when the file names no expiry.</summary>
    public long? ExpiresAtMs { get; } = expiresAtMs;

    public QuotaFingerprint Fingerprint => QuotaFingerprint.Of(Value);

    public bool IsExpired(long nowMs, long marginMs = 0) => ExpiresAtMs is { } at && nowMs >= at - marginMs;

    public override string ToString() => "QuotaToken";
}

/// <summary>
/// The sign-ins of QUOTAS 9.2, 9.5–9.7. Each file is read right before a request, scanned in memory for the named members
/// only (a refresh token is skipped unread), its bytes cleared afterwards, and nothing of it is kept, written or logged.
/// </summary>
public static class QuotaCredentials
{
    public const int MaxFileBytes = 1 << 20;

    /// <summary>A regular file of at most 1 MiB; null otherwise. The caller clears the bytes when done.</summary>
    public static byte[]? ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes || (info.Attributes & FileAttributes.Directory) != 0)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            if (stream.Length > MaxFileBytes)
            {
                return null;
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A folder named by an environment variable (empty counts as unset).</summary>
    public static string? Folder(string variable, Func<string, string?> environment) =>
        environment(variable) is { Length: > 0 } value ? value.Trim() : null;

    /// <summary><c>$KIMI_CODE_HOME/credentials/kimi-code.json</c>, else <c>~/.kimi-code/…</c>, else the legacy <c>~/.kimi/…</c>.</summary>
    public static string? KimiFile(string home, Func<string, string?> environment)
    {
        string?[] roots = [Folder("KIMI_CODE_HOME", environment), Path.Combine(home, ".kimi-code"), Path.Combine(home, ".kimi")];
        return roots.OfType<string>().Select(root => Path.Combine(root, "credentials", "kimi-code.json")).FirstOrDefault(File.Exists);
    }

    /// <summary><c>access_token</c> with <c>expires_at</c> in seconds (above 1e11, milliseconds).</summary>
    public static QuotaToken? Kimi(string home, Func<string, string?> environment) =>
        KimiFile(home, environment) is { } file ? Token(file, "access_token"u8, "expires_at"u8, VendorValue.EpochMs) : null;

    /// <summary><c>&lt;home&gt;/.gemini/oauth_creds.json</c> (<c>$GEMINI_CLI_HOME</c>, else <c>~</c>): <c>access_token</c> with <c>expiry_date</c> in ms.</summary>
    public static QuotaToken? Gemini(string home, Func<string, string?> environment) =>
        Token(Path.Combine(Folder("GEMINI_CLI_HOME", environment) ?? home, ".gemini", "oauth_creds.json"), "access_token"u8, "expiry_date"u8,
            VendorValue.Truncated);

    /// <summary>
    /// The first entry of <c>$GROK_HOME/auth.json</c> (else <c>~/.grok/auth.json</c>), an object keyed by scope URLs: its
    /// <c>key</c>, else <c>access_token</c>, with <c>expires_at</c> (ISO 8601).
    /// </summary>
    public static QuotaToken? Grok(string home, Func<string, string?> environment)
    {
        var bytes = ReadFile(Path.Combine(Folder("GROK_HOME", environment) ?? Path.Combine(home, ".grok"), "auth.json"));
        if (bytes is null)
        {
            return null;
        }

        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject || !reader.Read() || reader.TokenType != JsonTokenType.PropertyName
                || !reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            string? key = null, access = null;
            long? expires = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("key"u8))
                {
                    key = NonEmptyString(ref reader);
                }
                else if (reader.ValueTextEquals("access_token"u8))
                {
                    access = NonEmptyString(ref reader);
                }
                else if (reader.ValueTextEquals("expires_at"u8))
                {
                    reader.Read();
                    expires = reader.TokenType == JsonTokenType.String && reader.GetString() is { } text ? VendorValue.IsoMs(text.Trim()) : null;
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            return (key ?? access) is { } value ? new QuotaToken(value, expires) : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary><c>FACTORY_API_KEY</c> from the app's environment, else its line in <c>~/.factory/.env</c>. <c>settings.json</c> is never read.</summary>
    public static string? Droid(string home, Func<string, string?> environment)
    {
        if (environment("FACTORY_API_KEY")?.Trim() is { Length: > 0 } key)
        {
            return key;
        }

        var bytes = ReadFile(Path.Combine(home, ".factory", ".env"));
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return DroidLimits.KeyFromEnvFile(Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>A top-level string member and a numeric expiry, everything else skipped.</summary>
    private static QuotaToken? Token(string path, ReadOnlySpan<byte> valueName, ReadOnlySpan<byte> expiryName, Func<double, long> toMs)
    {
        var bytes = ReadFile(path);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            string? value = null;
            long? expires = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (value is null && reader.ValueTextEquals(valueName))
                {
                    value = NonEmptyString(ref reader);
                }
                else if (reader.ValueTextEquals(expiryName))
                {
                    reader.Read();
                    expires = reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var number) && double.IsFinite(number) ? toMs(number) : null;
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            return value is null ? null : new QuotaToken(value, expires);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string? NonEmptyString(ref Utf8JsonReader reader)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return null;
        }

        return reader.GetString() is { Length: > 0 } text ? text : null;
    }

    /// <summary>Printable ASCII only: a credential never breaks a header.</summary>
    public static bool IsHeaderSafe(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 8192 && value.All(c => c is >= ' ' and <= '~');
}
