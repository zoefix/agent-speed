using System.Text;
using System.Text.Json;

namespace AgentSpeed.Core.Remote;

/// <summary>
/// The JSON form of <c>remoteHosts</c> (REMOTE 2.1). Reading never throws: a value that is not an object is the empty
/// document, a host without a valid id is skipped, and a member of the wrong type takes its default. Members this
/// version does not know are kept as their JSON text and written back.
/// </summary>
public static class RemoteHostsJson
{
    private static readonly string[] DocumentMembers = ["version", "hosts"];
    private static readonly string[] HostMembers = ["id", "destination", "name", "enabled", "relayFolder", "quotaClaudeCli"];

    public static void Write(Utf8JsonWriter writer, RemoteHostsDocument document)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(document);
        writer.WriteStartObject();
        WriteExtra(writer, document.ExtraJson, DocumentMembers);
        writer.WriteNumber("version", document.Version);
        writer.WriteStartArray("hosts");
        foreach (var host in document.Hosts)
        {
            writer.WriteStartObject();
            WriteExtra(writer, host.ExtraJson, HostMembers);
            writer.WriteString("id", host.Id);
            writer.WriteString("destination", host.Destination);
            WriteNullable(writer, "name", host.Name);
            writer.WriteBoolean("enabled", host.Enabled);
            WriteNullable(writer, "relayFolder", host.RelayFolder);
            if (host.QuotaClaudeCli)
            {
                writer.WriteBoolean("quotaClaudeCli", true);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static RemoteHostsDocument Read(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return RemoteHostsDocument.Empty;
        }

        var hosts = new List<RemoteHost>();
        if (element.TryGetProperty("hosts", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (ReadHost(item) is { } host)
                {
                    hosts.Add(host);
                }
            }
        }

        var version = element.TryGetProperty("version", out var v) && v.TryGetInt32(out var number) ? number : RemoteHostsDocument.CurrentVersion;
        return new RemoteHostsDocument(hosts, version, Extra(element, DocumentMembers));
    }

    public static string ToJson(RemoteHostsDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, document);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The document of <paramref name="json"/>; the empty document when it is not JSON.</summary>
    public static RemoteHostsDocument Parse(string json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json);
            return Read(parsed.RootElement);
        }
        catch (JsonException)
        {
            return RemoteHostsDocument.Empty;
        }
    }

    private static RemoteHost? ReadHost(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || String(item, "id") is not { } id || !RemoteHostValidation.IsValidId(id))
        {
            return null;
        }

        return new RemoteHost(String(item, "destination") ?? string.Empty, id)
        {
            Name = String(item, "name"),
            Enabled = !item.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False,
            RelayFolder = String(item, "relayFolder"),
            QuotaClaudeCli = item.TryGetProperty("quotaClaudeCli", out var bridge) && bridge.ValueKind == JsonValueKind.True,
            ExtraJson = Extra(item, HostMembers),
        };
    }

    private static string? String(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
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

    /// <summary>The members not in <paramref name="known"/> as one compact object text, in their order (the last of a repeated name wins); null when there are none.</summary>
    private static string? Extra(JsonElement element, string[] known)
    {
        var members = new List<JsonProperty>();
        foreach (var member in element.EnumerateObject())
        {
            if (known.Contains(member.Name))
            {
                continue;
            }

            members.RemoveAll(m => m.NameEquals(member.Name));
            members.Add(member);
        }

        if (members.Count == 0)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var member in members)
            {
                member.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteExtra(Utf8JsonWriter writer, string? extraJson, string[] known)
    {
        if (extraJson is null)
        {
            return;
        }

        try
        {
            using var extra = JsonDocument.Parse(extraJson);
            if (extra.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var member in extra.RootElement.EnumerateObject())
            {
                if (!known.Contains(member.Name))
                {
                    member.WriteTo(writer);
                }
            }
        }
        catch (JsonException)
        {
        }
    }
}
