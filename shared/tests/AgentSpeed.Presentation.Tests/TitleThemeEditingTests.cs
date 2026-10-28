using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Tests;

public class TitleThemeEditingTests
{
    private static readonly Localizer Zh = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);

    private static string CopyName(string name) => Zh.Format("titles.custom.copyName", name);

    private static TitleThemesDocument CopyOf(TitleThemesDocument doc, string source, string id) =>
        TitleThemeEditing.Copy(doc, source, id, doc.Theme(source)!.Name(Zh), CopyName)!;

    [Fact]
    public void CopyingAPresetCreatesASelectedEmptySetBasedOnIt()
    {
        var doc = CopyOf(TitleThemesDocument.Default, "cultivation", "N1");
        var set = Assert.Single(doc.Custom);
        Assert.Equal("N1", doc.Selected);
        Assert.Equal("修真境界（自訂）", set.Name);
        Assert.Equal(TitlePreset.Cultivation, set.Base);
        Assert.True(set.IsEmpty);

        var again = CopyOf(doc, "cultivation", "N2");
        Assert.Equal("修真境界（自訂） 2", again.Custom[1].Name);
        Assert.Equal("修真境界（自訂） 3", CopyOf(again, "cultivation", "N3").Custom[2].Name);
    }

    [Fact]
    public void CopyingACustomSetInheritsItsBaseAndTexts()
    {
        var doc = TitleThemeEditing.SetField(CopyOf(TitleThemesDocument.Default, "space", "A"), "A", 4, "月亮");
        doc = TitleThemeEditing.SetField(doc, "A", 0, "睡覺");
        var copy = CopyOf(doc, "A", "B");
        var set = copy.CustomSet("B")!;
        Assert.Equal(TitlePreset.Space, set.Base);
        Assert.Equal("月亮", set.Field(4));
        Assert.Equal("睡覺", set.Idle);
        Assert.Equal("宇宙（自訂）（自訂）", set.Name);
    }

    [Fact]
    public void CopyingIsRefusedAtEightSetsForUnknownSourcesAndTakenIds()
    {
        var doc = TitleThemesDocument.Default;
        for (var i = 0; i < 8; i++)
        {
            doc = CopyOf(doc, "wind", $"S{i}");
        }

        Assert.False(doc.CanAddCustom);
        Assert.Null(TitleThemeEditing.Copy(doc, "wind", "S9", "x", CopyName));
        Assert.Null(TitleThemeEditing.Copy(TitleThemesDocument.Default, "nothing", "S9", "x", CopyName));
        var one = CopyOf(TitleThemesDocument.Default, "wind", "S0");
        Assert.Null(TitleThemeEditing.Copy(one, "wind", "S0", "x", CopyName));
        Assert.Null(TitleThemeEditing.Copy(one, "wind", "mage", "x", CopyName));
    }

    [Fact]
    public void UniqueNamesStayWithinThirtyTwoCharacters()
    {
        var name = new string('名', 32);
        var doc = TitleThemesDocument.Default with { Custom = [new CustomTitleSet("a", name, TitlePreset.Wind)] };
        var unique = TitleThemeEditing.UniqueName(doc, name);
        Assert.Equal(new string('名', 30) + " 2", unique);
    }

    [Fact]
    public void DeletingASelectedSetSelectsItsBase()
    {
        var doc = CopyOf(TitleThemesDocument.Default, "mage", "M");
        var deletion = TitleThemeEditing.Delete(doc, "M")!.Value;
        Assert.Equal(TitlePreset.Mage, deletion.Base);
        Assert.Equal("mage", deletion.Document.Selected);
        Assert.Empty(deletion.Document.Custom);

        var other = TitleThemeEditing.Select(doc, "wind");
        Assert.Equal("wind", TitleThemeEditing.Delete(other, "M")!.Value.Document.Selected);
        Assert.Null(TitleThemeEditing.Delete(doc, "mage"));
    }

    [Fact]
    public void SelectRenameBaseFieldAndResetAll()
    {
        var doc = CopyOf(TitleThemesDocument.Default, "animals", "A");
        Assert.Same(doc, TitleThemeEditing.Select(doc, "unknown"));
        Assert.Equal("gameRank", TitleThemeEditing.Select(doc, "gameRank").Selected);

        Assert.Equal("我的動物園", TitleThemeEditing.Rename(doc, "A", "  我的動物園 ").CustomSet("A")!.Name);
        Assert.Same(doc, TitleThemeEditing.Rename(doc, "A", "   "));

        var filled = TitleThemeEditing.SetField(doc, "A", 13, " 獵 鷹 ");
        Assert.Equal("獵 鷹", filled.CustomSet("A")!.Field(13));
        var rebased = TitleThemeEditing.SetBase(filled, "A", TitlePreset.Space);
        var theme = rebased.Theme("A")!;
        Assert.Equal("獵 鷹", theme.Text(SpeedTitle.Rocket, Zh));
        Assert.Equal("星塵", theme.Text(SpeedTitle.Walk, Zh));

        var reset = TitleThemeEditing.ResetAll(rebased, "A");
        Assert.True(reset.CustomSet("A")!.IsEmpty);
        Assert.Equal(TitlePreset.Space, reset.CustomSet("A")!.Base);
    }
}

