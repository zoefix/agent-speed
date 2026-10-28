using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Tests;

public class TitleSettingsTextTests
{
    private static readonly StringTable Embedded = Localizer.LoadEmbeddedTable();

    private static Localizer For(string language) => new(Embedded, language);

    private static TitleTheme Cultivation => TitleTheme.Of(TitlePreset.Cultivation);

    [Fact]
    public void StripShowsLevelsOneFiveNineAndThirteen()
    {
        Assert.Equal(["凡人", "元嬰", "大乘", "道祖"], TitleSettingsText.StripTitles(Cultivation, For(LanguageCodes.ZhHant)));
        Assert.Equal(["WALKING", "CAR", "MAGLEV", "ROCKET"], TitleSettingsText.StripTitles(TitleTheme.Vehicles, For(LanguageCodes.En)));
    }

    [Fact]
    public void StripOfACustomSetShowsItsEffectiveTitles()
    {
        var set = new CustomTitleSet("A1", "我的修仙路", TitlePreset.Cultivation, titles: ["", "", "", "", "結丹"]);
        Assert.Equal(["凡人", "結丹", "大乘", "道祖"], TitleSettingsText.StripTitles(TitleTheme.Of(set), For(LanguageCodes.ZhHant)));
    }

    [Fact]
    public void LadderListsEveryTitleWithItsThresholdAtTheRedline()
    {
        var ladder = TitleSettingsText.Ladder(Cultivation, 2000, For(LanguageCodes.ZhHant));
        Assert.StartsWith("凡人 0.5 · 練氣 40 · 築基 60 · ", ladder);
        Assert.EndsWith(" · 真仙 1.00k · 金仙 1.40k · 道祖 2.00k", ladder);
        Assert.Equal(13, ladder.Split(TitleCardStrip.Separator).Length);
    }

    [Fact]
    public void CardAccessibleNameJoinsTheStripWithTheListSeparator()
    {
        var en = For(LanguageCodes.En);
        Assert.Equal("Cultivation, such as MORTAL, NASCENT, MAHAYANA, DAO LORD",
            TitleSettingsText.CardAccessibleName("Cultivation", TitleSettingsText.StripTitles(Cultivation, en), en));
        var zh = For(LanguageCodes.ZhHant);
        Assert.Equal("修真境界，例如 凡人、元嬰、大乘、道祖",
            TitleSettingsText.CardAccessibleName("修真境界", TitleSettingsText.StripTitles(Cultivation, zh), zh));
    }

    [Theory]
    [InlineData(0, 2000, "")]
    [InlineData(1, 2000, "0.5")]
    [InlineData(2, 2000, "40")]
    [InlineData(11, 2000, "1.00k")]
    [InlineData(13, 2000, "2.00k")]
    [InlineData(2, 400, "8")]
    public void ThresholdIsTheLevelsEntrySpeed(int level, double redline, string expected)
    {
        Assert.Equal(expected, TitleSettingsText.Threshold(level, redline));
    }

    [Fact]
    public void RowsAreLabelledWithLevelThresholdAndBaseTitle()
    {
        var zh = For(LanguageCodes.ZhHant);
        Assert.Equal("第 4 級稱號，80 tok/s 以上；留空時為「金丹」", TitleSettingsText.RowAccessibleName(4, 2000, TitlePreset.Cultivation, zh));
        Assert.Equal("待機時的稱號；留空時為「閉關」", TitleSettingsText.RowAccessibleName(0, 2000, TitlePreset.Cultivation, zh));
        Assert.Equal("Level 13 title, from 2.00k tok/s; empty uses “10X DEV”",
            TitleSettingsText.RowAccessibleName(13, 2000, TitlePreset.Engineer, For(LanguageCodes.En)));
    }

    [Fact]
    public void BaseTextIsThePresetTitleInTheCurrentLanguage()
    {
        Assert.Equal("OFF DUTY", TitleSettingsText.BaseText(0, TitlePreset.Engineer, For(LanguageCodes.En)));
        Assert.Equal("步行", TitleSettingsText.BaseText(1, TitlePreset.Vehicles, For(LanguageCodes.ZhHant)));
        Assert.Equal("ロケット", TitleSettingsText.BaseText(13, TitlePreset.Vehicles, For(LanguageCodes.Ja)));
    }

    [Fact]
    public void LevelTitleClampsToTheLadder()
    {
        Assert.Equal(SpeedTitle.Idle, TitleSettingsText.LevelTitle(-1));
        Assert.Equal(SpeedTitle.Walk, TitleSettingsText.LevelTitle(1));
        Assert.Equal(SpeedTitle.Rocket, TitleSettingsText.LevelTitle(14));
    }
}
