using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Tests;

public class TitleThemeTests
{
    private static readonly StringTable Embedded = Localizer.LoadEmbeddedTable();

    private static Localizer For(string language) => new(Embedded, language);

    private static CustomTitleSet Set(string id = "A1", TitlePreset @base = TitlePreset.Cultivation, string idle = "", params string[] titles) =>
        new(id, "我的修仙路", @base, idle, titles);

    [Fact]
    public void PresetsAreInPickerOrderWithTheirIds()
    {
        Assert.Equal(["vehicles", "animals", "wind", "space", "cultivation", "wuxia", "mage", "gameRank", "engineer"],
            TitlePresets.All.Select(TitlePresets.Id));
        foreach (var preset in TitlePresets.All)
        {
            Assert.Equal(preset, TitlePresets.Parse(TitlePresets.Id(preset)));
        }

        Assert.Null(TitlePresets.Parse("Vehicles"));
        Assert.Null(TitlePresets.Parse("future"));
        Assert.Null(TitlePresets.Parse(null));
    }

    [Fact]
    public void VehiclesKeepTheirKeysAndOtherPresetsUseTitlesKeys()
    {
        Assert.Equal("tier.idle", TitlePresets.Key(TitlePreset.Vehicles, 0));
        Assert.Equal("title.walk", TitlePresets.Key(TitlePreset.Vehicles, 1));
        Assert.Equal("title.rocket", TitlePresets.Key(TitlePreset.Vehicles, 13));
        Assert.Equal("titles.cultivation.idle", TitlePresets.Key(TitlePreset.Cultivation, 0));
        Assert.Equal("titles.gameRank.13", TitlePresets.Key(TitlePreset.GameRank, 13));
        Assert.Equal("titles.wind.name", TitlePresets.NameKey(TitlePreset.Wind));
        Assert.True(TitlePresets.ShowsVehicleArt(TitlePreset.Vehicles));
        Assert.False(TitlePresets.ShowsVehicleArt(TitlePreset.Animals));
    }

