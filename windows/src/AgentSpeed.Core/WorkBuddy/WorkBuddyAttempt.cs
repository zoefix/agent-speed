using System.Text.Json;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.WorkBuddy;

/// <summary>
/// The measured attempt that answered a model call (METRICS 4.9 Timing model, CLI 2.184.0 and later): the last element of
/// <c>providerData.rawUsage.model_calls</c>, in truncated milliseconds. Earlier attempts failed and are not read.
/// </summary>
/// <param name="DurationMs">From the attempt's start to the end of its stream; above 0.</param>
/// <param name="FirstTokenMs">To its first chunk with content; above 0 and at most <paramref name="DurationMs"/>, else null.</param>
internal readonly record struct WorkBuddyAttempt(long DurationMs, long? FirstTokenMs)
{
    private const double TwoTo63 = 9_223_372_036_854_775_808.0;

    /// <summary>
    /// The call's measured start (saturating) and first token for a usage line written at <paramref name="endMs"/>; the first
    /// token never passes <paramref name="endMs"/>.
    /// </summary>
    public (long StartMs, long? FirstMs) Times(long endMs)
    {
        var start = endMs < long.MinValue + DurationMs ? long.MinValue : endMs - DurationMs;
        return (start, FirstTokenMs is { } first ? start + first : null);
    }

    /// <summary>
    /// Reads <c>model_calls</c>; the reader is on its value and ends on its last token. Null unless the value is a non-empty array
    /// whose last element is an object with a finite <c>duration_ms</c> above 0 once truncated.
    /// </summary>
    public static WorkBuddyAttempt? ReadLast(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            JsonScan.SkipValue(ref reader);
            return null;
        }

        WorkBuddyAttempt? last = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                last = ReadOne(ref reader);
            }
            else
            {
                last = null;
                JsonScan.SkipValue(ref reader);
            }
        }

        return last;
    }

    // The reader is on the attempt's object and ends on its EndObject; a repeated key's last value counts.
    private static WorkBuddyAttempt? ReadOne(ref Utf8JsonReader reader)
    {
        long? duration = null, first = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("duration_ms"u8))
            {
                reader.Read();
                duration = ReadFiniteMs(ref reader);
            }
            else if (reader.ValueTextEquals("time_to_first_token_ms"u8))
            {
                reader.Read();
                first = ReadFiniteMs(ref reader);
            }
            else
            {
                reader.Read();
                JsonScan.SkipValue(ref reader);
            }
        }

        if (duration is not { } d || d <= 0)
        {
            return null;
        }

        return new WorkBuddyAttempt(d, first is { } t && t > 0 && t <= d ? t : null);
    }

    /// <summary>A finite number truncated toward zero and saturated to 64 bits (METRICS 3.6); null for anything else.</summary>
    private static long? ReadFiniteMs(ref Utf8JsonReader reader)
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
}
