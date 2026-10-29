using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Work;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb.Lines;
using AgentSpeed.Presentation.Perf;

namespace AgentSpeed.Presentation.Tests;

/// <summary>The 性能 and work-signal wording of orbs and agent rows (DESIGN 4, 10, 12).</summary>
public class IntelligenceTextTests
{
    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);
    private static readonly Localizer ZhHant = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);

    [Fact]
    public void TheOrbTooltipAndLabelReadTheScoreAndThinking()
    {
        var sums = new IntelligenceSums(Output: 1200, Thinking: 528, ThinkingBasis: 1200) { Score = TestSums.Live(1286.25) };
        var content = OrbLinesText.Intelligence(PerfSetReading.Of(sums, []), sums, generating: false, ZhHant).Content!;
        Assert.Equal("1000 為參考水準，沒有上限", content.Detail);
        Assert.Equal("性能 1,286，思考每秒 176 個 token", content.Spoken);
        Assert.Equal("1,286", content.First.Number);
    }

    [Fact]
    public void ASetWithoutAScoreShowsADashOnlyWhileItsAgentsGenerate()
    {
        var sums = new IntelligenceSums(Output: 1200, Thinking: 528, ThinkingBasis: 1200);
        var generating = OrbLinesText.Intelligence(PerfSetReading.None, sums, generating: true, En).Content!;
        Assert.Equal(("–", "176"), (generating.First.Number, generating.Second.Number));
        Assert.Equal("performance: no score yet, thinking 176 tokens per second", generating.Spoken);
        Assert.Null(OrbLinesText.Intelligence(PerfSetReading.None, sums, generating: false, En).Content);
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "性能 912，初步（樣本少），3 個 agent 中最高", "性能 1,540，閒置中")]
    [InlineData(LanguageCodes.ZhHans, "性能 912，初步（样本少），3 个 agent 中最高", "性能 1,540，空闲中")]
    [InlineData(LanguageCodes.En, "performance 912, provisional (few samples), the highest of 3 agents", "performance 1,540, idle")]
    [InlineData(LanguageCodes.Ja, "性能 912、暫定（サンプルが少ない）、3 個の agent で最高", "性能 1,540、待機中")]
    public void TheOrbsWordsCarryTheProvisionalAndTheRestingForm(string language, string provisional, string resting)
    {
        var localizer = new Localizer(Localizer.LoadEmbeddedTable(), language);
        var score = new PerfSetReading(PerfForm.Live, 912, true, 3, null, null);
        Assert.Equal(provisional, OrbLinesText.Intelligence(score, IntelligenceSums.Zero, true, localizer).Content!.SpokenAlone);
        var pale = new PerfSetReading(PerfForm.Resting, 1540, false, 1, null, null);
        Assert.Equal(resting, OrbLinesText.Intelligence(pale, IntelligenceSums.Zero, false, localizer).Content!.SpokenAlone);
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "性能", "思考")]
    [InlineData(LanguageCodes.ZhHans, "性能", "思考")]
    [InlineData(LanguageCodes.En, "Perf", "TH")]
    [InlineData(LanguageCodes.Ja, "性能", "思考")]
    public void TheOrbReadoutsGlyphsNamePerformanceAndThinking(string language, string intelligence, string thinking)
    {
        var localizer = new Localizer(Localizer.LoadEmbeddedTable(), language);
        var sums = new IntelligenceSums(Output: 300) { Score = TestSums.Live(12_840) };
        var content = OrbLinesText.Intelligence(PerfSetReading.Of(sums, []), sums, generating: true, localizer).Content!;
        Assert.Equal((intelligence, "12.8k", string.Empty), (content.First.Glyph, content.First.Number, content.First.Unit));
        Assert.Equal((thinking, localizer["unit.tokS"]), (content.Second.Glyph, content.Second.Unit));
    }

    [Fact]
    public void LoopingLeadsTheTooltipAndTheSummaryClosesIt()
    {
        var looping = new WorkSignals(Looping: 3, Repeating: null, Novelty: 0.5, Failed: 4, Recovered: 1);
        Assert.Equal(En.Format("work.looping", 3), WorkText.Looping(looping, En));
        Assert.Equal([En.Format("work.summary", "50%", 4, 1)], WorkText.Closing(looping, En));

        var waiting = new WorkSignals(Looping: null, Repeating: 4, Novelty: null, Failed: 0, Recovered: 0);
        Assert.Null(WorkText.Looping(waiting, En));
        Assert.Equal([En.Format("work.repeating", 4)], WorkText.Closing(waiting, En));
        Assert.Empty(WorkText.Closing(WorkSignals.None, En));
    }

}
