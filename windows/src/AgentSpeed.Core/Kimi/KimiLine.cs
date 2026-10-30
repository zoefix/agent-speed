using System.Text.Json;
using AgentSpeed.Core.Harnesses;
using AgentSpeed.Core.Parsing;
using static AgentSpeed.Core.Harnesses.HarnessJson;

namespace AgentSpeed.Core.Kimi;

internal struct KimiLine
{
    public LineStatus Status;
    public string? Type;
    public string? Agent;
    public long? TimeMs;
    public string? Model;
    public string? Cwd;
    public string? Provider;
    public string? Role;
    public string? ResponseId;
    public TokenCounts? Usage;
    public long? DurationMs;
    public long? FirstTokenMs;
    public long? StreamMs;
    public bool ToolCalls;
    public bool Partial;

    /// <summary>The line's own <c>model</c> member, not the alias: an exact id may name a variant.</summary>
    public bool HasModelMember;
    public string? ModelMember;

    /// <summary>A message's <c>meta.model.model</c>.</summary>
    public bool HasMetaModel;
    public string? MetaModel;

    /// <summary><c>thinkingEffort</c>, and the legacy <c>thinkingLevel</c>, when they are non-empty strings.</summary>
    public string? ThinkingEffort;
    public string? ThinkingLevel;

    public static KimiLine Read(ReadOnlySpan<byte> line)
    {
        if (JsonScan.IsBlank(line))
        {
            return new KimiLine { Status = LineStatus.Blank };
        }

        try
        {
            var reader = new Utf8JsonReader(line, JsonScan.Options);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new KimiLine { Status = LineStatus.Malformed };
            }

            var type = String(root, "type");
            var result = new KimiLine
            {
                Status = LineStatus.Valid, Type = type, Agent = String(root, "agentId"),
                TimeMs = Timestamp(Get(root, "time")) ?? Timestamp(Get(root, "timestamp")),
                Cwd = String(Get(root, "environmentDisclosure"), "cwd"),
                Model = String(root, "modelAlias") ?? String(root, "model"), Provider = String(root, "provider"),
                ThinkingEffort = String(root, "thinkingEffort"), ThinkingLevel = String(root, "thinkingLevel"),
            };
            if (root.TryGetProperty("model", out var modelMember))
            {
                result.HasModelMember = true;
                result.ModelMember = modelMember.ValueKind == JsonValueKind.String ? modelMember.GetString() : null;
            }

            var message = Get(root, "message");
            var meta = Get(message, "meta");
            if (Get(message, "message").ValueKind == JsonValueKind.Object)
            {
                message = Get(message, "message");
            }

            if (type == "context.append_loop_event")
            {
                var ev = Get(root, "event");
                result.Type = String(ev, "type");
                result.ResponseId = String(ev, "messageId");
                result.FirstTokenMs = Milliseconds(Get(ev, "llmFirstTokenLatencyMs"));
                result.StreamMs = Milliseconds(Get(ev, "llmStreamDurationMs"));
                result.ToolCalls = result.Type == "tool.call" || String(ev, "finishReason") is "tool_use" or "tool_calls";
                var usage = Get(ev, "usage");
                if (usage.ValueKind == JsonValueKind.Object)
                {
                    result.Usage = KimiUsage(usage);
                }
            }
            else if (type is "context.append_message" or "agent.message.appended" or "human.agent.message.appended")
            {
                result.Role = String(message, "role");
                result.Partial = Get(message, "partial").ValueKind == JsonValueKind.True;
                result.ResponseId = String(message, "providerMessageId") ?? String(message, "id") ?? String(meta, "messageId");
                var usage = Get(message, "usage");
                if (usage.ValueKind != JsonValueKind.Object)
                {
                    usage = Get(meta, "usage");
                }

                if (usage.ValueKind == JsonValueKind.Object)
                {
                    result.Usage = KimiUsage(usage);
                }

                var timing = Get(message, "llmTiming");
                if (timing.ValueKind != JsonValueKind.Object)
                {
                    timing = Get(meta, "llmTiming");
                }

                result.FirstTokenMs = Milliseconds(Get(timing, "llmFirstTokenLatencyMs"));
                result.StreamMs = Milliseconds(Get(timing, "llmStreamDurationMs"));
                result.DurationMs = Milliseconds(Get(message, "durationMs")) ?? Milliseconds(Get(meta, "durationMs"));
                result.Model = String(Get(meta, "model"), "model") ?? String(message, "model") ?? result.Model;
                result.Provider = String(Get(meta, "model"), "provider") ?? result.Provider;
                if (Get(meta, "model") is { ValueKind: JsonValueKind.Object } metaModel && metaModel.TryGetProperty("model", out var metaId))
                {
                    result.HasMetaModel = true;
                    result.MetaModel = metaId.ValueKind == JsonValueKind.String ? metaId.GetString() : null;
                }

                var calls = Get(message, "toolCalls");
                result.ToolCalls = calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0;
            }

            return result;
        }
        catch (JsonException)
        {
            return new KimiLine { Status = LineStatus.Malformed };
        }
    }
}