public class TitleTransitionTests
{
    [Fact]
    public void TheOutgoingTitleIsGoneBeforeTheIncomingOneShows()
    {
        Assert.Equal(1, TitleTransitions.Outgoing(0).Alpha);
        Assert.Equal(0, TitleTransitions.Outgoing(0.4).Alpha, 6);
        Assert.Equal(1, TitleTransitions.Outgoing(0.4).Travel, 6);
        Assert.Equal(0, TitleTransitions.Incoming(0.35).Alpha, 6);
        Assert.Equal(0, TitleTransitions.Incoming(0.35).Travel, 6);
        Assert.Equal(1, TitleTransitions.Incoming(0.8).Alpha, 6);
        Assert.Equal(1, TitleTransitions.Incoming(1).Travel, 6);
        for (var p = 0.0; p <= 1; p += 0.01)
        {
            Assert.True(Math.Min(TitleTransitions.Outgoing(p).Alpha, TitleTransitions.Incoming(p).Alpha) < 0.05, $"p={p}");
        }
    }

    [Fact]
    public void ARenameOfTheShownLevelCrossfadesButALevelChangeDoesNot()
    {
        var tracker = new TitleRenameTracker();
        Assert.False(tracker.Note(5, "元嬰", 0));
        Assert.False(tracker.Note(5, "元嬰", 1));
        Assert.True(tracker.Note(5, "行星", 2));
        Assert.Equal("元嬰", tracker.From);
        Assert.True(tracker.IsRunning(2.1));
        Assert.Equal(0.5, tracker.Mix(2.125), 6);
        Assert.False(tracker.IsRunning(2.3));
        Assert.Equal(1, tracker.Mix(2.3));
        Assert.False(tracker.Note(6, "恆星", 3));
        Assert.Null(tracker.From);
        Assert.Equal(1, tracker.Mix(3));
    }

    [Fact]
    public void TitlesTightenTrackingThenShrinkToEightyPercent()
    {
        // Fits as it is.
        Assert.Equal((1.6, 9.0), OrbLabelFit.Fit(40, 1.6, 5, 60, 9));
        // Tightening alone is enough: (60 − 55) / 5 gaps = 1.0.
        Assert.Equal((1.0, 9.0), OrbLabelFit.Fit(55, 1.6, 5, 60, 9));
        // Too wide even untracked: no tracking, shrink.
        var shrunk = OrbLabelFit.Fit(66, 1.6, 5, 60, 9);
        Assert.Equal(0, shrunk.Tracking);
        Assert.Equal(Math.Floor(9 * 60 / 66.0 * 4) / 4, shrunk.Size);
        // Never below 80 %, never below the 7 pt floor.
        Assert.Equal(8, OrbLabelFit.Fit(200, 1.6, 5, 60, 10).Size, 6);
        Assert.Equal(7, OrbLabelFit.Fit(200, 1.6, 5, 60, 8).Size, 6);
        Assert.Equal(7, OrbLabelFit.Fit(200, 0, 5, 60, 7).Size, 6);
    }

    [Fact]
    public void CardStripsShowWholeTitlesKeepingTheFirstAndTheLast()
    {
        static double Measure(string text) => text.Length * 10;
        string[] titles = ["初學乍練", "略有小成", "出類拔萃", "天下無雙"];
        Assert.Equal([0, 1, 2, 3], TitleCardStrip.Choose(titles, 1000, Measure));
        var all = TitleCardStrip.Join(titles, [0, 1, 2, 3]).Length * 10;
        Assert.Equal([0, 2, 3], TitleCardStrip.Choose(titles, all - 1, Measure));
        Assert.Equal([0, 3], TitleCardStrip.Choose(titles, TitleCardStrip.Join(titles, [0, 3]).Length * 10, Measure));
        Assert.Equal([0], TitleCardStrip.Choose(titles, 10, Measure));
        Assert.Equal("初學乍練 · 天下無雙", TitleCardStrip.Join(titles, [0, 3]));
        Assert.Equal([SpeedTitle.Walk, SpeedTitle.Car, SpeedTitle.Maglev, SpeedTitle.Rocket], TitleCardStrip.Levels);
    }
}
