using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Modes;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

public sealed class EffortCapsuleTests
{
    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);

    /// <summary>Six DIP per character, so widths are easy to read.</summary>
    private static double Word(string word) => word.Length * 6;

    private static ExecutionModes Modes(string? effort, string? tier = null) =>
        ExecutionModes.Of(ExecutionModeNative.Effort(effort, ExecutionModeEvidence.Recorded, ExecutionModeScope.Turn))
            .Merge(ExecutionModeNative.OpenAiTier(tier, ExecutionModeEvidence.Selected, ExecutionModeScope.Session));

    [Fact]
    public void LevelTopRungAndBoltsFollowTheSharedTables()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(RepositoryPaths.Spec("fixtures/execution-mode-cases.json")));
        foreach (var item in document.RootElement.GetProperty("effortDisplay").EnumerateArray())
        {
            var capsule = new EffortCapsule(item.GetProperty("value").GetString(), null);
            var expected = item.GetProperty("expected");
            Assert.Equal(expected.GetProperty("level").ValueKind == JsonValueKind.Null ? null : expected.GetProperty("level").GetInt32(), capsule.Level);
            Assert.Equal(expected.GetProperty("top").GetBoolean(), capsule.IsTopRung);
        }

        foreach (var item in document.RootElement.GetProperty("speedBolts").EnumerateArray())
        {
            Assert.Equal(item.GetProperty("bolts").GetInt32(), new EffortCapsule(null, item.GetProperty("value").GetString()).Bolts);
        }
    }

    [Fact]
    public void TonesSeparateNeutralMaxAndTheTopRung()
    {
        Assert.Equal(EffortTone.Neutral, new EffortCapsule("xhigh", null).Tone);
        Assert.Equal(EffortTone.Max, new EffortCapsule("max", null).Tone);
        Assert.Equal(EffortTone.TopRung, new EffortCapsule("ultra", null).Tone);
        Assert.Equal(EffortTone.TopRung, new EffortCapsule("ultracode", null).Tone);
        Assert.Equal(EffortTone.Neutral, new EffortCapsule("persistent", null).Tone);
    }

    [Fact]
    public void ACapsuleShowsForAKnownEffortOrBoltsAndNeverForUnknownOrStandard()
    {
        Assert.False(new EffortCapsule(null, null).IsVisible);
        Assert.False(new EffortCapsule(null, "standard").IsVisible);
        Assert.False(new EffortCapsule(null, "flex").IsVisible);
        Assert.True(new EffortCapsule(null, "standard").IsKnown);
        Assert.True(new EffortCapsule(null, "fast").IsVisible);
        Assert.True(new EffortCapsule("off", null).IsVisible);
        Assert.Equal(new EffortCapsule("xhigh", "ultrafast"), EffortCapsule.Of(Modes("xhigh", "ultrafast")));
        Assert.Equal(new EffortCapsule(null, null), EffortCapsule.Of(ExecutionModes.Empty));
    }

    [Fact]
    public void WidthAddsPaddingMeterWordAndBoltsWithFivePointGaps()
    {
        var meter = EffortCapsule.MeterWidth;
        Assert.Equal(18.5, meter);
        Assert.Equal(12 + meter + 5 + 30, new EffortCapsule("xhigh", null).Width(compact: false, Word));
        Assert.Equal(12 + meter, new EffortCapsule("xhigh", null).Width(compact: true, Word));
        Assert.Equal(12 + meter + 5 + 30 + 5 + 15, new EffortCapsule("xhigh", "ultrafast").Width(compact: false, Word));
        Assert.Equal(12 + 9, new EffortCapsule(null, "fast").Width(compact: false, Word));
        Assert.Equal(0, new EffortCapsule(null, "standard").Width(compact: false, Word));
        Assert.Equal(15, EffortCapsule.BoltsWidth(2));
    }

    [Fact]
    public void CompactDropsTheWordWholeUnlessItIsAllTheCapsuleShows()
    {
        Assert.False(new EffortCapsule("max", null).ShowsWord(compact: true));
        Assert.True(new EffortCapsule("max", null).ShowsWord(compact: false));
        Assert.True(new EffortCapsule("persistent", null).ShowsWord(compact: true));
        Assert.False(new EffortCapsule("persistent", "fast").ShowsWord(compact: true));
        Assert.False(new EffortCapsule(null, "fast").ShowsWord(compact: false));
    }

    [Fact]
    public void TheColumnTakesTheWidestCapsuleAndDropsEveryWordBelowSeventyTwoPointsOfName()
    {
        EffortCapsule[] rows = [new("xhigh", null), new(null, null), new("max", "fast")];
        var full = 12 + 18.5 + 5 + 18 + 5 + 9;
        var wide = EffortCapsuleColumn.For(rows, nameRoom: 300, Word);
        Assert.Equal(new EffortCapsuleColumn(full, Compact: false), wide);
        Assert.Equal(full + 8, wide.Footprint);
        Assert.Equal(new EffortCapsuleColumn(full, Compact: false), EffortCapsuleColumn.For(rows, nameRoom: 72 + full + 8, Word));
        var narrow = EffortCapsuleColumn.For(rows, nameRoom: 72 + full + 7, Word);
        Assert.Equal(new EffortCapsuleColumn(12 + 18.5 + 5 + 9, Compact: true), narrow);
        Assert.Equal(EffortCapsuleColumn.None, EffortCapsuleColumn.For([new(null, "standard"), new(null, null)], nameRoom: 10, Word) with { Compact = false });
        Assert.Equal(0, EffortCapsuleColumn.For([], nameRoom: 300, Word).Footprint);
    }

    [Fact]
    public void TooltipSpellsTokensAndExplainsTheTopRung()
    {
        Assert.Equal(["Reasoning effort: xhigh", "Speed: Ultrafast"], new EffortCapsule("xhigh", "ultrafast").TooltipLines(En));
        Assert.Equal(["Reasoning effort: Not recorded", "Speed: flex"], new EffortCapsule(null, "flex").TooltipLines(En));
        Assert.Equal(["Reasoning effort: ultra", "Speed: Not recorded", En["effort.ultra.detail"]], new EffortCapsule("ultra", null).TooltipLines(En));
        Assert.Equal(En["effort.ultracode.detail"], new EffortCapsule("ultracode", "standard").TooltipLines(En)[2]);
    }

    [Fact]
    public void AnAgentsCurrentValuesReachTheCapsuleWithTheirForm()
    {
        var recorded = Modes("high", null);
        var session = ExecutionModes.Of(ExecutionModeNative.Effort("medium", ExecutionModeEvidence.Selected, ExecutionModeScope.Session));
        var effort = ExecutionModes.Of(ExecutionModeNative.Effort("max", ExecutionModeEvidence.Configured, ExecutionModeScope.Configuration));
        var tier = ExecutionModeNative.OpenAiTier("priority", ExecutionModeEvidence.Configured, ExecutionModeScope.Configuration);
        var configured = effort.Merge(tier);

        Assert.Equal(new EffortCapsule("high", "fast", SpeedConfigured: true), EffortCapsule.Of(Agent() with { Modes = recorded, ConfiguredModes = configured }));
        Assert.Equal(new EffortCapsule("medium", "fast", SpeedConfigured: true), EffortCapsule.Of(Agent() with { SessionModes = session, ConfiguredModes = configured }));
        Assert.Equal(new EffortCapsule("max", "fast", true, true), EffortCapsule.Of(Agent() with { ConfiguredModes = configured }));
        Assert.Equal(new EffortCapsule(null, null), EffortCapsule.Of(Agent()));
    }

    [Fact]
    public void OnlyACapsuleOfConfiguredValuesTakesTheDashedForm()
    {
        Assert.True(new EffortCapsule("high", null, EffortConfigured: true).IsConfigured);
        Assert.True(new EffortCapsule(null, "fast", SpeedConfigured: true).IsConfigured);
        Assert.True(new EffortCapsule("high", "fast", true, true).IsConfigured);
        Assert.False(new EffortCapsule("high", "fast", EffortConfigured: true).IsConfigured);
        Assert.False(new EffortCapsule("high", "standard", SpeedConfigured: true).IsConfigured);
        Assert.False(new EffortCapsule("high", null).IsConfigured);
        Assert.False(new EffortCapsule(null, null).IsConfigured);
    }

    [Theory]
    [InlineData("max")]
    [InlineData("ultra")]
    [InlineData("ultracode")]
    public void NothingConfirmsAConfiguredTopSoItGetsNoHeatOrShimmer(string effort)
    {
        var capsule = new EffortCapsule(effort, null, EffortConfigured: true);
        Assert.Equal(EffortTone.Configured, capsule.Tone);
        Assert.False(capsule.IsTopRung);
        Assert.Equal(5, capsule.Level);
        Assert.Single(capsule.TooltipLines(En), l => l == En["modes.configured.note"]);
        Assert.DoesNotContain(capsule.TooltipLines(En), l => l == En["effort.ultra.detail"] || l == En["effort.ultracode.detail"]);
    }

    [Fact]
    public void TheMarkFollowsTheWordAndDropsWithIt()
    {
        var capsule = new EffortCapsule("xhigh", null, EffortConfigured: true);
        Assert.True(capsule.ShowsMark(compact: false));
        Assert.False(capsule.ShowsMark(compact: true));
        Assert.False(new EffortCapsule("xhigh", "fast", EffortConfigured: true).ShowsMark(compact: false));
        Assert.True(new EffortCapsule(null, "fast", SpeedConfigured: true).ShowsMark(compact: false));
        Assert.False(new EffortCapsule(null, "fast", SpeedConfigured: true).ShowsMark(compact: true));
        Assert.Equal(12 + 18.5 + 5 + 30 + 5 + 21, capsule.Width(compact: false, Word, 20.5));
        Assert.Equal(12 + 18.5, capsule.Width(compact: true, Word, 20.5));
        Assert.Equal(12 + 18.5 + 5 + 30, new EffortCapsule("xhigh", null).Width(compact: false, Word, 20.5));
        Assert.Equal(12 + 18.5 + 5 + 30 + 5 + 21 + 5 + 9, new EffortCapsule("xhigh", "fast", true, true).Width(compact: false, Word, 20.5));

        var full = 12 + 18.5 + 5 + 30 + 5 + 21;
        var column = EffortCapsuleColumn.For([capsule, new("low", null)], nameRoom: 72 + full + 8, Word, 20.5);
        Assert.Equal(new EffortCapsuleColumn(full, Compact: false), column);
        Assert.True(EffortCapsuleColumn.For([capsule], nameRoom: 72 + full + 7, Word, 20.5).Compact);
    }

    [Fact]
    public void AConfiguredValueSaysSoAndOneNoteFollowsTheLines()
    {
        Assert.Equal(["Reasoning effort: high (configured)", "Speed: Not recorded", En["modes.configured.note"]],
            new EffortCapsule("high", null, EffortConfigured: true).TooltipLines(En));
        Assert.Equal(["Reasoning effort: low", "Speed: Fast (configured)", En["modes.configured.note"]],
            new EffortCapsule("low", "fast", SpeedConfigured: true).TooltipLines(En));
        Assert.Equal(["Reasoning effort: max (configured)", "Speed: Ultrafast (configured)", En["modes.configured.note"]],
            new EffortCapsule("max", "ultrafast", true, true).TooltipLines(En));
        Assert.Equal(["Reasoning effort: ultra", "Speed: Fast (configured)", En["effort.ultra.detail"], En["modes.configured.note"]],
            new EffortCapsule("ultra", "fast", SpeedConfigured: true).TooltipLines(En));
    }

    [Fact]
    public void TheRowsWordsAnnounceAConfiguredValueWithoutTheNote()
    {
        var capsule = new EffortCapsule("high", "fast", true, true);
        Assert.Equal("Reasoning effort: high (configured), Speed: Fast (configured)", capsule.AccessibilityText(En));
        var zh = new Localizer(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);
        Assert.Equal("推理強度：high（設定值），加速：Fast（設定值）", capsule.AccessibilityText(zh));
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "設定值")]
    [InlineData(LanguageCodes.ZhHans, "配置值")]
    [InlineData(LanguageCodes.En, "config")]
    [InlineData(LanguageCodes.Ja, "設定値")]
    public void TheMarkExistsInEveryLanguage(string language, string mark)
    {
        var loc = new Localizer(Localizer.LoadEmbeddedTable(), language);
        Assert.Equal(mark, loc["mode.configured.mark"]);
        Assert.False(string.IsNullOrWhiteSpace(loc["modes.configured.note"]));
    }

    [Fact]
    public void TheNoteStaysOnTheCardAndOutOfWhatTheRowSays()
    {
        var lines = new EffortCapsule("high", null, EffortConfigured: true).TooltipLines(En);
        var card = AgentSpeed.Presentation.Perf.PerfCard.Build(new AgentSpeed.Presentation.Perf.PerfCardSubject(SourceKind.Codex, "p", "m", null, lines), En);
        Assert.Contains(En["modes.configured.note"], card.Facts);
        Assert.DoesNotContain(En["modes.configured.note"], card.Description, StringComparison.Ordinal);
        Assert.Contains("Reasoning effort: high (configured)", card.Description, StringComparison.Ordinal);
    }

    private static AgentStatus Agent() => new("a", SourceKind.Codex, "p", "m", AgentPhase.Idle, false, default, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void AccessibilityJoinsTheTwoLinesPerLanguage()
    {
        var capsule = new EffortCapsule("ultracode", "fast");
        Assert.Equal("Reasoning effort: ultracode, Speed: Fast", capsule.AccessibilityText(En));
        var zh = new Localizer(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);
        Assert.Equal("推理強度：ultracode，加速：Fast", capsule.AccessibilityText(zh));
    }
}
