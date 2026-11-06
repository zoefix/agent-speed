using System.Text.Json.Nodes;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Boards;

namespace AgentSpeed.Presentation.Tests.Boards;

/// <summary>DESIGN 2c, Data model <c>fadeWhenIdle</c>: off for a new board and for one saved by an earlier version, persisted with the board.</summary>
public class BoardFadeWhenIdleTests
{
    private const string IdA = "B0A2D000-0000-4000-8000-00000000000A";
    private const string IdB = "B0A2D000-0000-4000-8000-00000000000B";

    private static AppSettings Read(string boardsJson) => SettingsSerializer.Deserialize("{ \"boards\": " + boardsJson + " }");

    [Fact]
    public void ABoardFromAnEarlierVersionDoesNotFade()
    {
        var settings = Read("{ \"version\": 1, \"boards\": [ { \"id\": \"" + IdA + "\", \"style\": \"neon\", \"size\": \"M\", \"opacity\": 0.8, " +
            "\"alwaysOnTop\": true, \"clickThrough\": false, \"allSpaces\": true, \"visible\": true } ] }");
        var board = settings.Boards.Find(IdA)!;

        Assert.False(board.FadeWhenIdle);
        Assert.Equal(0.8, board.Opacity);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("[]")]
    public void AValueThatIsNotABooleanReadsAsOff(string value)
    {
        var settings = Read("{ \"version\": 1, \"boards\": [ { \"id\": \"" + IdA + "\", \"fadeWhenIdle\": " + value + " } ] }");

        Assert.False(settings.Boards.Find(IdA)!.FadeWhenIdle);
    }

    [Fact]
    public void ANewBoardDoesNotFade()
    {
        var added = BoardsDocument.Empty.Add(IdA);

        Assert.NotNull(added);
        Assert.False(added.Value.Board.FadeWhenIdle);
    }

    [Fact]
    public void TheSwitchRoundTripsWithItsBoard()
    {
        var document = new BoardsDocument([new BoardDefinition(IdA), new BoardDefinition(IdB)]);
        var faded = BoardEditing.SetFadeWhenIdle(document, IdB, true);
        Assert.Same(faded, BoardEditing.SetFadeWhenIdle(faded, "missing", true));

        var json = SettingsSerializer.Serialize(AppSettings.Default with { Boards = faded });
        var boards = JsonNode.Parse(json)!["boards"]!["boards"]!;
        Assert.False(boards[0]!["fadeWhenIdle"]!.GetValue<bool>());
        Assert.True(boards[1]!["fadeWhenIdle"]!.GetValue<bool>());

        var back = SettingsSerializer.Deserialize(json).Boards;
        Assert.Equal(faded, back);
        Assert.False(back.Find(IdA)!.FadeWhenIdle);
        Assert.True(back.Find(IdB)!.FadeWhenIdle);
        Assert.False(BoardEditing.SetFadeWhenIdle(back, IdB, false).Find(IdB)!.FadeWhenIdle);
    }
}
