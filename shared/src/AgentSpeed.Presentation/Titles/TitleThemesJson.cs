using System.Text.Json;

namespace AgentSpeed.Presentation.Titles;

/// <summary>
/// The settings value <c>titleThemes</c> (DESIGN 1, Persistence). Reading is tolerant: an invalid custom set is skipped,
/// an invalid field takes its default, and stored text is re-normalized by the text rules (not the length cut).
/// </summary>
public static class TitleThemesJson
{
    /// <summary>Elements of the <c>custom</c> array looked at; more are ignored.</summary>
    public const int MaxElementsRead = 64;

    private const int MaxTextRead = 4096;

    public static void Write(Utf8JsonWriter writer, TitleThemesDocument document)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(document);
        writer.WriteStartObject();
        writer.WriteNumber("version", TitleThemesDocument.CurrentVersion);
        writer.WriteString("selected", document.Selected);
        writer.WriteStartArray("custom");
        foreach (var set in document.Custom)
        {
            writer.WriteStartObject();
            writer.WriteString("id", set.Id);
            writer.WriteString("name", set.Name);
            writer.WriteString("base", TitlePresets.Id(set.Base));
            writer.WriteString("idle", set.Idle);
            writer.WriteStartArray("titles");
            foreach (var title in set.Titles)
            {
                writer.WriteStringValue(title);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>The document, or null when the value is not an object (the caller uses the default: Vehicles, no sets).</summary>
    public static TitleThemesDocument? Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var sets = new List<CustomTitleSet>(TitleThemesDocument.MaxCustom);
        if (value.TryGetProperty("custom", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in list.EnumerateArray().Take(MaxElementsRead))
            {
                if (sets.Count >= TitleThemesDocument.MaxCustom)
                {
                    break;
                }

                if (ReadSet(element) is { } set && !sets.Any(s => s.Id == set.Id))
                {
                    sets.Add(set);
                }
            }
        }

        return new TitleThemesDocument
        {
            Selected = ReadString(value, "selected") ?? TitlePresets.Id(TitlePreset.Vehicles),
            Custom = [.. sets],
        }.Validated();
    }

    private static CustomTitleSet? ReadSet(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || CustomTitleSet.CleanId(ReadString(element, "id")) is not { } id)
        {
            return null;
        }

        var titles = new List<string?>(TitlePresets.LevelCount);
        if (element.TryGetProperty("titles", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray().Take(TitlePresets.LevelCount))
            {
                titles.Add(item.ValueKind == JsonValueKind.String && item.GetString() is { Length: <= MaxTextRead } text ? text : null);
            }
        }

        return new CustomTitleSet(id, ReadString(element, "name") ?? string.Empty,
            TitlePresets.Parse(ReadString(element, "base")) ?? TitlePreset.Vehicles, ReadString(element, "idle"), titles);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: <= MaxTextRead } text
            ? text
            : null;
}
