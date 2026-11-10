using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentSpeed.Core.Quota;

/// <summary>One stored window of the Claude CLI quota file: the percent and the reset in epoch seconds, as Claude Code gave them.</summary>
internal readonly record struct CliWindowValue(double UsedPercentage, double ResetsAt)
{
    /// <summary>The reset in Unix ms (truncated).</summary>
    public long ResetsAtMs => (long)Math.Truncate(ResetsAt * 1000);
}

/// <summary>The quota file <c>&lt;data&gt;/quota/claude-cli.json</c> (QUOTAS 5.5): one value per window and when one last changed.</summary>
internal sealed record CliQuotaState(long? ReceivedMs, CliWindowValue? FiveHour, CliWindowValue? SevenDay)
{
    public static CliQuotaState Empty { get; } = new(null, null, null);
}

/// <summary>
/// The status line helper's logic (QUOTAS 5.5), shared by the helper and the app: the merge of Claude Code's
/// <c>rate_limits</c> into the quota file, the file's format, the fallback line and the previous command of the sidecar.
/// Reflection-free, so the NativeAOT helper can compile it.
/// </summary>
internal static class ClaudeCliQuota
{
    public const string HelperName = "agent-speed-statusline";
    public const string QuotaFolder = "quota";
    public const string QuotaFileName = "claude-cli.json";
    public const string SidecarFileName = "claude-statusline-previous.json";
    public const string BackupFileName = "claude-settings.backup.json";
    public const int InputLimit = 1 << 20;

    /// <summary>Two values whose resets are this close belong to the same window.</summary>
    public const long SameWindowMs = 60_000;

    private static readonly JsonDocumentOptions Tolerant = new() { MaxDepth = 64 };

    /// <summary>Some shells pipe text with a UTF-8 byte order mark (Windows PowerShell); JSON is parsed without it.</summary>
    public static ReadOnlySpan<byte> WithoutBom(ReadOnlySpan<byte> json) => json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? json[3..] : json;

    /// <summary>Reads a quota file; anything unreadable is an empty state.</summary>
    public static CliQuotaState Read(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(WithoutBom(json).ToArray(), Tolerant);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number || version.GetDouble() != 1)
            {
                return CliQuotaState.Empty;
            }

            long? received = root.TryGetProperty("receivedMs", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var ms) ? ms : null;
            CliWindowValue? five = null, seven = null;
            if (root.TryGetProperty("windows", out var windows) && windows.ValueKind == JsonValueKind.Object)
            {
                five = Window(windows, "five_hour", "used_percentage");
                seven = Window(windows, "seven_day", "used_percentage");
            }

            return new CliQuotaState(received, five, seven);
        }
        catch (JsonException)
        {
            return CliQuotaState.Empty;
        }
    }

    public static byte[] Write(CliQuotaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            if (state.ReceivedMs is { } received)
            {
                w.WriteNumber("receivedMs", received);
            }
            else
            {
                w.WriteNull("receivedMs");
            }

            w.WriteStartObject("windows");
            WriteWindow(w, "five_hour", state.FiveHour);
            WriteWindow(w, "seven_day", state.SevenDay);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static void WriteWindow(Utf8JsonWriter w, string name, CliWindowValue? value)
    {
        if (value is not { } v)
        {
            return;
        }

        w.WriteStartObject(name);
        w.WriteNumber("used_percentage", v.UsedPercentage);
        w.WriteNumber("resets_at", v.ResetsAt);
        w.WriteEndObject();
    }

    /// <summary>Merges the status line input's <c>rate_limits</c>; <paramref name="changed"/> tells whether the file must be written.</summary>
    public static CliQuotaState Merge(CliQuotaState stored, ReadOnlySpan<byte> input, long nowMs, out bool changed)
    {
        changed = false;
        try
        {
            using var document = JsonDocument.Parse(WithoutBom(input).ToArray(), Tolerant);
            return Merge(stored, document.RootElement, nowMs, out changed);
        }
        catch (JsonException)
        {
            return stored;
        }
    }

    /// <summary>
    /// For each window with a numeric percent and reset: a reset more than 60 s later is a new window and replaces the
    /// stored value; within 60 s it is the same window and the higher percent wins; an earlier one is ignored. A stored
    /// window missing from the input is kept.
    /// </summary>
    public static CliQuotaState Merge(CliQuotaState stored, JsonElement input, long nowMs, out bool changed)
    {
        ArgumentNullException.ThrowIfNull(stored);
        changed = false;
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
        {
            return stored;
        }

        var five = Pick(stored.FiveHour, Window(limits, "five_hour", "used_percentage"), ref changed);
        var seven = Pick(stored.SevenDay, Window(limits, "seven_day", "used_percentage"), ref changed);
        return changed ? new CliQuotaState(nowMs, five, seven) : stored;
    }

    private static CliWindowValue? Pick(CliWindowValue? old, CliWindowValue? next, ref bool changed)
    {
        if (next is not { } n)
        {
            return old;
        }

        CliWindowValue take;
        if (old is not { } o || n.ResetsAt * 1000 - o.ResetsAt * 1000 > SameWindowMs)
        {
            take = n;
        }
        else if (Math.Abs(n.ResetsAt - o.ResetsAt) * 1000 <= SameWindowMs)
        {
            take = n.UsedPercentage > o.UsedPercentage ? n : o;
        }
        else
        {
            take = o;
        }

        if (old is not { } kept || take != kept)
        {
            changed = true;
        }

        return take;
    }

    private static CliWindowValue? Window(JsonElement parent, string name, string percentName) =>
        parent.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Object
        && Number(w, percentName) is { } used && Number(w, "resets_at") is { } resets
            ? new CliWindowValue(used, resets)
            : null;

    private static double? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsFinite(d)
            ? d
            : null;

    /// <summary>The stored windows that have not reset, <c>5h 6% · 7d 86%</c>; empty when there is none.</summary>
    public static string Line(CliQuotaState state, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(state);
        var line = new StringBuilder();
        Append(line, "5h", state.FiveHour, nowMs);
        Append(line, "7d", state.SevenDay, nowMs);
        return line.ToString();
    }

    private static void Append(StringBuilder line, string label, CliWindowValue? value, long nowMs)
    {
        if (value is not { } v || nowMs >= v.ResetsAtMs)
        {
            return;
        }

        if (line.Length > 0)
        {
            line.Append(" · ");
        }

        var percent = (int)Math.Clamp(Math.Floor(v.UsedPercentage + 0.5), 0, 100);
        line.Append(label).Append(' ').Append(percent.ToString(CultureInfo.InvariantCulture)).Append('%');
    }

    /// <summary>The sidecar's previous status line command: a <c>command</c> object with a non-empty command, else null.</summary>
    public static string? PreviousCommand(ReadOnlySpan<byte> sidecar)
    {
        try
        {
            using var document = JsonDocument.Parse(WithoutBom(sidecar).ToArray(), Tolerant);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("statusLine", out var line) && line.ValueKind == JsonValueKind.Object
                && line.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.ValueEquals("command")
                && line.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String
                && command.GetString() is { Length: > 0 } text
                    ? text
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
