using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace AgentSpeed.Core.Locations;

/// <summary>
/// The stored form (DESIGN 5, settings value <c>sourceLocations</c>):
/// <c>{"version":1,"lists":{"codex":[{"automatic":true},{"path":"C:\\work\\codex\\sessions"}]}}</c>. A folder whose list is
/// <c>[automatic]</c> is left out; the automatic entry is a marker, never a path.
/// </summary>
public sealed partial class LocationLists
{
    public const int DocumentVersion = 1;

    /// <summary>The document as compact JSON, lists in ordinal name order.</summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public void Write(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        writer.WriteNumber("version", DocumentVersion);
        writer.WriteStartObject("lists");
        var names = _lists.Keys.Select(folder => folder.Key()).Concat(_unknown.Keys).Order(StringComparer.Ordinal);
        foreach (var name in names)
        {
            writer.WritePropertyName(name);
            if (_unknown.TryGetValue(name, out var raw))
            {
                writer.WriteRawValue(raw, skipInputValidation: false);
                continue;
            }

            LocationFolders.TryParse(name, out var folder);
            writer.WriteStartArray();
            foreach (var entry in _lists[folder])
            {
                writer.WriteStartObject();
                if (entry.Path is { } path)
                {
                    writer.WriteString("path", path);
                }
                else
                {
                    writer.WriteBoolean("automatic", true);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Null when the value is not a JSON object. Tolerant otherwise: an entry that is neither <c>{"automatic":true}</c> nor
    /// <c>{"path":&lt;absolute path&gt;}</c> is skipped and each list is normalized; lists of folders this build does not know
    /// are kept as stored.
    /// </summary>
    public static LocationLists? Read(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var lists = Automatic;
        if (!document.TryGetProperty("lists", out var members) || members.ValueKind != JsonValueKind.Object)
        {
            return lists;
        }

        var unknown = lists._unknown;
        foreach (var member in members.EnumerateObject())
        {
            if (!LocationFolders.TryParse(member.Name, out var folder))
            {
                unknown = unknown.SetItem(member.Name, member.Value.GetRawText());
                continue;
            }

            if (member.Value.ValueKind == JsonValueKind.Array)
            {
                lists = lists.With(folder, member.Value.EnumerateArray().Select(Entry).OfType<DataLocation>());
            }
        }

        return new LocationLists(lists._lists, unknown);
    }

    /// <summary>Parses the stored text; null when it is not a JSON object.</summary>
    public static LocationLists? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DataLocation? Entry(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (value.TryGetProperty("automatic", out var automatic) && automatic.ValueKind == JsonValueKind.True)
        {
            return DataLocation.Automatic;
        }

        return value.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
            && LocationPaths.Standardized(path.GetString()) is { } standardized
            ? DataLocation.At(standardized)
            : null;
    }
}
