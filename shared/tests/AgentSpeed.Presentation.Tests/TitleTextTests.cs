using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Tests;

public class TitleTextTests
{
    [Theory]
    [InlineData("  結丹  ", "結丹")]
    [InlineData("Red\tGiant", "Red Giant")]
    [InlineData("Red\r\n\r\nGiant", "Red Giant")]
    [InlineData("大\u3000魔\u00A0導師", "大 魔 導師")]
    [InlineData("a   b", "a b")]
    [InlineData("a\u2028b\u2029c", "abc")]
    [InlineData("\u202Eevil\u202C\u2066x\u2069", "evilx")]
    [InlineData("bell\u0007\u0000", "bell")]
    [InlineData("★ 傳奇 ✦ ∞", "★ 傳奇 ✦ ∞")]
    [InlineData("", "")]
    public void TheTextRulesCleanSpacesControlsAndBidi(string input, string expected)
    {
        var result = TitleText.Sanitize(input);
        Assert.Equal(expected, result.Text);
        Assert.False(result.RemovedEmoji);
    }

    [Fact]
    public void TextIsComposedToNfc()
    {
        Assert.Equal("caf\u00E9", TitleText.Sanitize("cafe\u0301").Text);
    }

    [Theory]
    [InlineData("火箭🚀", "火箭")]
    [InlineData("🔥 狂飆 🔥", "狂飆")]
    [InlineData("❤️ love", "love")]
    [InlineData("1️⃣ first", "first")]
    [InlineData("👨‍👩‍👧 family", "family")]
    [InlineData("🇹🇼 台灣", "台灣")]
    [InlineData("👍🏽 ok", "ok")]
    public void ColorEmojiAreRemovedAndReported(string input, string expected)
    {
        var result = TitleText.Sanitize(input);
        Assert.Equal(expected, result.Text);
        Assert.True(result.RemovedEmoji);
    }

    [Theory]
    [InlineData("★")]
    [InlineData("✦")]
    [InlineData("∞")]
    [InlineData("☆")]
    [InlineData("#")]
    [InlineData("1")]
    [InlineData("©")]
    public void TextSymbolsAreNotColorEmoji(string symbol)
    {
        Assert.False(TitleText.IsColorEmoji(symbol));
    }

    [Fact]
    public void AtMostTwentyFourGraphemeClustersAreKept()
    {
        var long1 = string.Concat(Enumerable.Repeat("e\u0301", 30));
        Assert.Equal(24, TitleText.GraphemeCount(TitleText.Sanitize(long1).Text));
        var hangul = string.Concat(Enumerable.Repeat("가", 30));
        Assert.Equal(24, TitleText.Sanitize(hangul).Text.Length);
        Assert.Equal("abc", TitleText.Sanitize("abcdef", 3).Text);
    }

    [Fact]
    public void FittingCutsAtTheLastWholeGraphemeThatFits()
    {
        static double Measure(string text) => TitleText.GraphemeCount(text) * 7.0;

        Assert.Equal(new FittedText("凡人", false), TitleText.Fitted("凡人", 51.5, Measure));
        var cut = TitleText.Fitted("一二三四五六七八九", 51.5, Measure);
        Assert.True(cut.Cut);
        Assert.Equal("一二三四五六七", cut.Text);
        Assert.Equal("ab", TitleText.Fitted("ab cd", 21, Measure).Text);
        var combining = TitleText.Fitted("e\u0301e\u0301e\u0301", 14, Measure);
        Assert.Equal("e\u0301e\u0301", combining.Text);
        Assert.Equal(string.Empty, TitleText.Fitted("abc", 1, Measure).Text);
    }

    [Fact]
    public void DisplayCaseUppercasesOnlyForEnglish()
    {
        Assert.Equal("10X DEV", TitleText.DisplayCase("10x dev", english: true));
        Assert.Equal("10x dev", TitleText.DisplayCase("10x dev", english: false));
        Assert.Equal("我的 ABC", TitleText.DisplayCase("我的 abc", english: true));
    }

    [Fact]
    public void CustomNamesFollowTheTextRulesWithinThirtyTwoCharacters()
    {
        Assert.Null(CustomTitleSet.CleanName("   "));
        Assert.Null(CustomTitleSet.CleanName("🚀"));
        Assert.Equal("修 真", CustomTitleSet.CleanName(" 修\t真 "));
        Assert.Equal(32, CustomTitleSet.CleanName(new string('x', 40))!.Length);
    }
}
