using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Orb.Names;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

public class NamePlateTextTests
{
    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);
    private static readonly Localizer ZhHant = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);

    private static OrbDefinition Orb(params SourceKind[] sources) =>
        new("o") { Sources = sources.Length == 0 ? OrbSources.All : OrbSources.Of(SourceSet.Create(sources)) };

    private static string PlateText(OrbDefinition orb, double size, double budget, Localizer? localizer = null) =>
        NamePlateContent.Plate(orb, SourceAvailability.All, localizer ?? En, new NamePlateMetrics(size), budget, PlateMeasure.Measure)
            .Line.Runs[0].Text;

    private static double PlateWidth(OrbDefinition orb, double size, double budget = 10_000) =>
        NamePlateContent.Plate(orb, SourceAvailability.All, En, new NamePlateMetrics(size), budget, PlateMeasure.Measure).Width;

    [Theory]
    [InlineData(112, 171)]
    [InlineData(148, 189)]
    [InlineData(196, 222)]
    public void APairNeedsAboutTheWidthOfTheSpec(double size, double expected)
    {
        // The stand-in font lands within 4 % of the prototype's measurements.
        Assert.Equal(expected, PlateWidth(Orb(SourceKind.ClaudeCode, SourceKind.ClaudeDesktop), size), expected * 0.04);
    }

    [Theory]
    [InlineData(112, 118)]
    [InlineData(148, 154)]
    [InlineData(196, 202)]
    public void InATightRowAPairShortensToTheFirstPlusOne(double size, double rowBudget)
    {
        // Alone, the pair fits at 148 and 196 (1.5 S) but not at 112, where it needs about 171 pt of 168.
        var pair = Orb(SourceKind.ClaudeCode, SourceKind.ClaudeDesktop);
        Assert.Equal(size > 112 ? "Claude Code + Claude Desktop" : "Claude Code +1", PlateText(pair, size, new NamePlateMetrics(size).AloneBudget));
        Assert.Equal("Claude Code +1", PlateText(pair, size, rowBudget));
        Assert.True(PlateWidth(pair, size, rowBudget) <= rowBudget);
    }

    [Fact]
    public void APairThenEllipsizesItsFirstName()
    {
        var pair = Orb(SourceKind.ClaudeCode, SourceKind.ClaudeDesktop);
        var text = PlateText(pair, 112, 80);
        Assert.EndsWith("… +1", text, StringComparison.Ordinal);
        Assert.StartsWith(text[..text.IndexOf('…', StringComparison.Ordinal)], "Claude Code", StringComparison.Ordinal);
        Assert.True(PlateWidth(pair, 112, 80) <= 80);
    }

    [Fact]
    public void ThreeOrMoreSourcesEllipsizeTheFirstAndKeepTheCount()
    {
        var three = Orb(SourceKind.ClaudeDesktop, SourceKind.DeepSeek, SourceKind.Gemini);
        Assert.Equal("Claude Desktop +2", PlateText(three, 148, 222));
        var text = PlateText(three, 148, 95);
        Assert.Matches("^Claude( D[a-z]*)?… \\+2$", text);
        Assert.True(PlateWidth(three, 148, 95) <= 95);
    }

    [Fact]
    public void OneSourceAllSourcesAndCustomNamesGetATailEllipsis()
    {
        Assert.Equal("Claude Desktop", PlateText(Orb(SourceKind.ClaudeDesktop), 112, 168));
        Assert.Matches("^Claude D?e?s?k?…$", PlateText(Orb(SourceKind.ClaudeDesktop), 112, 70));
        Assert.Matches("^All S?o?u?r?…$", PlateText(Orb(), 112, 55));
        Assert.Equal("全部來源", PlateText(Orb(), 112, 168, ZhHant));

        var custom = Orb(SourceKind.Codex) with { Name = "Codex nightly refactor and dependency upgrades" };
        var text = PlateText(custom, 112, 168);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
        Assert.DoesNotContain("+", text, StringComparison.Ordinal);
        Assert.True(PlateWidth(custom, 112, 168) <= 168);
    }

    [Fact]
    public void TheEllipsisDropsTrailingSpaces()
    {
        var custom = Orb(SourceKind.Codex) with { Name = "ab cdefghijklmnopqrstuvwxyz" };
        var name = NamePlateName.For(custom, En);
        var fit = name.Fit(3.5 * 10 * 0.57 + 0.01, t => PlateMeasure.Measure(t, 10));
        Assert.Equal("ab…", fit.Text);
        Assert.True(fit.Shortened);
    }

    [Theory]
    [InlineData("夜間批次重構任務與相依套件")]
    [InlineData("🧑‍💻🧑‍💻🧑‍💻 pair programming session")]
    [InlineData("🇹🇼🇯🇵🇺🇸🇩🇪🇫🇷🇰🇷🇬🇧🇮🇹")]
    [InlineData("éééééééééééé")]
    [InlineData("コードレビューと夜間ビルドの確認作業")]
    public void TheEllipsisFallsOnAGraphemeBoundary(string custom)
    {
        var orb = Orb(SourceKind.Codex) with { Name = custom };
        foreach (var budget in new[] { 40.0, 60, 80, 100 })
        {
            var text = PlateText(orb, 112, budget);
            if (text == custom)
            {
                continue;
            }

            Assert.EndsWith("…", text, StringComparison.Ordinal);
            var kept = text[..^1];
            Assert.StartsWith(kept, custom, StringComparison.Ordinal);
            Assert.Contains(kept.Length, StringInfo.ParseCombiningCharacters(custom));
            Assert.True(new StringInfo(kept).LengthInTextElements >= 1);
        }
    }

    [Fact]
    public void ATinyBudgetStillKeepsOneGrapheme()
    {
        var orb = Orb(SourceKind.Codex) with { Name = "🧑‍💻 session" };
        Assert.Equal("🧑‍💻…", PlateText(orb, 112, 1));
    }

    [Fact]
    public void CjkNamesTrackWider()
    {
        Assert.False(NamePlateScript.HasCjk("Claude Code"));
        Assert.True(NamePlateScript.HasCjk("夜間批次"));
        Assert.True(NamePlateScript.HasCjk("コードレビュー"));
        Assert.True(NamePlateScript.HasCjk("Codex 與 Claude"));
        Assert.True(NamePlateScript.HasCjk("한국어"));
        Assert.True(NamePlateScript.HasCjk("𠮷野家"));

        var m = new NamePlateMetrics(148);
        var latin = NamePlateLayout.TextWidth("abcd", false, m, PlateMeasure.Measure);
        var cjk = NamePlateLayout.TextWidth("abcd", true, m, PlateMeasure.Measure);
        Assert.Equal(3 * (0.4 - 0.12), cjk - latin, 9);

        var name = NamePlateName.For(Orb(SourceKind.Codex) with { Name = "Codex 夜間重構批次任務" }, En);
        Assert.True(name.IsCjk);
        var plate = NamePlateContent.Plate(Orb(SourceKind.Codex) with { Name = "Codex 夜間重構批次任務" }, SourceAvailability.All, En, m, 60,
            PlateMeasure.Measure);
        Assert.True(plate.Line.Runs[0].Cjk);
    }

    [Fact]
    public void JoinedScriptsAreNotLetterSpaced()
    {
        Assert.True(NamePlateScript.AllowsTracking("Claude 夜間"));
        Assert.False(NamePlateScript.AllowsTracking("مرحبا"));
        Assert.False(NamePlateScript.AllowsTracking("नमस्ते"));
        Assert.Equal(10, NamePlateScript.TrackedWidth("مرحبا", 10, 1));
        Assert.Equal(14, NamePlateScript.TrackedWidth("abcde", 10, 1));
    }

    [Fact]
    public void DotsFollowTheSources()
    {
        var available = new SourceAvailability(SourceSet.Create([SourceKind.Codex, SourceKind.ClaudeCode]), SourceSet.Empty);
        Assert.Equal([NamePlateDot.Spectrum], NamePlateContent.Dots(OrbSources.All, available).ToArray());
        Assert.Equal(
            [
                new NamePlateDot(NamePlateDotKind.Source, SourceKind.Codex),
                new NamePlateDot(NamePlateDotKind.Source, SourceKind.ClaudeCode),
                new NamePlateDot(NamePlateDotKind.Hollow, SourceKind.Gemini),
            ],
            NamePlateContent.Dots(
                OrbSources.Of(SourceSet.Create([SourceKind.Gemini, SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Omp])), available).ToArray());
    }

    [Fact]
    public void TheChipShowsEverySourceWithItsStatus()
    {
        var availability = new SourceAvailability(SourceSet.Of(SourceKind.ClaudeCode), SourceSet.Of(SourceKind.Codex));
        var chip = NamePlateContent.Chip(Orb(SourceKind.ClaudeCode, SourceKind.Gemini), availability, ZhHant);
        Assert.Equal(2, chip.Runs.Length);
        Assert.Equal("Claude Code", chip.Runs[0].Text);
        Assert.Equal(NamePlateInk.Primary, chip.Runs[0].Ink);
        Assert.Equal([new NamePlateDot(NamePlateDotKind.Source, SourceKind.ClaudeCode)], chip.Runs[0].Dots.ToArray());
        Assert.Equal("Gemini CLI · 未偵測到", chip.Runs[1].Text);
        Assert.Equal(NamePlateInk.Secondary, chip.Runs[1].Ink);
        Assert.Equal([new NamePlateDot(NamePlateDotKind.Hollow, SourceKind.Gemini)], chip.Runs[1].Dots.ToArray());

        var disabled = NamePlateContent.Chip(Orb(SourceKind.Codex), availability, En);
        Assert.Equal("Codex · " + En["source.disabled"], Assert.Single(disabled.Runs).Text);

        var three = NamePlateContent.Chip(Orb(SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Gemini), SourceAvailability.All, En);
        Assert.Equal("Codex +2", Assert.Single(three.Runs).Text);

        var all = NamePlateContent.Chip(Orb(), SourceAvailability.All, En);
        Assert.Equal("All Sources", Assert.Single(all.Runs).Text);
        Assert.Equal([NamePlateDot.Spectrum], all.Runs[0].Dots.ToArray());
    }

    [Fact]
    public void TheChipMovesNothingWhenItShowsWhatThePlateShows()
    {
        var m = new NamePlateMetrics(148);
        NamePlateLine Plate(OrbDefinition orb) => NamePlateContent.Plate(orb, SourceAvailability.All, En, m, 222, PlateMeasure.Measure).Line;
        NamePlateLine Chip(OrbDefinition orb) => NamePlateContent.Chip(orb, SourceAvailability.All, En);

        var codex = Orb(SourceKind.Codex);
        Assert.False(NamePlateContent.ChipDiffers(Plate(codex), Chip(codex)));
        Assert.False(NamePlateContent.ChipDiffers(Plate(Orb()), Chip(Orb())));
        Assert.True(NamePlateContent.ChipDiffers(Plate(codex with { Name = "夜間批次" }), Chip(codex with { Name = "夜間批次" })));
        var pair = Orb(SourceKind.Codex, SourceKind.ClaudeCode);
        Assert.True(NamePlateContent.ChipDiffers(Plate(pair), Chip(pair)));
    }

    [Fact]
    public void TheUndoChipEndsWithTheAccentAction()
    {
        var line = NamePlateContent.Undo(ZhHant.Format("orbs.didSplit", 3), ZhHant);
        Assert.Equal(["已拆分成 3 個懸浮窗", "復原"], line.Runs.Select(r => r.Text));
        Assert.Equal([NamePlateInk.Primary, NamePlateInk.Accent], line.Runs.Select(r => r.Ink));
        Assert.All(line.Runs, r => Assert.Empty(r.Dots));
    }

    [Fact]
    public void NamesFollowTheAutomaticNaming()
    {
        Assert.Equal("Claude Code + Claude Desktop", NamePlateName.For(Orb(SourceKind.ClaudeCode, SourceKind.ClaudeDesktop), En).Text);
        Assert.Equal("Claude Code", NamePlateName.For(Orb(SourceKind.ClaudeCode, SourceKind.ClaudeDesktop), En).Head);
        Assert.Equal(1, NamePlateName.For(Orb(SourceKind.ClaudeCode, SourceKind.ClaudeDesktop), En).More);
        Assert.Null(NamePlateName.For(Orb(SourceKind.ClaudeCode), En).Head);
        Assert.Null(NamePlateName.For(Orb(SourceKind.ClaudeCode, SourceKind.Codex) with { Name = "Mine" }, En).Head);
        Assert.Equal("Mine", NamePlateName.For(Orb(SourceKind.ClaudeCode, SourceKind.Codex) with { Name = "Mine" }, En).Text);
    }
}
