using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentSpeed.Core.Quota;

/// <summary>A cached usage response of Claude Desktop: its URL, when claude.ai computed it, and the decoded body.</summary>
internal sealed record DesktopUsageEntry(string Url, long ObservedMs, JsonElement Body);

/// <summary>
/// One Chromium <i>simple cache</i> entry file (QUOTAS 4.2), read strictly: any mismatch skips the entry, so a changed
/// format shows no quota rather than a wrong one.
/// </summary>
internal static partial class DesktopCacheEntry
{
    public const ulong HeaderMagic = 0xfcfb6d1ba7725c30;
    public const ulong EofMagic = 0xf4fa6f45970d41d8;
    public const int Version = 5;
    public const int HeaderSize = 24;
    public const int EofSize = 24;
    public const int KeyShaFlag = 0x2;
    public const int EntryLimit = 1 << 20;
    public const int BodyLimit = 1 << 20;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [GeneratedRegex(@"^https://claude\.ai/api/organizations/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/usage(\?.*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex UsageUrl();

    [GeneratedRegex(@"^\d+/\d+/", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPrefix();

    /// <summary>The entry's file name: the first 8 bytes of the key's SHA-1 as a little-endian number in hex, then <c>_0</c>.</summary>
    public static string FileName(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Span<byte> digest = stackalloc byte[20];
        SHA1.HashData(Encoding.UTF8.GetBytes(key), digest);
        return BinaryPrimitives.ReadUInt64LittleEndian(digest).ToString("x16", CultureInfo.InvariantCulture) + "_0";
    }

    /// <summary>The URL of a key: the text after its last space, else without a leading <c>&lt;digits&gt;/&lt;digits&gt;/</c>.</summary>
    public static string UrlOf(string key)
    {
        var space = key.LastIndexOf(' ');
        return space >= 0 ? key[(space + 1)..] : KeyPrefix().Replace(key, string.Empty, 1);
    }

    public static bool IsUsageUrl(string url) => UsageUrl().IsMatch(url);

    /// <summary>
    /// The key of an entry from its first bytes (<see cref="HeaderSize"/> + key length), or null when the header is not a
    /// version-5 simple-cache header. <paramref name="keyLength"/> tells how many bytes the key needs.
    /// </summary>
    public static string? Key(ReadOnlySpan<byte> head, out int keyLength)
    {
        keyLength = 0;
        if (head.Length < HeaderSize || BinaryPrimitives.ReadUInt64LittleEndian(head) != HeaderMagic
            || BinaryPrimitives.ReadUInt32LittleEndian(head[8..]) != Version)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
        if (length > EntryLimit)
        {
            return null;
        }

        keyLength = (int)length;
        if (head.Length < HeaderSize + keyLength)
        {
            return null;
        }

        try
        {
            return StrictUtf8.GetString(head.Slice(HeaderSize, keyLength));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>A usage entry's URL, time and body; null for any other or malformed entry (QUOTAS 4.2).</summary>
    public static DesktopUsageEntry? Parse(ReadOnlySpan<byte> data, long modifiedMs)
    {
        if (data.Length > EntryLimit || data.Length < HeaderSize + 2 * EofSize)
        {
            return null;
        }

        if (Key(data, out var keyLength) is not { } key)
        {
            return null;
        }

        var url = UrlOf(key);
        if (!IsUsageUrl(url))
        {
            return null;
        }

        long keyEnd = HeaderSize + keyLength;
        long eof0 = data.Length - EofSize;
        if (BinaryPrimitives.ReadUInt64LittleEndian(data[(int)eof0..]) != EofMagic)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[((int)eof0 + 8)..]);
        long size0 = BinaryPrimitives.ReadUInt32LittleEndian(data[((int)eof0 + 16)..]);
        var stream0End = eof0 - ((flags & KeyShaFlag) != 0 ? 32 : 0);
        var stream0Start = stream0End - size0;
        var eof1 = stream0Start - EofSize;
        if (eof1 < keyEnd || BinaryPrimitives.ReadUInt64LittleEndian(data[(int)eof1..]) != EofMagic)
        {
            return null;
        }

        if (ResponseInfo(data[(int)stream0Start..(int)stream0End]) is not { Status: 200 } info)
        {
            return null;
        }

        if (DecodeBody(data[(int)keyEnd..(int)eof1], info.Headers.GetValueOrDefault("content-encoding")) is not { } body)
        {
            return null;
        }

        var observed = modifiedMs;
        if (info.Headers.TryGetValue("date", out var date) && HttpDate(date) is { } at)
        {
            observed = at;
        }

        return new DesktopUsageEntry(url, observed, body);
    }

    /// <summary>The status line's code and the headers (lower-case names, first value wins) of stream 0.</summary>
    internal static (int? Status, Dictionary<string, string> Headers)? ResponseInfo(ReadOnlySpan<byte> stream0)
    {
        var start = stream0.IndexOf("HTTP/"u8);
        if (start < 0)
        {
            return null;
        }

        var rest = stream0[start..];
        int? status = null;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var first = true;
        while (true)
        {
            var end = rest.IndexOf((byte)0);
            var part = end < 0 ? rest : rest[..end];
            if (part.IsEmpty)
            {
                break;
            }

            var line = Encoding.Latin1.GetString(part);
            if (first)
            {
                var fields = line.Split(' ');
                status = fields.Length > 1 && fields[1].Length > 0 && fields[1].All(char.IsAsciiDigit)
                    && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : null;
                first = false;
            }
            else
            {
                var colon = line.IndexOf(':');
                if (colon >= 0)
                {
                    headers.TryAdd(line[..colon].Trim().ToLowerInvariant(), line[(colon + 1)..].Trim());
                }
            }

            if (end < 0)
            {
                break;
            }

            rest = rest[(end + 1)..];
        }

        return first ? null : (status, headers);
    }

    /// <summary>The body decoded by its content encoding into a JSON object of at most 1 MiB; null otherwise.</summary>
    internal static JsonElement? DecodeBody(ReadOnlySpan<byte> body, string? encoding)
    {
        var name = (encoding ?? "identity").Trim().ToLowerInvariant();
        byte[] decoded;
        try
        {
            switch (name)
            {
                case "identity":
                    if (body.Length > BodyLimit)
                    {
                        return null;
                    }

                    decoded = body.ToArray();
                    break;
                case "zstd":
                case "gzip":
                case "br":
                    using (var input = new MemoryStream(body.ToArray(), writable: false))
                    using (Stream stream = name switch
                    {
                        "zstd" => new ZstdSharp.DecompressionStream(input),
                        "gzip" => new GZipStream(input, CompressionMode.Decompress),
                        _ => new BrotliStream(input, CompressionMode.Decompress),
                    })
                    {
                        if (ReadAtMost(stream, BodyLimit) is not { } read)
                        {
                            return null;
                        }

                        decoded = read;
                    }

                    break;
                default:
                    return null;
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ZstdSharp.ZstdException or InvalidOperationException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(decoded);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[]? ReadAtMost(Stream stream, int limit)
    {
        var buffer = new byte[Math.Min(limit + 1, 64 * 1024)];
        using var output = new MemoryStream();
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > limit)
            {
                return null;
            }
        }

        return output.ToArray();
    }

    /// <summary>An RFC 7231 date (<c>Tue, 10 Nov 2026 19:26:13 GMT</c>) in Unix ms; null when it does not parse.</summary>
    internal static long? HttpDate(string text)
    {
        if (DateTimeOffset.TryParseExact(text, "r", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at)
            || DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at))
        {
            return at.ToUnixTimeMilliseconds();
        }

        return null;
    }
}
