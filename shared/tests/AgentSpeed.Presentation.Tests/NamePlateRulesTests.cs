using AgentSpeed.Core;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Names;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests;

public class NamePlateRulesTests
{
    private static OrbDefinition All(string id) => new(id);

    private static OrbDefinition Subset(string id, params SourceKind[] sources) => new(id) { Sources = OrbSources.Of(SourceSet.Create(sources)) };

    private static AppSettings With(OrbNamesMode mode, params OrbDefinition[] orbs) =>
        AppSettings.Default with { OrbNames = mode, Orbs = new OrbsDocument(orbs) };

    private static bool Shows(AppSettings settings, string id) => NamePlateRules.Shows(settings.Orbs.Find(id)!, settings);

    [Fact]
    public void AutoKeepsTheClassicSingleOrbExactlyAsBefore()
    {
        Assert.False(Shows(With(OrbNamesMode.Auto, All("a")), "a"));
        Assert.False(Shows(AppSettings.Default, AppSettings.Default.Orbs.Orbs[0].Id));
    }

    [Fact]
    public void AutoShowsASubsetOrbEvenAlone() => Assert.True(Shows(With(OrbNamesMode.Auto, Subset("a", SourceKind.Codex)), "a"));

    [Fact]
    public void AutoShowsEveryPlateWithTwoOrMoreVisibleOrbs()
    {
        var settings = With(OrbNamesMode.Auto, All("a"), All("b"));
        Assert.True(Shows(settings, "a"));
        Assert.True(Shows(settings, "b"));
    }

    [Fact]
    public void HiddenOrbsDoNotCountAndShowNoPlate()
    {
        var settings = With(OrbNamesMode.Auto, All("a"), All("b") with { Visible = false });
        Assert.False(Shows(settings, "a"));
        Assert.False(Shows(settings, "b"));
        Assert.Equal(1, NamePlateRules.VisibleCount(settings));

        // The menus count a hidden orb as shown: with it, two orbs would be visible.
        Assert.True(NamePlateRules.IsOn(settings.Orbs.Find("b")!, settings));
        Assert.False(NamePlateRules.IsOn(settings.Orbs.Find("a")!, settings));

        var off = With(OrbNamesMode.Always, All("a"), All("b")) with { ShowOrbs = false };
        Assert.False(Shows(off, "a"));
        Assert.Equal(0, NamePlateRules.VisibleCount(off));
    }

    [Fact]
    public void AlwaysAndNeverApplyToEveryOrb()
    {
        Assert.True(Shows(With(OrbNamesMode.Always, All("a")), "a"));
        Assert.False(Shows(With(OrbNamesMode.Never, Subset("a", SourceKind.Codex), All("b")), "a"));
        Assert.False(Shows(With(OrbNamesMode.Never, Subset("a", SourceKind.Codex), All("b")), "b"));
    }

    [Fact]
    public void AnOrbsOwnSettingOverridesTheGlobalOne()
    {
        Assert.True(Shows(With(OrbNamesMode.Never, All("a") with { ShowName = true }), "a"));
        Assert.False(Shows(With(OrbNamesMode.Always, All("a") with { ShowName = false }), "a"));
        Assert.False(Shows(With(OrbNamesMode.Auto, Subset("a", SourceKind.Codex) with { ShowName = false }, All("b")), "a"));
        Assert.True(Shows(With(OrbNamesMode.Auto, Subset("a", SourceKind.Codex) with { ShowName = false }, All("b")), "b"));
    }

    [Theory]
    [InlineData(64, false)]
    [InlineData(76, false)]
    [InlineData(80, true)]
    [InlineData(96, true)]
    public void NoPlateBelowEightyPoints(double size, bool shows)
    {
        var orb = new OrbDefinition("a") { Style = OrbStyle.Minimal, ShowName = true }.WithSize(size);
        Assert.Equal(shows, Shows(With(OrbNamesMode.Always, orb), "a"));
        Assert.Equal(shows, NamePlateRules.IsOn(orb, With(OrbNamesMode.Always, orb)));
    }

    [Fact]
    public void TheAutomaticResultIgnoresTheOverride()
    {
        var settings = With(OrbNamesMode.Auto, All("a") with { ShowName = true });
        Assert.False(NamePlateRules.AutomaticResult(settings.Orbs.Find("a")!, settings));
        Assert.True(NamePlateRules.IsOn(settings.Orbs.Find("a")!, settings));
        Assert.False(NamePlateRules.Toggled(settings.Orbs.Find("a")!, settings));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheMaterialKeepsItsTextReadable(bool dark)
    {
        var material = NamePlateMaterial.For(dark);
        foreach (var fill in new[] { material.FillTop, material.FillBottom })
        {
            Assert.True(Rgba.ContrastRatio(material.Text, fill) >= 7, $"text {Rgba.ContrastRatio(material.Text, fill):0.0}");
            Assert.True(Rgba.ContrastRatio(material.Secondary, fill) >= 4.5, $"secondary {Rgba.ContrastRatio(material.Secondary, fill):0.0}");
            Assert.True(Rgba.ContrastRatio(material.Accent, fill) >= 4.5, $"accent {Rgba.ContrastRatio(material.Accent, fill):0.0}");
        }
    }

    [Fact]
    public void TheSpectrumRunsThroughEverySourceInOrderFromTwelveOClock()
    {
        var kinds = Enum.GetValues<SourceKind>();
        for (var i = 0; i < kinds.Length; i++)
        {
            var expected = NamePlateMaterial.SourceColor(kinds[i], true);
            var actual = NamePlateMaterial.SpectrumAt((double)i / kinds.Length, true);
            Assert.Equal(expected.R, actual.R, 5);
            Assert.Equal(expected.G, actual.G, 5);
            Assert.Equal(expected.B, actual.B, 5);
        }

        Assert.Equal(NamePlateMaterial.SpectrumAt(0, false), NamePlateMaterial.SpectrumAt(1, false));
        Assert.Equal(OkLab.Darken(HeatPalette.SourceColor(SourceKind.Codex), 0.1), NamePlateMaterial.SourceColor(SourceKind.Codex, false));
    }
}