    [Fact]
    public void EveryPresetHasANameAnIdleLabelAndThirteenUniqueTitlesInEveryLanguage()
    {
        foreach (var language in LanguageCodes.All)
        {
            var loc = For(language);
            foreach (var preset in TitlePresets.All)
            {
                var theme = TitleTheme.Of(preset);
                Assert.True(Embedded.Contains(TitlePresets.NameKey(preset)), $"{preset} name");
                var texts = Enumerable.Range(0, 14).Select(level => theme.Text(level, loc.Get)).ToArray();
                foreach (var (text, level) in texts.Select((t, i) => (t, i)))
                {
                    Assert.False(string.IsNullOrWhiteSpace(text), $"{preset} level {level} in {language}");
                    Assert.NotEqual(TitlePresets.Key(preset, level), text);
                    Assert.Equal(text.Trim(), text);
                }

                Assert.Equal(13, texts.Skip(1).Distinct(StringComparer.Ordinal).Count());
                Assert.DoesNotContain(texts[0], texts.Skip(1));
            }
        }
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, TitlePreset.Cultivation, 1, "凡人")]
    [InlineData(LanguageCodes.ZhHant, TitlePreset.Wuxia, 0, "打坐")]
    [InlineData(LanguageCodes.En, TitlePreset.Space, 9, "PULSAR")]
    [InlineData(LanguageCodes.En, TitlePreset.Wind, 0, "BECALMED")]
    [InlineData(LanguageCodes.En, TitlePreset.Engineer, 13, "10X DEV")]
    [InlineData(LanguageCodes.ZhHant, TitlePreset.Vehicles, 13, "火箭")]
    [InlineData(LanguageCodes.ZhHant, TitlePreset.Vehicles, 0, "待機")]
    public void PresetTextsComeFromTheStringTable(string language, TitlePreset preset, int level, string expected)
    {
        Assert.Equal(expected, TitleTheme.Of(preset).Text(level, For(language).Get));
    }

    [Fact]
    public void ACustomSetUsesItsFilledFieldsAndTheBaseElsewhereInEveryLanguage()
    {
        var theme = TitleTheme.Of(Set(idle: "", titles: ["", "", "", "結丹"]));
        var zh = For(LanguageCodes.ZhHant);
        var en = For(LanguageCodes.En);
        Assert.Equal("結丹", theme.Text(SpeedTitle.Motorcycle, zh));
        Assert.Equal("結丹", theme.Text(SpeedTitle.Motorcycle, en));
        Assert.Equal("凡人", theme.Text(SpeedTitle.Walk, zh));
        Assert.Equal("MORTAL", theme.Text(SpeedTitle.Walk, en));
        Assert.Equal("閉關", theme.Text(SpeedTitle.Idle, zh));
        Assert.Equal(TitlePreset.Cultivation, theme.Base);
        Assert.False(theme.ShowsVehicleArt);
        Assert.True(TitleTheme.Of(Set(@base: TitlePreset.Vehicles)).ShowsVehicleArt);
    }

    [Fact]
    public void LabelsUppercaseLatinOnlyInEnglishAndCustomTextStaysAsTypedOtherwise()
    {
        var theme = TitleTheme.Of(Set(titles: ["Ascended one"]));
        Assert.Equal("ASCENDED ONE", theme.Label(SpeedTitle.Walk, For(LanguageCodes.En)));
        Assert.Equal("Ascended one", theme.Label(SpeedTitle.Walk, For(LanguageCodes.ZhHant)));
        Assert.Equal("Ascended one", theme.Text(SpeedTitle.Walk, For(LanguageCodes.En)));
        var labels = TitleTheme.Of(TitlePreset.Engineer).Labels(For(LanguageCodes.En));
        Assert.Equal(14, labels.Length);
        Assert.Equal("OFF DUTY", labels[0]);
        Assert.Equal("10X DEV", labels[13]);
    }

    [Fact]
    public void LadderNamesTitleCaseBuiltInEnglishAndKeepCustomTextAsTyped()
    {
        var en = For(LanguageCodes.En);
        var theme = TitleTheme.Of(Set(titles: ["", "", "", "my CORE"]));
        Assert.Equal("Gold Core", TitleLadderText.Name(SpeedTitle.Motorcycle, TitleTheme.Of(TitlePreset.Cultivation), en));
        Assert.Equal("my CORE", TitleLadderText.Name(SpeedTitle.Motorcycle, theme, en));
        Assert.Equal("Qi Refining", TitleLadderText.Name(SpeedTitle.Bicycle, theme, en));
        Assert.Equal("練氣", TitleLadderText.Name(SpeedTitle.Bicycle, theme, For(LanguageCodes.ZhHant)));
        Assert.Equal("Sports Car", TitleLadderText.Name(SpeedTitle.SportsCar, TitleTheme.Vehicles, en));
    }

    [Fact]
    public void NamesArePresetNamesOrLiteralCustomNames()
    {
        var zh = For(LanguageCodes.ZhHant);
        Assert.Equal("修真境界", TitleTheme.Of(TitlePreset.Cultivation).Name(zh));
        Assert.Equal("Cultivation", TitleTheme.Of(TitlePreset.Cultivation).Name(For(LanguageCodes.En)));
        Assert.Equal("我的修仙路", TitleTheme.Of(Set()).Name(For(LanguageCodes.En)));
        Assert.Equal("修真境界（自訂）", TitleTheme.Of(new CustomTitleSet("x", "   ", TitlePreset.Cultivation)).Name(zh));
    }

    [Fact]
    public void ResolutionFallsBackToVehiclesAndOrbsFallBackToGeneral()
    {
        var set = Set("C1");
        var doc = new TitleThemesDocument { Selected = "C1", Custom = [set] };
        Assert.Equal("C1", doc.General.Id);
        Assert.Equal("C1", doc.OrbTheme(null).Id);
        Assert.Equal("wind", doc.OrbTheme("wind").Id);
        Assert.Equal("C1", doc.OrbTheme("deleted-set").Id);
        Assert.Equal("C1", doc.OrbTheme("futurePreset").Id);
        Assert.Null(doc.Theme("deleted-set"));

        Assert.Equal("vehicles", (doc with { Selected = "deleted-set" }).General.Id);
        Assert.Equal("vehicles", TitleThemesDocument.Default.General.Id);
        Assert.Equal(10, doc.AllThemes.Count);
        Assert.Equal("C1", doc.AllThemes[^1].Id);
    }

    [Fact]
    public void ValidationDropsDuplicateAndPresetIdsAndKeepsEightSets()
    {
        var sets = Enumerable.Range(0, 11).Select(i => Set($"S{i % 10}")).Append(Set("wind")).Append(Set("  ")).ToArray();
        var doc = new TitleThemesDocument { Selected = "  ", Custom = [.. sets] }.Validated();
        Assert.Equal(8, doc.Custom.Length);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"S{i}"), doc.Custom.Select(s => s.Id));
        Assert.Equal("vehicles", doc.Selected);
        Assert.False(doc.CanAddCustom);
    }

    [Fact]
    public void CustomSetsAlwaysHoldThirteenNormalizedTitles()
    {
        var set = new CustomTitleSet("x", " 名\t稱 ", TitlePreset.Space, " 休\u3000眠 ", Enumerable.Repeat(" a  b ", 20));
        Assert.Equal("名 稱", set.Name);
        Assert.Equal("休 眠", set.Idle);
        Assert.Equal(13, set.Titles.Length);
        Assert.All(set.Titles, t => Assert.Equal("a b", t));
        Assert.Equal(13, new CustomTitleSet("y", "n", TitlePreset.Space, null, ["x"]).Titles.Length);
        Assert.Equal("x", new CustomTitleSet("y", "n", TitlePreset.Space, null, ["x"]).Field(1));
        Assert.Equal(string.Empty, new CustomTitleSet("y", "n", TitlePreset.Space, null, ["x"]).Field(13));
        Assert.Equal(new CustomTitleSet("y", "n", TitlePreset.Space, null, ["x"]), new CustomTitleSet("y", "n", TitlePreset.Space, "", ["x", ""]));
    }
}
