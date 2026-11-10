using System.Security.Cryptography;
using System.Text;

namespace AgentSpeed.Core.Quota.Cookies;

/// <summary>
/// Chromium's cookie encryption (QUOTAS 9.10): on Windows <c>v10</c>/<c>v11</c> are AES-256-GCM with the browser's
/// DPAPI-wrapped key; the macOS and Linux AES-128-CBC scheme is kept as pure functions for the shared fixtures. <c>v20</c>
/// (app-bound) values cannot be read by a user program. Plaintext buffers are zeroed after use.
/// </summary>
internal static class ChromeCookieCrypto
{
    /// <summary>Chrome 130 and later (<c>meta.version</c> ≥ 24) put the SHA-256 of <c>host_key</c> before the value.</summary>
    public const int HostHashVersion = 24;
    public const int MacIterations = 1003;
    public const int LinuxIterations = 1;
    public const int GcmKeyBytes = 32;

    private const int PrefixBytes = 3;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int HostHashBytes = 32;
    private const int CbcKeyBytes = 16;
    private const int CbcBlockBytes = 16;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static ReadOnlySpan<byte> Salt => "saltysalt"u8;

    /// <summary>PBKDF2-HMAC-SHA1(password, <c>saltysalt</c>, iterations, 16 bytes); null without iterations.</summary>
    public static byte[]? CbcKey(ReadOnlySpan<byte> password, int iterations) =>
        iterations > 0 ? Rfc2898DeriveBytes.Pbkdf2(password, Salt, iterations, HashAlgorithmName.SHA1, CbcKeyBytes) : null;

    /// <summary>A macOS or Linux <c>v10</c>/<c>v11</c> value: AES-128-CBC with an IV of 16 spaces and PKCS#7 padding.</summary>
    public static string? DecryptCbc(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> key, int metaVersion)
    {
        if (!IsV10OrV11(blob) || key.Length != CbcKeyBytes)
        {
            return null;
        }

        var body = blob[PrefixBytes..];
        if (body.IsEmpty || body.Length % CbcBlockBytes != 0)
        {
            return null;
        }

        Span<byte> iv = stackalloc byte[CbcBlockBytes];
        iv.Fill(0x20);
        var keyCopy = key.ToArray();
        var plain = new byte[body.Length];
        try
        {
            using var aes = Aes.Create();
            aes.Key = keyCopy;
            return aes.TryDecryptCbc(body, iv, plain, out var written, PaddingMode.PKCS7)
                ? Plaintext(plain.AsSpan(0, written), metaVersion)
                : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>A Windows <c>v10</c>/<c>v11</c> value: a 12-byte nonce after the prefix, the ciphertext, then a 16-byte tag.</summary>
    public static string? DecryptGcm(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> key, int metaVersion)
    {
        if (blob.Length < PrefixBytes + NonceBytes + TagBytes || !IsV10OrV11(blob) || key.Length != GcmKeyBytes
            || !AesGcm.IsSupported)
        {
            return null;
        }

        var nonce = blob.Slice(PrefixBytes, NonceBytes);
        var cipher = blob[(PrefixBytes + NonceBytes)..^TagBytes];
        var tag = blob[^TagBytes..];
        var plain = new byte[cipher.Length];
        try
        {
            using var gcm = new AesGcm(key, TagBytes);
            gcm.Decrypt(nonce, cipher, tag, plain);
            return Plaintext(plain, metaVersion);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static bool IsV10OrV11(ReadOnlySpan<byte> blob) =>
        blob.Length >= PrefixBytes && blob[0] == (byte)'v' && blob[1] == (byte)'1' && blob[2] is (byte)'0' or (byte)'1';

    /// <summary>Strict UTF-8 of the decrypted bytes, without the host hash from version 24 on.</summary>
    private static string? Plaintext(ReadOnlySpan<byte> bytes, int metaVersion)
    {
        if (metaVersion >= HostHashVersion)
        {
            if (bytes.Length < HostHashBytes)
            {
                return null;
            }

            bytes = bytes[HostHashBytes..];
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
