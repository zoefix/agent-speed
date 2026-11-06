using System.Text.Json;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Devin;

internal enum DevinRole : byte
{
    Other,
    User,
    Tool,
    Assistant,
}

/// <summary><c>metadata.metrics</c> of a row: token values (METRICS 3.2) and finite, truncated times (3.6).</summary>
internal readonly record struct DevinMetrics(long Input, long Output, long CacheRead, long CacheCreation, long? TotalMs, long? TtftMs);

/// <summary>The parts of one CLI <c>message_nodes.chat_message</c> that METRICS 4.17 reads; never its content.</summary>
internal sealed record DevinMessage(
    DevinRole Role, bool HasToolCalls, bool FinishedWithToolCalls, string? RequestId, string? Model, DevinMetrics? Metrics)
{
    private const double TwoTo63 = 9_223_372_036_854_775_808d;

    /// <summary>The phase the row sets: the prompt or tool results start the next request; null for other roles.</summary>
    public AgentPhase? Phase => Role switch
    {
        DevinRole.User or DevinRole.Tool => AgentPhase.Generating,
        DevinRole.Assistant => HasToolCalls || FinishedWithToolCalls ? AgentPhase.Tools : AgentPhase.Idle,
        _ => null,
    };

    /// <summary>Null when the text is not exactly one JSON object (a malformed row; <c>NaN</c> included, METRICS 3.6).</summary>
    public static DevinMessage? Parse(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, JsonScan.Options);
        var role = DevinRole.Other;
        var tools = false;
        var metadata = Metadata.None;
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("role"u8))
                {
                    reader.Read();
                    role = ReadRole(ref reader);
                }
                else if (reader.ValueTextEquals("tool_calls"u8))
                {
                    reader.Read();
                    tools = NonEmptyArray(ref reader);
                }
                else if (reader.ValueTextEquals("metadata"u8))
                {
                    reader.Read();
                    metadata = ReadMetadata(ref reader);
                }
                else
                {
                    reader.Read();
                    JsonScan.SkipValue(ref reader);
                }
            }

            if (reader.Read())
            {
                return null;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }

        return new DevinMessage(role, tools, metadata.ToolCalls, metadata.RequestId, metadata.Model, metadata.Metrics);
    }

    private static DevinRole ReadRole(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            JsonScan.SkipValue(ref reader);
            return DevinRole.Other;
        }

        return reader.ValueTextEquals("assistant"u8) ? DevinRole.Assistant
            : reader.ValueTextEquals("user"u8) ? DevinRole.User
            : reader.ValueTextEquals("tool"u8) ? DevinRole.Tool
            : DevinRole.Other;
    }

    private static bool NonEmptyArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            JsonScan.SkipValue(ref reader);
            return false;
        }

        var any = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            any = true;
            JsonScan.SkipValue(ref reader);
        }

        return any;
    }

    /// <summary>A later <c>metadata</c> member replaces an earlier one whole, as a JSON object's last member wins.</summary>
    private static Metadata ReadMetadata(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            JsonScan.SkipValue(ref reader);
            return Metadata.None;
        }

        var metadata = Metadata.None;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("metrics"u8))
            {
                reader.Read();
                metadata = metadata with { Metrics = ReadMetrics(ref reader) };
            }
            else if (reader.ValueTextEquals("request_id"u8))
            {
                reader.Read();
                metadata = metadata with { RequestId = Trimmed(JsonScan.ReadToken(ref reader)) };
            }
            else if (reader.ValueTextEquals("generation_model"u8))
            {
                reader.Read();
                metadata = metadata with { Model = Trimmed(JsonScan.ReadToken(ref reader)) };
            }
            else if (reader.ValueTextEquals("finish_reason"u8))
            {
                reader.Read();
                var toolCalls = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("tool_calls"u8);
                JsonScan.SkipValue(ref reader);
                metadata = metadata with { ToolCalls = toolCalls };
            }
            else
            {
                reader.Read();
                JsonScan.SkipValue(ref reader);
            }
        }

        return metadata;
    }

    private static DevinMetrics? ReadMetrics(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            JsonScan.SkipValue(ref reader);
            return null;
        }

        long input = 0, output = 0, read = 0, creation = 0;
        long? total = null, ttft = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("input_tokens"u8))
            {
                reader.Read();
                input = JsonScan.ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("output_tokens"u8))
            {
                reader.Read();
                output = JsonScan.ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("cache_read_tokens"u8))
            {
                reader.Read();
                read = JsonScan.ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("cache_creation_tokens"u8))
            {
                reader.Read();
                creation = JsonScan.ReadCount(ref reader);
            }
            else if (reader.ValueTextEquals("total_time_ms"u8))
            {
                reader.Read();
                total = ReadFiniteMs(ref reader);
            }
            else if (reader.ValueTextEquals("ttft_ms"u8))
            {
                reader.Read();
                ttft = ReadFiniteMs(ref reader);
            }
            else
            {
                reader.Read();
                JsonScan.SkipValue(ref reader);
            }
        }

        return new DevinMetrics(input, output, read, creation, total, ttft);
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

    /// <summary>A string trimmed; null when blank.</summary>
    internal static string? Trimmed(string? value) => value?.Trim() is { Length: > 0 } text ? text : null;

    private readonly record struct Metadata(string? RequestId, string? Model, bool ToolCalls, DevinMetrics? Metrics)
    {
        public static Metadata None => default;
    }
}

/// <summary>One CLI call as METRICS 4.17 <i>Calls</i> defines it.</summary>
internal static class DevinCalls
{
    public static string Key(string session, string? requestId, long rowId) =>
        requestId is not null ? "devin:" + session + ":request:" + requestId
            : "devin:" + session + ":row:" + rowId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The session model: <c>sessions.model</c> trimmed, unless blank or <c>adaptive</c> (a routing mode, not a model).</summary>
    public static string? SessionModel(string? raw) =>
        DevinMessage.Trimmed(raw) is { } model && !model.Equals("adaptive", StringComparison.OrdinalIgnoreCase) ? model : null;

    /// <summary>The response of an assistant row with metrics; null when it has none or no tokens.</summary>
    public static ParsedResponse? Response(DevinMessage message, string session, long rowId, long endMs, string? sessionModel, string agentId)
    {
        if (message.Role != DevinRole.Assistant || message.Metrics is not { } metrics)
        {
            return null;
        }

        var input = JsonScan.SaturatingAdd(JsonScan.SaturatingAdd(metrics.Input, metrics.CacheRead), metrics.CacheCreation);
        if (input == 0 && metrics.Output == 0)
        {
            return null;
        }

        long? start = null, first = null;
        if (metrics.TotalMs is { } total && total > 0)
        {
            start = endMs < long.MinValue + total ? long.MinValue : endMs - total;
            if (metrics.TtftMs is { } ttft && ttft > 0 && ttft <= total)
            {
                first = start + ttft;
            }
        }

        var tokens = new TokenCounts(input, metrics.CacheRead, metrics.CacheCreation, metrics.Output);
        return new ParsedResponse(Key(session, message.RequestId, rowId), SourceKind.Devin, agentId, message.Model ?? sessionModel,
            start, endMs, tokens) { FirstMs = first };
    }
}
