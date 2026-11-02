using System.Text.Json.Nodes;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Boards;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests.Boards;

public class BoardsSettingsTests
{
    private const string IdA = "B0A2D000-0000-4000-8000-00000000000A";
    private const string IdB = "B0A2D000-0000-4000-8000-00000000000B";
    private const string Arrangement = "DISPLAY1|2560x1440";

    private static BoardsDocument Sample() =>
        new BoardsDocument([
                new BoardDefinition(IdA)
                {
                    Style = BoardStyle.Timing, Size = BoardSize.L, Sources = OrbSources.Of(SourceSet.Of(SourceKind.Codex)), Opacity = 0.7, AlwaysOnTop = false,
                    ClickThrough = true, FadeWhenIdle = true, AllSpaces = false, Readouts = new BoardReadouts(intelligence: true, thinking: false, cost: true),
                },
                new BoardDefinition(IdB) { Visible = false },
            ])
            .WithPosition(Arrangement, IdA, new OrbPositionRecord("DISPLAY1|2560x1440", OrbAxis.High(12), OrbAxis.Fraction(0.2)));

    private static AppSettings Read(string boardsJson) => SettingsSerializer.Deserialize("{ \"boards\": " + boardsJson + " }");

    [Fact]
    public void AFreshInstallAndAnUpgradeHaveNoBoard()
    {
        Assert.Empty(AppSettings.Default.Boards.Boards);
        Assert.Empty(SettingsSerializer.Deserialize("{ \"orbs\": { \"version\": 1, \"orbs\": [] } }").Boards.Boards);
    }

