using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace AgentSpeed.Core.Lan;

/// <summary>
/// The JSON form of the settings value <c>lan</c> (LAN 2.1). Reading never throws: another version reads as the defaults,
/// an invalid member takes its default, and a target with an unknown kind, an invalid address or a duplicate id is left out.
/// </summary>
public static class LanSettingsJson
{
    public static LanSettings Read(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetDouble(out var number) || number != LanSettings.Version)
        {
            return LanSettings.Default;
        }

        var instance = String(element, "instance") is { } id && LanSettings.IsUuid(id) ? id : null;
        var server = new LanServerSettings();
        if (element.TryGetProperty("server", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            server = new LanServerSettings(
                Bool(s, "enabled") ?? false,
                s.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var port) && LanSettings.IsValidPort(port)
                    ? port
                    : LanSettings.DefaultPort,
                String(s, "key") is { } key && LanKey.IsValid(key) ? key : null);
        }

        var targets = ImmutableArray.CreateBuilder<LanTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (element.TryGetProperty("targets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (targets.Count == LanSettings.MaxTargets)
                {
                    break;
                }

                if (ReadTarget(item) is { } target && seen.Add(target.Id))
                {
                    targets.Add(target);
                }
            }
        }

        return new LanSettings(instance, server, targets.ToImmutable());
    }

    public static LanSettings Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return LanSettings.Default;
        }
    }

    public static void Write(Utf8JsonWriter writer, LanSettings settings)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(settings);
        writer.WriteStartObject();
        writer.WriteNumber("version", LanSettings.Version);
        if (settings.Instance is { } instance)
        {
            writer.WriteString("instance", instance);
        }
        else
        {
            writer.WriteNull("instance");
        }

        writer.WriteStartObject("server");
        writer.WriteBoolean("enabled", settings.Server.Enabled);
        writer.WriteNumber("port", settings.Server.Port);
        Nullable(writer, "key", settings.Server.Key);
        writer.WriteEndObject();
        writer.WriteStartArray("targets");
        foreach (var target in settings.Targets)
        {
            writer.WriteStartObject();
            writer.WriteString("id", target.Id);
            writer.WriteString("name", target.Name);
            writer.WriteString("kind", target.Kind == LanTargetKind.Udp ? "udp" : "http");
            writer.WriteString("address", target.Address);
            Nullable(writer, "key", target.Key);
            writer.WriteBoolean("enabled", target.Enabled);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static string ToJson(LanSettings settings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, settings);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static LanTarget? ReadTarget(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || String(item, "id") is not { } id || !LanSettings.IsUuid(id)
            || String(item, "address") is not { } address)
        {
            return null;
        }

        LanTargetKind kind;
        switch (String(item, "kind"))
        {
            case "udp":
                kind = LanTargetKind.Udp;
                break;
            case "http":
                kind = LanTargetKind.Http;
                break;
            default:
                return null;
        }

        address = LanText.Trim(address);
        if (LanTargetAddress.Parse(kind, address) is null)
        {
            return null;
        }

        var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? LanText.Field(n.GetString(), 0, LanText.NameLimit) : null;
        string? key = null;
        if (item.TryGetProperty("key", out var k) && k.ValueKind != JsonValueKind.Null)
        {
            key = k.ValueKind == JsonValueKind.String ? LanText.Field(k.GetString(), 1, LanTarget.KeyLimit) : null;
        }

        return new LanTarget(id, name ?? string.Empty, kind, address, key, Bool(item, "enabled") ?? true);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static void Nullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}
