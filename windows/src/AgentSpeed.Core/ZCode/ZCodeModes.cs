using System.Text.Json;

namespace AgentSpeed.Core.ZCode;

/// <summary>The facts of one <c>model_usage</c> row (EXECUTION-MODES, ZCode), from the row and the user message it answers.</summary>
internal static class ZCodeModes
{
    private const string MockCardPrefix = "hsc_mock_";

    /// <param name="message">The JSON of the row's <c>parent_user_message_id</c>; empty when there is none.</param>
    /// <param name="variant">The <c>variant</c> column: the reasoning level the request sent.</param>
    /// <param name="provider">The <c>provider_id</c> column.</param>
    /// <param name="model">The <c>model_id</c> column.</param>
    /// <param name="startedMs">The <c>started_at</c> column.</param>
    public static ExecutionModes Read(ReadOnlySpan<byte> message, string? variant, string? provider, string? model, long? startedMs)
    {
        var facts = ExecutionModes.Of(ExecutionModeNative.Effort(variant, ExecutionModeEvidence.Recorded, ExecutionModeScope.Request))
            .Merge(ExecutionModeNative.ZCodeModel(model));
        var routed = ExecutionModeNative.ZCodeProvider(provider);
        if (Card(message) is not { } card)
        {
            return routed.IsEmpty ? facts : facts.Merge(routed);
        }

        facts = facts.With(new(ExecutionModeKind.Accelerator, "highspeed", "highspeed", ExecutionModeEvidence.Selected, "zcode", ExecutionModeScope.Turn));
        if (card.Mock)
        {
            return facts;
        }

        return !routed.IsEmpty ? facts.Merge(routed)
            : card.FallbackAtMs is { } fallback && startedMs >= fallback
                ? facts.With(new(ExecutionModeKind.Accelerator, "standard", "fallback", ExecutionModeEvidence.Recorded, "zcode", ExecutionModeScope.Request))
                : facts;
    }

    private static (bool Mock, long? FallbackAtMs)? Card(ReadOnlySpan<byte> message)
    {
        if (message.IsEmpty) return null;
        try
        {
            using var document = JsonDocument.Parse(message.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String
                || role.GetString() != "user" || !root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object
                || !metadata.TryGetProperty("highspeed", out var highspeed) || highspeed.ValueKind != JsonValueKind.Object)
                return null;
            if (!highspeed.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetDouble(out var version) || version != 1
                || !highspeed.TryGetProperty("cardId", out var card) || card.ValueKind != JsonValueKind.String || card.GetString() is not { Length: > 0 } cardId
                || !highspeed.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String || model.GetString() is not { Length: > 0 })
                return null;
            long? fallback = highspeed.TryGetProperty("fallbackAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var ms) ? ms : null;
            return (cardId.StartsWith(MockCardPrefix, StringComparison.Ordinal), fallback);
        }
        catch (JsonException) { return null; }
    }
}
