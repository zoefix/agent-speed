using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests.Perf;

/// <summary>The card's value formats, tier words, chips and score words (DESIGN 12).</summary>
public sealed class PerfTextTests
{
    private static readonly Localization.Localizer Zh = PerfData.Loc();
    private static readonly Localization.Localizer En = PerfData.Loc(Localization.LanguageCodes.En);

    [Theory]
    [InlineData(292, "292 ms")]
    [InlineData(999.4, "999 ms")]
    [InlineData(999.6, "1.0 s")]
    [InlineData(5400, "5.4 s")]
    [InlineData(9960, "10 s")]
    [InlineData(59_600, "1.0 min")]
    [InlineData(600_000, "10 min")]
    public void TimesTakeTheirUnitFromTheRoundedValue(double ms, string text) =>
        Assert.Equal(text, PerfText.Measure(PerfComponentId.L, ms, En));

    [Theory]
    [InlineData(0.97, "97%")]
    [InlineData(0.996, "99%")]
    [InlineData(1, "100%")]
    [InlineData(0, "0%")]
    public void SharesNeverRoundUpToAHundredPercent(double share, string text) => Assert.Equal(text, PerfText.Percent(share));

    [Fact]
    public void ThroughputPaceAndUnits()
    {
        Assert.Equal("77 tok/s", PerfText.Measure(PerfComponentId.T, 77.2, En));
        Assert.Equal("3.5/min", PerfText.Measure(PerfComponentId.N, 3.46, En));
        Assert.Equal("12/min", PerfText.Measure(PerfComponentId.N, 12.4, En));
        Assert.Equal("3.5 次/分", PerfText.Measure(PerfComponentId.N, 3.5, Zh));
        Assert.Equal("292 毫秒", PerfText.Measure(PerfComponentId.U, 292, Zh));
    }

    [Theory]
    [InlineData(699, "perf.tier.slow")]
    [InlineData(700, "perf.tier.standard")]
    [InlineData(1299, "perf.tier.standard")]
    [InlineData(1300, "perf.tier.fast")]
    [InlineData(2000, "perf.tier.veryFast")]
    public void TierWords(int shown, string key) => Assert.Equal(key, PerfText.TierKey(shown));

    [Fact]
    public void RatiosAndSignedPercents()
    {
        Assert.Equal("1.21", PerfText.Ratio(1213));
        Assert.Equal("12.84", PerfText.Ratio(12_840));
        Assert.Equal("+8%", PerfText.SignedPercent(8));
        Assert.Equal("±0%", PerfText.SignedPercent(0));
        Assert.Equal("−12%", PerfText.SignedPercent(-12));
        Assert.Equal(new PerfChip("比平常 +8%", PerfChipTone.Mint), PerfText.Usual(8, Zh));
        Assert.Equal(PerfChipTone.Neutral, PerfText.Usual(0, Zh).Tone);
        Assert.Equal(PerfChipTone.Outlined, PerfText.Usual(null, Zh).Tone);
    }

    [Fact]
    public void ScoreWordsWrapInDesignOrder()
    {
        Assert.Equal("1,286", PerfText.Words(1286, false, PerfForm.Live, 1, Zh));
        Assert.Equal("912，初步（樣本少）", PerfText.Words(912, true, PerfForm.Live, 1, Zh));
        Assert.Equal("1,540，閒置中，2 個 agent 中最高", PerfText.Words(1540, false, PerfForm.Resting, 2, Zh));
    }
}
