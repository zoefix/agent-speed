using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Tests;

/// <summary>DESIGN 4c, Hero title badge, Title layout (measured with a deterministic fake font).</summary>
public class HeroBadgeLayoutTests
{
    /// <summary>CJK 1 em per character, everything else 0.62 em; tracking after every character but the last.</summary>
    private static double Measure(string text, double size, double tracking)
    {
        var graphemes = TitleBadgeLayout.Graphemes(text);
        var width = 0.0;
        foreach (var g in graphemes)
        {
            width += size * (TitleBadgeLayout.HasCjk(g) ? 1.0 : 0.62);
        }

        return width + tracking * size * Math.Max(0, graphemes.Count - 1);
    }

    private static TitleBadgeLayout Layout(string text, string language) => TitleBadgeLayout.Layout(text, language, Measure);

    [Theory]
    [InlineData("zh-Hant")]
    [InlineData("zh-Hans")]
    public void FourHanCharactersMakeASealInChinese(string language)
    {
        var seal = Layout("初學乍練", language);

        Assert.Equal(TitleBadgeKind.Seal, seal.Kind);
        Assert.Equal(new[] { "初學", "乍練" }, seal.Lines);
        Assert.Equal(21, seal.Size);
        Assert.Equal(1.04, seal.LineAdvance);
    }

    [Fact]
    public void JapaneseNeverUsesTheSeal()
    {
        var layout = Layout("初學乍練", "ja");

        Assert.Equal(TitleBadgeKind.OneLine, layout.Kind);
        Assert.Equal(new[] { "初學乍練" }, layout.Lines);
        Assert.True(Measure("初學乍練", layout.Size, layout.Tracking) <= TitleBadgeLayout.MaxWidth);
    }

    [Theory]
    [InlineData("RED GIANT", "RED", "GIANT")]
    [InlineData("FORCE 7", "FORCE", "7")]
    public void LatinTitlesWithASpaceBreakIntoTwoLines(string text, string first, string second)
    {
        var layout = Layout(text, "en");

        Assert.Equal(TitleBadgeKind.TwoLines, layout.Kind);
        Assert.Equal(new[] { first, second }, layout.Lines);
        Assert.Equal(20, layout.Size);
        Assert.Equal(0.05, layout.Tracking);
        Assert.Equal(1.1, layout.LineAdvance);
    }

    [Fact]
    public void FiveHanCharactersBreakAfterTheSecond()
    {
        var layout = Layout("初級工程師", "zh-Hant");

        Assert.Equal(TitleBadgeKind.TwoLines, layout.Kind);
        Assert.Equal(new[] { "初級", "工程師" }, layout.Lines);
        Assert.Equal(22, layout.Size);
        Assert.Equal(0.04, layout.Tracking);
    }

    [Fact]
    public void SixOrMoreHanCharactersBreakAtTheMiddle()
    {
        Assert.Equal(new[] { "一二三", "四五六" }, Layout("一二三四五六", "zh-Hans").Lines);
        Assert.Equal(new[] { "一二三", "四五六七" }, Layout("一二三四五六七", "zh-Hant").Lines);
    }

    [Fact]
    public void MixedTitlesBreakAtTheSpace()
    {
        Assert.Equal(new[] { "10x", "工程師" }, Layout("10x 工程師", "zh-Hant").Lines);
    }

    [Fact]
    public void ALongSingleWordStaysOnOneSmallerLine()
    {
        var layout = Layout("TRIBULATION", "en");

        Assert.Equal(TitleBadgeKind.OneLine, layout.Kind);
        Assert.InRange(layout.Size, 9.5, 12);
        Assert.Equal(0, layout.Tracking);
        Assert.True(Measure("TRIBULATION", layout.Size, 0) <= TitleBadgeLayout.MaxWidth);
        Assert.True(Measure("TRIBULATION", layout.Size + 0.5, 0) > TitleBadgeLayout.MaxWidth);
    }

    [Fact]
    public void RunsOfKanaNeverBreak()
    {
        var layout = Layout("ブラックホール", "ja");

        Assert.Equal(TitleBadgeKind.OneLine, layout.Kind);
        Assert.InRange(layout.Size, 9.5, 12);
    }

    [Theory]
    [InlineData("神", "zh-Hant")]
    [InlineData("W", "en")]
    public void ASingleGraphemeTakesAtMost34Units(string text, string language)
    {
        var layout = Layout(text, language);

        Assert.Equal(TitleBadgeKind.OneLine, layout.Kind);
        Assert.Equal(34, layout.Size);
    }

    [Fact]
    public void ShortTitlesUseTheirMaximum()
    {
        Assert.Equal(26, Layout("S1", "en").Size);
        Assert.Equal(22, Layout("CTO", "en").Size);
        Assert.Equal(28, Layout("流星", "zh-Hant").Size);
    }

    [Fact]
    public void NeverTruncatesAndNeverGoesBelowNine()
    {
        var text = new string('M', 24);
        var layout = Layout(text, "en");

        Assert.Equal(TitleBadgeKind.OneLine, layout.Kind);
        Assert.Equal(TitleBadgeLayout.Smallest, layout.Size);
        Assert.Equal(new[] { text }, layout.Lines);
    }
}
