using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentSpeed.Core.Quota;

/// <summary>The bridge's state as the settings file shows it (QUOTAS 5.4).</summary>
public enum ClaudeCliBridgeStatus
{
    Off,
    Waiting,
    Ok,
    Replaced,
    Unreadable,
    Failed,
}

/// <summary>Turning the bridge on: the new settings text (null leaves the file alone) and the sidecar to keep.</summary>
internal sealed record ClaudeCliOn(ClaudeCliBridgeStatus Status, string? Settings, JsonObject? Previous);

/// <summary>
/// The edits of Claude Code's settings file (QUOTAS 5.2–5.4), as pure text transforms: members keep their order,
/// <c>statusLine</c> is replaced where it was or appended last, two-space indentation and a final newline.
/// </summary>
internal static class ClaudeCliSettings
{
    private static readonly JsonWriterOptions Writer = new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 256 };

    /// <summary>macOS and Linux: the path in single quotes, <c>'</c> written as <c>'\''</c>.</summary>
    public static string PosixCommand(string path) => "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>Windows: the path with <c>/</c> separators in double quotes.</summary>
    public static string WindowsCommand(string path) => "\"" + path.Replace('\\', '/') + "\"";

    /// <summary>A <c>statusLine</c> object of type <c>command</c> whose command contains <c>agent-speed-statusline</c>.</summary>
    public static bool IsOurs(JsonNode? value) =>
        value is JsonObject line && Text(line["type"]) == "command" && Text(line["command"]) is { } command
        && command.Contains(ClaudeCliQuota.HelperName, StringComparison.Ordinal);

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>Parses a settings text (null = no file = <c>{}</c>); null when it is not a JSON object.</summary>
    public static JsonObject? Parse(string? text)
    {
        if (text is null)
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(text, NodeOptions, DocumentOptions) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public static string Serialize(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, Writer))
        {
            node.WriteTo(w);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>
    /// 5.2: unless the current <c>statusLine</c> is ours, the sidecar keeps it (or none); the new value runs the helper
    /// and keeps a previous object's numeric <c>padding</c>.
    /// </summary>
    public static ClaudeCliOn On(string? text, string command, JsonObject? previousSidecar)
    {
        if (Parse(text) is not { } current)
        {
            return new ClaudeCliOn(ClaudeCliBridgeStatus.Unreadable, null, previousSidecar);
        }

        JsonObject? previous;
        if (current.TryGetPropertyValue("statusLine", out var existing) && IsOurs(existing))
        {
            previous = previousSidecar;
        }
        else
        {
            previous = new JsonObject { ["version"] = 1 };
            if (current.ContainsKey("statusLine"))
            {
                previous["statusLine"] = existing?.DeepClone();
            }
        }

        var line = new JsonObject { ["type"] = "command", ["command"] = command };
        if (previous?["statusLine"] is JsonObject old && old["padding"] is JsonValue padding && padding.GetValueKind() == JsonValueKind.Number)
        {
            line["padding"] = padding.DeepClone();
        }

        current["statusLine"] = line;
        return new ClaudeCliOn(ClaudeCliBridgeStatus.Ok, Serialize(current), previous);
    }

    /// <summary>5.3: while the value is still ours, the previous one goes back (or the member goes); null leaves the file alone.</summary>
    public static string? Off(string? text, JsonObject? previous)
    {
        if (text is null || Parse(text) is not { } current || !current.TryGetPropertyValue("statusLine", out var line) || !IsOurs(line))
        {
            return null;
        }

        if (previous is not null && previous.TryGetPropertyValue("statusLine", out var old))
        {
            current["statusLine"] = old?.DeepClone();
        }
        else
        {
            current.Remove("statusLine");
        }

        return Serialize(current);
    }

    /// <summary>5.4: ours with or without a quota file, replaced, or unreadable.</summary>
    public static ClaudeCliBridgeStatus Status(string? text, bool hasQuotaFile)
    {
        if (Parse(text) is not { } current)
        {
            return ClaudeCliBridgeStatus.Unreadable;
        }

        if (!current.TryGetPropertyValue("statusLine", out var line) || !IsOurs(line))
        {
            return ClaudeCliBridgeStatus.Replaced;
        }

        return hasQuotaFile ? ClaudeCliBridgeStatus.Ok : ClaudeCliBridgeStatus.Waiting;
    }

    /// <summary>The sidecar file's text.</summary>
    public static string SidecarText(JsonObject previous) => Serialize(previous);

    public static JsonObject? ReadSidecar(string? text) => text is null ? null : Parse(text);
}
