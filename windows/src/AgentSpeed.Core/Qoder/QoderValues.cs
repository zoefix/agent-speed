using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Qoder;

/// <summary><c>chat_message.token_info</c> of an assistant row as token values (METRICS 3.2); never its other members.</summary>
internal readonly record struct QoderUsage(long Prompt, long Completion, long Cached);

/// <summary>
/// The JSON columns of Qoder IDE's database (METRICS 4.18), each read only when it is exactly one JSON object (3.6; <c>NaN</c>
/// and trailing text make it absent): <c>token_info</c>, <c>model_info.model_key</c> and
/// <c>chat_record.extra["ai-coding/turn-info"].finishedAt</c>. A later duplicate member replaces an earlier one.
/// </summary>
internal static class QoderValues
{
    private const double TwoTo63 = 9_223_372_036_854_775_808d;

    /// <summary><c>prompt_tokens</c>, <c>completion_tokens</c> and <c>cached_tokens</c>; null when the text is no JSON object.</summary>
    public static QoderUsage? Usage(ReadOnlySpan<byte> json)
    {
        long prompt = 0, completion = 0, cached = 0;
        var parsed = Members(json, (ref Utf8JsonReader reader) =>
        {
            if (reader.ValueTextEquals("prompt_tokens"u8))
            {
                reader.Read();
                prompt = JsonScan.ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("completion_tokens"u8))
            {
                reader.Read();
                completion = JsonScan.ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("cached_tokens"u8))
            {
                reader.Read();
                cached = JsonScan.ReadCount(ref reader);
            }
            else
            {
                Skip(ref reader);
            }
        });
        return parsed ? new QoderUsage(prompt, completion, cached) : null;
    }

    /// <summary><c>model_key</c> trimmed when it is a string that is not blank: the selected tier or a custom model's key.</summary>
    public static string? ModelKey(ReadOnlySpan<byte> json)
    {
        string? key = null;
        var parsed = Members(json, (ref Utf8JsonReader reader) =>
        {
            if (reader.ValueTextEquals("model_key"u8))
            {
                reader.Read();
                key = Trimmed(JsonScan.ReadToken(ref reader));
            }
            else
            {
                Skip(ref reader);
            }
        });
        return parsed ? key : null;
    }

    /// <summary><c>extra["ai-coding/turn-info"].finishedAt</c> (Unix ms, finite, truncated); null when absent.</summary>
    public static long? FinishedAt(ReadOnlySpan<byte> json)
    {
        long? finished = null;
        var parsed = Members(json, (ref Utf8JsonReader reader) =>
        {
            if (!reader.ValueTextEquals("ai-coding/turn-info"u8))
            {
                Skip(ref reader);
                return;
            }

            reader.Read();
            finished = null;
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                JsonScan.SkipValue(ref reader);
                return;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isFinished = reader.ValueTextEquals("finishedAt"u8);
                reader.Read();
                if (isFinished)
                {
                    finished = FiniteMs(ref reader);
                }
                else
                {
                    JsonScan.SkipValue(ref reader);
                }
            }
        });
        return parsed ? finished : null;
    }

    /// <summary>
    /// The working folder of <c>chat_session.project_uri</c>: a path as it is, or a <c>file://</c> URI's path, percent-decoded,
    /// without the <c>/</c> before a drive letter (<c>file:///c%3A/work/app</c> → <c>c:/work/app</c>); null when blank.
    /// </summary>
    public static string? ProjectPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        if (uri.Length < 7 || !uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var rest = uri[7..];
        if (!rest.StartsWith('/'))
        {
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            rest = slash >= 0 ? rest[slash..] : string.Empty;
        }

        var decoded = PercentDecoded(rest);
        if (decoded.Length >= 3 && decoded[0] == '/' && char.IsAsciiLetter(decoded[1]) && decoded[2] == ':')
        {
            decoded = decoded[1..];
        }

        return decoded.Length > 0 ? decoded : null;
    }

    /// <summary>A string trimmed; null when blank.</summary>
    public static string? Trimmed(string? value) => value?.Trim() is { Length: > 0 } text ? text : null;

    private delegate void MemberReader(ref Utf8JsonReader reader);

    /// <summary>Feeds every member of one JSON object to <paramref name="member"/>; false when the text is not exactly one object.</summary>
    private static bool Members(ReadOnlySpan<byte> json, MemberReader member)
    {
        var reader = new Utf8JsonReader(json, JsonScan.Options);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                member(ref reader);
            }

            return reader.TokenType == JsonTokenType.EndObject && !reader.Read();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void Skip(ref Utf8JsonReader reader)
    {
        reader.Read();
        JsonScan.SkipValue(ref reader);
    }

    /// <summary>A finite number truncated toward zero and saturated to 64 bits (METRICS 3.6); null for anything else.</summary>
    private static long? FiniteMs(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            JsonScan.SkipValue(ref reader);
            return null;
        }

        if (reader.TryGetInt64(out var exact))
        {
            return exact;
        }

        if (!reader.TryGetDouble(out var value) || !double.IsFinite(value))
        {
            return null;
        }

        return value >= TwoTo63 ? long.MaxValue : value <= -TwoTo63 ? long.MinValue : (long)Math.Truncate(value);
    }

    /// <summary>Percent-decoding as Python's <c>urllib.parse.unquote</c>: runs of <c>%XX</c> are UTF-8, invalid bytes U+FFFD.</summary>
    private static string PercentDecoded(string text)
    {
        if (!text.Contains('%', StringComparison.Ordinal))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        var bytes = new List<byte>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 2 < text.Length && char.IsAsciiHexDigit(text[i + 1]) && char.IsAsciiHexDigit(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }

            Flush();
            result.Append(text[i]);
        }

        Flush();
        return result.ToString();

        void Flush()
        {
            if (bytes.Count > 0)
            {
                result.Append(Encoding.UTF8.GetString([.. bytes]));
                bytes.Clear();
            }
        }
    }
}
