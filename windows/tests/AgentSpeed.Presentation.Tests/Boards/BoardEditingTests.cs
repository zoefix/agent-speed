using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Boards;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests.Boards;

public class BoardEditingTests
{
    private static BoardsDocument Two() => new([new BoardDefinition("a"), new BoardDefinition("b") { Style = BoardStyle.Glass }]);

    [Fact]
    public void EditsChangeOnlyTheirBoard()
    {
        var document = Two();
        var styled = BoardEditing.SetStyle(document, "a", BoardStyle.Timing);
        Assert.Equal(BoardStyle.Timing, styled.Find("a")!.Style);
        Assert.Equal(BoardStyle.Glass, styled.Find("b")!.Style);

        Assert.Equal(BoardSize.L, BoardEditing.SetSize(document, "a", BoardSize.L).Find("a")!.Size);
        Assert.False(BoardEditing.SetAlwaysOnTop(document, "a", false).Find("a")!.AlwaysOnTop);
        Assert.True(BoardEditing.SetClickThrough(document, "a", true).Find("a")!.ClickThrough);
        Assert.True(BoardEditing.SetFadeWhenIdle(document, "a", true).Find("a")!.FadeWhenIdle);
        Assert.False(document.Find("b")!.FadeWhenIdle);
        Assert.False(BoardEditing.SetVisible(document, "b", false).Find("b")!.Visible);
        Assert.Same(document, BoardEditing.SetSize(document, "zzz", BoardSize.L));
    }

    [Fact]
    public void ReadoutSwitchesAreIndependent()
    {
        var document = Two();
        var noCost = BoardEditing.SetReadout(document, "a", BoardReadout.Cost, false);
        // 每列費用 does not follow 費用 (DESIGN 2c, Switches).
        Assert.Equal(BoardReadouts.All with { Cost = false }, noCost.Find("a")!.Readouts);
        Assert.Equal(BoardReadouts.All, noCost.Find("b")!.Readouts);
        var none = BoardReadouts.Order.Aggregate(document, (d, r) => BoardEditing.SetReadout(d, "a", r, false));
        Assert.True(none.Find("a")!.Readouts.IsNone);
        Assert.False(none.Find("a")!.Readouts.Hero);
        Assert.True(BoardEditing.SetReadout(none, "a", BoardReadout.Thinking, true).Find("a")!.Readouts.Hero);
        Assert.Same(document, BoardEditing.SetReadout(document, "zzz", BoardReadout.Cost, false));
    }

    [Fact]
    public void OpacityIsClampedToTheOrbsRange()
    {
        var document = Two();
        Assert.Equal(OrbDefinition.MinOpacity, BoardEditing.SetOpacity(document, "a", 0.1).Find("a")!.Opacity);
        Assert.Equal(1.0, BoardEditing.SetOpacity(document, "a", 3).Find("a")!.Opacity);
        Assert.Equal(0.7, BoardEditing.SetOpacity(document, "a", 0.7).Find("a")!.Opacity, 6);
    }

    [Fact]
    public void SourcesToggleLikeAnOrbsAndTheLastOneStays()
    {
        var document = Two();
        var codex = BoardEditing.ToggleSource(document, "a", SourceKind.Codex)!;
        Assert.Equal(SourceSet.Of(SourceKind.Codex), codex.Find("a")!.TrackedSources);

        var both = BoardEditing.ToggleSource(codex, "a", SourceKind.ClaudeCode)!;
        Assert.Equal(SourceSet.Create([SourceKind.ClaudeCode, SourceKind.Codex]), both.Find("a")!.TrackedSources);

        Assert.Null(BoardEditing.ToggleSource(codex, "a", SourceKind.Codex));
        Assert.False(BoardEditing.CanUncheck(codex.Find("a")!, SourceKind.Codex));
        Assert.True(BoardEditing.SetAllSources(both, "a").Find("a")!.Sources.TracksAll);
    }

    [Fact]
    public void ShowAndHideActOnEveryBoard()
    {
        var document = BoardEditing.SetVisible(Two(), "a", false);
        Assert.True(BoardEditing.AnyVisible(document));

        var hidden = BoardEditing.SetAllVisible(document, false);
        Assert.False(BoardEditing.AnyVisible(hidden));
        Assert.All(BoardEditing.SetAllVisible(hidden, true).Boards, b => Assert.True(b.Visible));
        Assert.Same(hidden, BoardEditing.SetAllVisible(hidden, false));
    }

    [Fact]
    public void RemoveDropsTheBoardAndItsPositions()
    {
        var document = Two().WithPosition("MAIN", "a", new OrbPositionRecord("MAIN", OrbAxis.High(12), OrbAxis.Fraction(0.2)));
        var removed = BoardEditing.Remove(document, "a")!;

        Assert.Null(removed.Find("a"));
        Assert.Null(removed.RecordFor("MAIN", "a"));
        Assert.Null(BoardEditing.Remove(removed, "a"));
    }

    [Fact]
    public void EditedBoardsSurviveTheSettingsRoundTrip()
    {
        var document = BoardEditing.SetOpacity(BoardEditing.ToggleSource(BoardEditing.SetSize(Two(), "a", BoardSize.S), "a", SourceKind.Codex)!, "b", 0.6);
        var settings = AppSettings.Default with { Boards = document };
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings)).Boards;

        Assert.Equal(document, back);
    }

    [Fact]
    public void SettingsRowsNameTheStyleSourcesAndSize()
    {
        var en = new Localizer(Localizer.LoadEmbeddedTable(), LanguageCodes.En);
        var board = new BoardDefinition("a") { Style = BoardStyle.Timing, Size = BoardSize.L, Sources = OrbSources.Of(SourceKind.Codex) };

        Assert.Equal(en["boards.style.timing"], BoardRowText.Name(board, en));
        Assert.Equal(OrbNaming.SourceName(SourceKind.Codex, en) + " · L", BoardRowText.Detail(board, en));
        Assert.Equal(en["boards.style.timing"] + ", " + BoardRowText.Detail(board, en), BoardRowText.Accessible(board, en));
        Assert.EndsWith(en["orbs.hidden"], BoardRowText.Accessible(board with { Visible = false }, en));
    }

    [Fact]
    public void TheListCountsBoardsAndAddExplainsTheLimit()
    {
        var en = new Localizer(Localizer.LoadEmbeddedTable(), LanguageCodes.En);
        Assert.Equal(en.Format("orbs.count", 2, 4), BoardRowText.Count(Two(), en));
        Assert.Equal(en["boards.add"], BoardRowText.AddTooltip(Two(), en));

        var full = new BoardsDocument(Enumerable.Range(0, 4).Select(i => new BoardDefinition("b" + i)));
        Assert.Equal(en.Format("boards.limit", 4), BoardRowText.AddTooltip(full, en));
    }
}