    [Fact]
    public void RoundTripsEveryField()
    {
        var settings = AppSettings.Default with { Boards = Sample() };
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));

        Assert.Equal(settings.Boards, back.Boards);
        var a = back.Boards.Find(IdA)!;
        Assert.Equal(BoardStyle.Timing, a.Style);
        Assert.Equal(BoardSize.L, a.Size);
        Assert.Equal(SourceSet.Of(SourceKind.Codex), a.TrackedSources);
        Assert.Equal(0.7, a.Opacity);
        Assert.True(a.ClickThrough);
        Assert.True(a.FadeWhenIdle);
        Assert.False(a.AlwaysOnTop);
        Assert.False(back.Boards.Find(IdB)!.FadeWhenIdle);
        Assert.False(back.Boards.Find(IdB)!.Visible);
        Assert.Equal(new BoardReadouts(true, false, true), a.Readouts);
        Assert.Equal(BoardReadouts.All, back.Boards.Find(IdB)!.Readouts);
        Assert.Equal(OrbAxis.High(12), back.Boards.RecordFor(Arrangement, IdA)!.Value.X);
    }

    [Fact]
    public void WritesTheSpecifiedShape()
    {
        var root = JsonNode.Parse(SettingsSerializer.Serialize(AppSettings.Default with { Boards = Sample() }))!;
        var boards = root["boards"]!;
        Assert.Equal(1, boards["version"]!.GetValue<int>());
        var first = boards["boards"]![0]!;
        Assert.Equal(IdA, first["id"]!.GetValue<string>());
        Assert.Equal("timing", first["style"]!.GetValue<string>());
        Assert.Equal("L", first["size"]!.GetValue<string>());
        Assert.Equal("codex", first["sources"]![0]!.GetValue<string>());
        Assert.Equal("all", boards["boards"]![1]!["sources"]!.GetValue<string>());
        foreach (var key in new[] { "opacity", "alwaysOnTop", "clickThrough", "fadeWhenIdle", "allSpaces", "visible" })
        {
            Assert.NotNull(first[key]);
        }

        Assert.True(first["fadeWhenIdle"]!.GetValue<bool>());
        Assert.False(boards["boards"]![1]!["fadeWhenIdle"]!.GetValue<bool>());

        var readouts = first["readouts"]!;
        Assert.True(readouts["intelligence"]!.GetValue<bool>());
        Assert.False(readouts["thinking"]!.GetValue<bool>());
        Assert.True(readouts["cost"]!.GetValue<bool>());
        Assert.Null(readouts["rowCost"]);

        var record = boards["layouts"]![Arrangement]![IdA]!;
        Assert.Equal("high", record["xAnchor"]!.GetValue<string>());
        Assert.Equal(0.2, record["y"]!.GetValue<double>());
    }

    [Theory]
    [InlineData("\"sparkle\"", BoardStyle.Glass)]
    [InlineData("42", BoardStyle.Glass)]
    [InlineData("\"neon\"", BoardStyle.Neon)]
    public void UnknownStylesAreGlass(string style, BoardStyle expected)
    {
        var settings = Read("{ \"version\": 1, \"boards\": [ { \"id\": \"" + IdA + "\", \"style\": " + style + " } ] }");
        Assert.Equal(expected, settings.Boards.Boards[0].Style);
    }

    [Theory]
    [InlineData("\"XL\"", BoardSize.M)]
    [InlineData("\"S\"", BoardSize.S)]
    [InlineData("null", BoardSize.M)]
    public void UnknownSizesAreMedium(string size, BoardSize expected)
    {
        var settings = Read("{ \"version\": 1, \"boards\": [ { \"id\": \"" + IdA + "\", \"size\": " + size + " } ] }");
        Assert.Equal(expected, settings.Boards.Boards[0].Size);
    }

    // DESIGN 2c data model: a missing field or key, or a value that is not a boolean, reads as true; no migration step. An earlier
    // version's rowCost key is ignored.
    [Theory]
    [InlineData("", true, true, true)]
    [InlineData(", \"readouts\": 7", true, true, true)]
    [InlineData(", \"readouts\": null", true, true, true)]
    [InlineData(", \"readouts\": { }", true, true, true)]
    [InlineData(", \"readouts\": { \"thinking\": false }", true, false, true)]
    [InlineData(", \"readouts\": { \"intelligence\": \"no\", \"cost\": 0, \"rowCost\": false }", true, true, true)]
    [InlineData(", \"readouts\": { \"intelligence\": false, \"thinking\": false, \"cost\": false, \"rowCost\": false }", false, false, false)]
    [InlineData(", \"readouts\": { \"rowCost\": true, \"cost\": false }", true, true, false)]
    public void ReadoutsReadTolerantly(string field, bool intelligence, bool thinking, bool cost)
    {
        var settings = Read("{ \"version\": 1, \"boards\": [ { \"id\": \"" + IdA + "\"" + field + " } ] }");
        Assert.Equal(new BoardReadouts(intelligence, thinking, cost), settings.Boards.Boards[0].Readouts);
    }

    [Fact]
    public void AStoredRowCostKeyIsDroppedOnTheNextWrite()
    {
        var settings = Read("{ \"version\": 1, \"boards\": [ { \"id\": \"" + IdA + "\", \"readouts\": { \"thinking\": false, \"rowCost\": false } } ] }");
        var readouts = JsonNode.Parse(SettingsSerializer.Serialize(settings))!["boards"]!["boards"]![0]!["readouts"]!.AsObject();
        Assert.Equal(["intelligence", "thinking", "cost"], readouts.Select(p => p.Key));
        Assert.False(readouts["thinking"]!.GetValue<bool>());
    }

    [Fact]
    public void ReadingIsTolerantAndBounded()
    {
        var many = string.Join(",", Enumerable.Range(0, 9).Select(i => "{ \"id\": \"id-" + i + "\", \"opacity\": 9 }"));
        var settings = Read("{ \"version\": 1, \"boards\": [ 7, \"x\", " + many + " ], \"layouts\": { \"\": {}, \"K\": { \"id-0\": { \"display\": \"D\" } } } }");

        Assert.Equal(BoardsDocument.MaxBoards, settings.Boards.Count);
        Assert.All(settings.Boards.Boards, b => Assert.Equal(1.0, b.Opacity));
        Assert.Empty(settings.Boards.Layouts);
        Assert.Empty(Read("[]").Boards.Boards);
        Assert.Empty(Read("{ \"boards\": 3 }").Boards.Boards);
    }

    [Fact]
    public void DuplicateIdsGetFreshOnes()
    {
        var settings = Read("{ \"boards\": [ { \"id\": \"" + IdA + "\" }, { \"id\": \"" + IdA + "\" } ] }");
        Assert.Equal(2, settings.Boards.Boards.Select(b => b.Id).Distinct().Count());
    }

    [Fact]
    public void AddUsesTheLastUsedStyleThenNeonAndStopsAtFour()
    {
        Assert.Equal(BoardStyle.Neon, BoardsDocument.Empty.Add()!.Value.Board.Style);
        var document = BoardsDocument.Empty;
        for (var i = 0; i < BoardsDocument.MaxBoards; i++)
        {
            var added = document.Add()!.Value;
            Assert.Equal(BoardSize.M, added.Board.Size);
            Assert.True(added.Board.Sources.IsAll);
            Assert.Equal(BoardReadouts.All, added.Board.Readouts);
            // Add copies nothing: the next board has every switch on again.
            document = added.Document.Replace(added.Board with { Style = BoardStyle.Glass, Readouts = BoardReadouts.None });
        }

        Assert.False(document.CanAdd);
        Assert.Null(document.Add());
        Assert.Equal(BoardStyle.Glass, document.NextStyle);
    }

    [Fact]
    public void RemovingABoardDropsItsPositions()
    {
        var document = Sample().Remove(IdA);
        Assert.Null(document.Find(IdA));
        Assert.Empty(document.Layouts);
    }

    [Fact]
    public void ArrangementsAreMostRecentlyUsedFirstAndBounded()
    {
        var document = new BoardsDocument([new BoardDefinition(IdA)]);
        for (var i = 0; i < 20; i++)
        {
            document = document.WithPosition("D" + i, IdA, new OrbPositionRecord("D" + i, OrbAxis.Fraction(0.5), OrbAxis.Fraction(0.5)));
        }

        Assert.Equal(OrbsDocument.MaxArrangements, document.Layouts.Length);
        Assert.Equal("D19", document.Layouts[0].Arrangement);
        Assert.Equal("D18", document.Touch("D18").Layouts[0].Arrangement);
    }
}
