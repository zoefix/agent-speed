using System.Collections.Immutable;
using System.Text.Json;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Settings.Boards;

/// <summary>
/// The settings value <c>boards</c> (DESIGN 2c, Data model). Reading is tolerant like <see cref="OrbsJson"/>: an invalid
/// field takes its default, an invalid board or position is skipped, and sizes are bounded.
/// </summary>
public static class BoardsJson
{
    public static void Write(Utf8JsonWriter writer, BoardsDocument document)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(document);
        writer.WriteStartObject();
        writer.WriteNumber("version", BoardsDocument.CurrentVersion);
        writer.WriteStartArray("boards");
        foreach (var board in document.Boards)
        {
            writer.WriteStartObject();
            writer.WriteString("id", board.Id);
            writer.WriteString("style", BoardStyles.Key(board.Style));
            writer.WriteString("size", BoardSizes.Key(board.Size));
            if (board.Sources.ToKeys() is { } keys)
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

            writer.WriteNumber("opacity", board.Opacity);
            writer.WriteBoolean("alwaysOnTop", board.AlwaysOnTop);
            writer.WriteBoolean("clickThrough", board.ClickThrough);
            writer.WriteBoolean("fadeWhenIdle", board.FadeWhenIdle);
            writer.WriteBoolean("allSpaces", board.AllSpaces);
            writer.WriteBoolean("visible", board.Visible);
            writer.WriteStartObject("readouts");
            foreach (var readout in BoardReadouts.Order)
            {
                writer.WriteBoolean(BoardReadouts.Key(readout), board.Readouts[readout]);
            }

            writer.WriteEndObject();
            OrbsJson.WriteHosts(writer, board.Hosts);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("layouts");
        foreach (var layout in document.Layouts)
        {
            writer.WriteStartObject(layout.Arrangement);
            foreach (var (id, record) in layout.Positions.OrderBy(p => document.IndexOf(p.Key)))
            {
                OrbsJson.WriteRecord(writer, id, record);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>The document; <see cref="BoardsDocument.Empty"/> when the value is not a document.</summary>
    public static BoardsDocument Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("boards", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return BoardsDocument.Empty;
        }

        var boards = new List<BoardDefinition>(BoardsDocument.MaxBoards);
        foreach (var element in list.EnumerateArray().Take(OrbsJson.MaxOrbElementsRead))
        {
            if (boards.Count >= BoardsDocument.MaxBoards)
            {
                break;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            boards.Add(new BoardDefinition(OrbDefinition.CleanId(OrbsJson.ReadString(element, "id", OrbDefinition.MaxIdLength)) ?? OrbDefinition.NewId())
            {
                Style = BoardStyles.Parse(OrbsJson.ReadString(element, "style", 32)),
                Size = BoardSizes.Parse(OrbsJson.ReadString(element, "size", 8)),
                Sources = OrbsJson.ReadSources(element),
                Opacity = OrbsJson.ReadNumber(element, "opacity") ?? OrbDefinition.MaxOpacity,
                AlwaysOnTop = OrbsJson.ReadBool(element, "alwaysOnTop", true),
                ClickThrough = OrbsJson.ReadBool(element, "clickThrough", false),
                FadeWhenIdle = OrbsJson.ReadBool(element, "fadeWhenIdle", false),
                AllSpaces = OrbsJson.ReadBool(element, "allSpaces", true),
                Visible = OrbsJson.ReadBool(element, "visible", true),
                Readouts = ReadReadouts(element),
                Hosts = OrbsJson.ReadHosts(element),
            }.Validated());
        }

        var cleaned = new BoardsDocument(boards).Validated();
        var layouts = value.TryGetProperty("layouts", out var raw) ? ReadLayouts(raw, cleaned) : [];
        return (cleaned with { Layouts = layouts }).Validated();
    }

    /// <summary>
    /// A missing <c>readouts</c>, a missing key or a value that is not a boolean reads as on; an earlier version's <c>rowCost</c> key is
    /// ignored.
    /// </summary>
    private static BoardReadouts ReadReadouts(JsonElement board)
    {
        if (!board.TryGetProperty("readouts", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return BoardReadouts.All;
        }

        var readouts = BoardReadouts.All;
        foreach (var readout in BoardReadouts.Order)
        {
            readouts = readouts.With(readout, OrbsJson.ReadBool(value, BoardReadouts.Key(readout), true));
        }

        return readouts;
    }

    private static ImmutableArray<OrbLayout> ReadLayouts(JsonElement layouts, BoardsDocument document)
    {
        if (layouts.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<OrbLayout>(OrbsDocument.MaxArrangements);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arrangement in layouts.EnumerateObject().Take(OrbsJson.MaxLayoutElementsRead))
        {
            if (result.Count >= OrbsDocument.MaxArrangements)
            {
                break;
            }

            if (arrangement.Value.ValueKind != JsonValueKind.Object || !OrbsDocument.IsValidArrangement(arrangement.Name) || seen.Contains(arrangement.Name))
            {
                continue;
            }

            var positions = ImmutableDictionary.CreateBuilder<string, OrbPositionRecord>(StringComparer.Ordinal);
            foreach (var entry in arrangement.Value.EnumerateObject().Take(OrbsJson.MaxOrbElementsRead))
            {
                if (positions.Count < BoardsDocument.MaxBoards && document.Find(entry.Name) is not null && !positions.ContainsKey(entry.Name)
                    && OrbsJson.ReadRecord(entry.Value) is { } record)
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
}
