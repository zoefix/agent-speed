using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

/// <summary>The taskbar intelligence block's thinking line hidden at zero (DESIGN 10, Menu bar and taskbar).</summary>
public class BarThinkingHoldTests
{
    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);
    private static readonly Localizer ZhHant = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);
    private static readonly AppSettings Chosen = AppSettings.Default with { BarIntelligence = true };

    /// <summary>125 over 0 tok/s.</summary>
    private static readonly IntelligenceSums Unthinking = TestSums.Of(300, 125, 0, 300);

    /// <summary>125 over 10 tok/s.</summary>
    private static readonly IntelligenceSums Thinking = TestSums.Of(300, 125, 30, 300);

    [Fact]
    public void TheHoldHidesARateThatReadsZeroForThreeSeconds()
    {
        var hold = new ThinkingZeroHold();
        Assert.False(hold.Hides(true, 10));
        Assert.False(hold.Hides(true, 12.9));
        Assert.True(hold.Hides(true, 13));
        Assert.True(hold.Hides(true, 20));

        // A rate above 0 shows at once, and the next zero holds 3 s again.
        Assert.False(hold.Hides(false, 20.5));
        Assert.False(hold.Hides(true, 21));
        Assert.False(hold.Hides(true, 23.9));
        Assert.True(hold.Hides(true, 24));
    }

    [Fact]
    public void LineTwoHidesAfterThreeSecondsOfAZeroRateUnderALiveValue()
    {
        var hold = new ThinkingZeroHold();
        var first = BarIntelligenceBlock.From(Chosen, Snapshot(Unthinking), ZhHant, hold, 100)!;
        Assert.True(first.ThinkingZero);
        Assert.False(first.ThinkingHidden);
        Assert.Equal("性能 125，思考每秒 0 個 token", first.Spoken);
        Assert.False(BarIntelligenceBlock.From(Chosen, Snapshot(Unthinking), ZhHant, hold, 102.5)!.ThinkingHidden);

        var hidden = BarIntelligenceBlock.From(Chosen, Snapshot(Unthinking), ZhHant, hold, 103)!;
        Assert.True(hidden.ThinkingHidden);
        Assert.Equal("性能 125", hidden.Spoken);
        Assert.Equal(("125", "0", "tok/s"), (hidden.Index, hidden.Thinking, hidden.Unit));

        // A rate above 0 draws line 2 again at once.
        var back = BarIntelligenceBlock.From(Chosen, Snapshot(Thinking), ZhHant, hold, 103.5)!;
        Assert.False(back.ThinkingHidden);
        Assert.Equal("性能 125，思考每秒 10 個 token", back.Spoken);
    }

    [Fact]
    public void ABlockWithoutAScoreNeverHidesItsLineTwo()
    {
        var hold = new ThinkingZeroHold();
        foreach (var time in new[] { 0.0, 5, 10 })
        {
            var quiet = BarIntelligenceBlock.From(Chosen, Snapshot(IntelligenceSums.Zero), En, hold, time)!;
            Assert.False(quiet.ThinkingZero);
            Assert.False(quiet.ThinkingHidden);
            Assert.Equal(("–", "–"), (quiet.Index, quiet.Thinking));
        }

        var dash = BarIntelligenceBlock.Of(IntelligenceSums.Zero, En);
        Assert.Same(dash, dash.HidingThinking());

        // A block that is not chosen drops its hold: chosen again, it holds 3 s from there.
        var later = new ThinkingZeroHold();
        BarIntelligenceBlock.From(Chosen, Snapshot(Unthinking), En, later, 0);
        Assert.Null(BarIntelligenceBlock.From(AppSettings.Default, Snapshot(Unthinking), En, later, 5));
        Assert.False(BarIntelligenceBlock.From(Chosen, Snapshot(Unthinking), En, later, 6)!.ThinkingHidden);
        Assert.True(BarIntelligenceBlock.From(Chosen, Snapshot(Unthinking), En, later, 9)!.ThinkingHidden);
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "性能 125")]
    [InlineData(LanguageCodes.ZhHans, "性能 125")]
    [InlineData(LanguageCodes.En, "performance 125")]
    [InlineData(LanguageCodes.Ja, "性能 125")]
    public void TheLabelReadsTheIndexAloneWhileLineTwoIsHidden(string language, string spoken)
    {
        var localizer = new Localizer(Localizer.LoadEmbeddedTable(), language);
        var block = BarIntelligenceBlock.Of(Unthinking, localizer).HidingThinking();
        Assert.True(block.ThinkingHidden);
        Assert.Equal(spoken, block.Spoken);
        Assert.False(BarIntelligenceBlock.Of(Thinking, localizer).HidingThinking().ThinkingHidden);
    }

    [Fact]
    public void LineOneAloneIsCentredInTheSameCells()
    {
        static double Number(string text) => text.Sum(c => char.IsDigit(c) ? 6.0 : c switch { '.' => 2.5, 'k' => 5.5, _ => 6 });
        static double Text(string text) => text.Length * 7.0;
        var shown = BarIntelligenceBlock.Of(Unthinking, ZhHant);
        var hidden = shown.HidingThinking();
        Assert.Equal(shown.Cells(Text, Number), hidden.Cells(Text, Number));

        // Two lines 12 px apart about the centre; alone, line 1 sits halfway between their baselines.
        const double center = 20;
        const double cap = 7;
        var first = shown.FirstBaseline(center, cap, 12);
        Assert.Equal(center - 6 + 3.5, first, 9);
        Assert.Equal((first + first + 12) / 2, hidden.FirstBaseline(center, cap, 12), 9);
        Assert.Equal(center + cap / 2, hidden.FirstBaseline(center, cap, 12), 9);
    }

    private static SpeedSnapshot Snapshot(IntelligenceSums sums) => new(
        DateTimeOffset.UnixEpoch, MonitorState.Running, Rate.Zero, 0, Rate.Zero, null, null, TokenTotals.Zero,
        [new SourceStatus(SourceKind.ClaudeCode, true, string.Empty, true, 1, Rate.Zero, TokenTotals.Zero, 0) { Intelligence = sums }],
        [], 0, 0, [], null, []);
}
