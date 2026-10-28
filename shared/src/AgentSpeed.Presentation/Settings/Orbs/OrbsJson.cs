using System.Collections.Immutable;
using System.Text.Json;
using AgentSpeed.Presentation.Orb;

namespace AgentSpeed.Presentation.Settings.Orbs;

/// <summary>
/// The settings value <c>orbs</c> (DESIGN 2b, Data model). Reading is tolerant: an invalid field takes its default,
/// an invalid orb or position is skipped, and sizes are bounded, so a hand-edited or newer file never fails to load.
/// </summary>
public static class OrbsJson
{
    /// <summary>Elements of the <c>orbs</c> array looked at; more are ignored.</summary>
    public const int MaxOrbElementsRead = 64;

    /// <summary>Arrangements of <c>layouts</c> looked at; more are ignored.</summary>
    public const int MaxLayoutElementsRead = 64;

    public static void Write(Utf8JsonWriter writer, OrbsDocument document)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(document);
        writer.WriteStartObject();
        writer.WriteNumber("version", OrbsDocument.CurrentVersion);
        writer.WriteStartArray("orbs");
        foreach (var orb in document.Orbs)
        {
            WriteOrb(writer, orb);
        }

        writer.WriteEndArray();
        writer.WriteStartObject("layouts");
        foreach (var layout in document.Layouts)
        {
            writer.WriteStartObject(layout.Arrangement);
            foreach (var (id, record) in layout.Positions.OrderBy(p => document.IndexOf(p.Key)))
            {
                writer.WriteStartObject(id);
                writer.WriteString("display", record.Display);
                writer.WriteString("xAnchor", OrbAxis.KeyOf(record.X.Anchor));
                writer.WriteNumber("x", record.X.Value);
                writer.WriteString("yAnchor", OrbAxis.KeyOf(record.Y.Anchor));
                writer.WriteNumber("y", record.Y.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>The document, or null when the value is not a document or has no usable orb (the caller migrates).</summary>
    public static OrbsDocument? Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("orbs", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var orbs = new List<OrbDefinition>(OrbsDocument.MaxOrbs);
        foreach (var element in list.EnumerateArray().Take(MaxOrbElementsRead))
        {
            if (orbs.Count >= OrbsDocument.MaxOrbs)
            {
                break;
            }

            if (ReadOrb(element) is { } orb)
            {
                orbs.Add(orb);
            }
        }

        if (orbs.Count == 0)
        {
            return null;
        }

        var cleaned = new OrbsDocument(orbs).Validated();
        var layouts = value.TryGetProperty("layouts", out var raw) ? ReadLayouts(raw, cleaned) : [];
        return (cleaned with { Layouts = layouts }).Validated();
    }

    private static void WriteOrb(Utf8JsonWriter writer, OrbDefinition orb)
    {
        writer.WriteStartObject();
        writer.WriteString("id", orb.Id);
        WriteNullable(writer, "name", orb.Name);
        if (orb.Sources.ToKeys() is { } keys)
        {
            writer.WriteStartArray("sources");
            foreach (var key in keys)
            {
                writer.WriteStringValue(key);
            }

            writer.WriteEndArray();
        }
        else
        {
            writer.WriteString("sources", "all");
        }

        writer.WriteString("style", OrbStyles.Key(orb.Style));
        writer.WriteNumber("size", orb.Size);
        writer.WriteNumber("opacity", orb.Opacity);
        writer.WriteBoolean("alwaysOnTop", orb.AlwaysOnTop);
        writer.WriteBoolean("clickThrough", orb.ClickThrough);
        writer.WriteBoolean("fadeWhenIdle", orb.FadeWhenIdle);
        writer.WriteBoolean("allSpaces", orb.AllSpaces);
        writer.WriteBoolean("visible", orb.Visible);
        WriteNullable(writer, "redlineOutput", orb.RedlineOutput);
        WriteNullable(writer, "redlineTotal", orb.RedlineTotal);
        WriteNullable(writer, "showName", orb.ShowName);
        if (orb.TitleTheme is { } theme)
        {
            writer.WriteString("titleTheme", theme);
        }
        else
        {
            writer.WriteNull("titleTheme");
        }

        writer.WriteString("unit", OrbUnits.Key(orb.Unit));
        writer.WriteString("traffic", OrbTraffics.Key(orb.Traffic));
        writer.WriteEndObject();
    }

    private static OrbDefinition? ReadOrb(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var style = OrbStyles.Parse(ReadString(element, "style", 32));
        return new OrbDefinition(OrbDefinition.CleanId(ReadString(element, "id", OrbDefinition.MaxIdLength)) ?? OrbDefinition.NewId())
        {
            Name = ReadString(element, "name", 1024),
            Sources = ReadSources(element),
            Style = style,
            Size = ReadNumber(element, "size") ?? OrbDefinition.DefaultSize,
            Opacity = ReadNumber(element, "opacity") ?? OrbDefinition.MaxOpacity,
            AlwaysOnTop = ReadBool(element, "alwaysOnTop", true),
            ClickThrough = ReadBool(element, "clickThrough", false),
            FadeWhenIdle = ReadBool(element, "fadeWhenIdle", false),
            AllSpaces = ReadBool(element, "allSpaces", true),
            Visible = ReadBool(element, "visible", true),
            RedlineOutput = ReadNumber(element, "redlineOutput"),
            RedlineTotal = ReadNumber(element, "redlineTotal"),
            ShowName = ReadNullableBool(element, "showName"),
            TitleTheme = ReadString(element, "titleTheme", OrbDefinition.MaxIdLength),
            Unit = OrbUnits.Parse(ReadString(element, "unit", 32)),
            Traffic = OrbTraffics.Parse(ReadString(element, "traffic", 32)),
        }.Validated();
    }

    private static OrbSources ReadSources(JsonElement element)
    {
        if (!element.TryGetProperty("sources", out var sources))
        {
            return OrbSources.All;
        }

        return sources.ValueKind switch
        {
            JsonValueKind.String => sources.GetString() is "all" or null ? OrbSources.All : OrbSources.FromKeys([sources.GetString()]),
            JsonValueKind.Array => OrbSources.FromKeys(sources.EnumerateArray()
                .Take(OrbSources.MaxKeysRead)
                .Select(k => k.ValueKind == JsonValueKind.String ? k.GetString() : null)),
            _ => OrbSources.All,
        };
    }

    private static ImmutableArray<OrbLayout> ReadLayouts(JsonElement layouts, OrbsDocument document)
    {
        if (layouts.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<OrbLayout>(OrbsDocument.MaxArrangements);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arrangement in layouts.EnumerateObject().Take(MaxLayoutElementsRead))
        {
            if (result.Count >= OrbsDocument.MaxArrangements)
            {
                break;
            }

            if (arrangement.Value.ValueKind != JsonValueKind.Object
                || !OrbsDocument.IsValidArrangement(arrangement.Name)
                || seen.Contains(arrangement.Name))
            {
                continue;
            }

            var positions = ImmutableDictionary.CreateBuilder<string, OrbPositionRecord>(StringComparer.Ordinal);
            foreach (var entry in arrangement.Value.EnumerateObject().Take(MaxOrbElementsRead))
            {
                if (positions.Count < OrbsDocument.MaxOrbs
                    && document.Find(entry.Name) is not null
                    && !positions.ContainsKey(entry.Name)
                    && ReadRecord(entry.Value) is { } record)
                {
                    positions[entry.Name] = record;
                }
            }

            if (positions.Count > 0)
            {
                seen.Add(arrangement.Name);
                result.Add(new OrbLayout(arrangement.Name, positions.ToImmutable()));
            }
        }

        return result.ToImmutable();
    }

    private static OrbPositionRecord? ReadRecord(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || ReadString(element, "display", OrbPositionRecord.MaxDisplayKeyLength) is not { } display
            || OrbAxis.ParseAnchor(ReadString(element, "xAnchor", 16)) is not { } xAnchor
            || OrbAxis.ParseAnchor(ReadString(element, "yAnchor", 16)) is not { } yAnchor
            || ReadNumber(element, "x") is not { } x
            || ReadNumber(element, "y") is not { } y)
        {
            return null;
        }

        return new OrbPositionRecord(display, new OrbAxis(xAnchor, x), new OrbAxis(yAnchor, y)).Validated();
    }

    private static string? ReadString(JsonElement element, string name, int maxLength) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text && text.Length <= maxLength
            ? text
            : null;

    private static double? ReadNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : null;

    private static bool ReadBool(JsonElement element, string name, bool fallback) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            }
            : fallback;

    /// <summary>True or false; absent, null or any other value is null.</summary>
    private static bool? ReadNullableBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static void WriteNullable(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is { } flag)
        {
            writer.WriteBoolean(name, flag);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

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

    private static void WriteNullable(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
