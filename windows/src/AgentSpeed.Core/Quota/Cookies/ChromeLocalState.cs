using System.Security.Cryptography;
using System.Text.Json;

namespace AgentSpeed.Core.Quota.Cookies;

/// <summary>
/// A Chromium browser's cookie key on Windows (QUOTAS 9.10): <c>Local State</c> <c>os_crypt.encrypted_key</c>, base64,
/// <c>DPAPI</c> prefix dropped, unwrapped by DPAPI to 32 bytes.
/// </summary>
internal static class ChromeLocalState
{
    public const int MaxBytes = 8 << 20;

    private static ReadOnlySpan<byte> DpapiPrefix => "DPAPI"u8;

    /// <summary>The 32-byte AES-256-GCM key; null when it cannot be had. The caller zeroes it.</summary>
    public static byte[]? Key(ChromiumBrowser browser, IBrowserKeyUnwrapper unwrapper)
    {
        if (WrappedKey(browser.LocalState) is not { } wrapped)
        {
            return null;
        }

        var key = unwrapper.Unwrap(browser.Kind, wrapped);
        if (key is { Length: ChromeCookieCrypto.GcmKeyBytes })
        {
            return key;
        }

        if (key is not null)
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return null;
    }

    /// <summary>The DPAPI blob of <c>os_crypt.encrypted_key</c> without its prefix; null when missing or malformed.</summary>
    internal static byte[]? WrappedKey(string localState)
    {
        if (CookieFiles.ReadSmall(localState, MaxBytes) is not { } bytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("os_crypt", out var crypt)
                || crypt.ValueKind != JsonValueKind.Object
                || !crypt.TryGetProperty("encrypted_key", out var encoded)
                || encoded.ValueKind != JsonValueKind.String
                || !encoded.TryGetBytesFromBase64(out var decoded)
                || decoded.Length <= DpapiPrefix.Length
                || !decoded.AsSpan().StartsWith(DpapiPrefix))
            {
                return null;
            }

            return decoded[DpapiPrefix.Length..];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
