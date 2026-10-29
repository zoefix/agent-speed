using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

/// <summary>
/// 性能 in the taskbar block (DESIGN 10, Menu bar and taskbar; DESIGN 12): the highest score of all enabled sources' agents
/// in compact form, its forms and words, the provisional mark at 0.17 × the size and the tooltip's supplying agent.
/// </summary>
public class BarPerfScoreTests
{
    private static readonly Localizer ZhHant = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);
    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);
    private static readonly AppSettings Chosen = AppSettings.Default with { BarIntelligence = true };

    private static AgentStatus Agent(string id, string project, PerfForm form, double score, bool provisional = false,
        double thinking = 0, SourceKind source = SourceKind.ClaudeCode)
    {
        var set = (form == PerfForm.Live ? TestSums.Live(score, provisional) : TestSums.Resting(score, provisional)) with { AgentId = id };
        var sums = new IntelligenceSums(thinking > 0 ? 3 * thinking : 0, 3 * thinking, thinking > 0 ? 3 * thinking : 0) { Score = set };
        return new AgentStatus(id, source, project, "model", form == PerfForm.Live ? AgentPhase.Generating : AgentPhase.Idle,
            false, Rate.Zero, null, DateTimeOffset.UnixEpoch)
        {
            Intelligence = new AgentIntelligence(sums, set),
        };
    }

    /// <summary>A snapshot of the agents' own sources, each source summing its agents' window and taking their highest score.</summary>
    private static SpeedSnapshot Snapshot(params AgentStatus[] agents) => new(
        DateTimeOffset.UnixEpoch, MonitorState.Running, Rate.Zero, 0, Rate.Zero, null, null, TokenTotals.Zero,
        [.. agents.GroupBy(a => a.Source).Select(group => new SourceStatus(group.Key, true, string.Empty, true, 1, Rate.Zero, TokenTotals.Zero, 0)
        {
            Intelligence = group.Aggregate(IntelligenceSums.Zero, (sum, agent) => sum + agent.Intelligence.Sums),
        })],
        agents, 0, 0, [], null, []);

    [Fact]
    public void TheBlockShowsTheHighestScoreOfEveryEnabledSourceLiveBeforeResting()
    {
        var snapshot = Snapshot(
            Agent("a1", "nova", PerfForm.Live, 1286, thinking: 128),
            Agent("a2", "rocket", PerfForm.Live, 520, source: SourceKind.Codex),
            Agent("a3", "atlas", PerfForm.Resting, 9700, source: SourceKind.Codex));
        var block = BarIntelligenceBlock.From(Chosen, snapshot, ZhHant)!;
        Assert.Equal(("1,286", PerfForm.Live, false, false), (block.Index, block.Form, block.IndexDim, block.Provisional));
        Assert.Equal(("性能", "思考"), (block.Label, block.ThinkingLabel));
        Assert.Equal("128", block.Thinking);
        Assert.Equal("性能 1,286，2 個 agent 中最高，思考每秒 128 個 token", block.Spoken);
        Assert.Equal("性能 1,286 · 最高：nova", block.Top);
    }

    [Fact]
    public void TheBlockUsesTheCompactFormFromTenThousandAndTheTooltipTheFullOne()
    {
        var block = BarIntelligenceBlock.From(Chosen, Snapshot(Agent("a1", "quasar", PerfForm.Live, 12_840.4, thinking: 40)), En)!;
        Assert.Equal(("12.8k", "Perf"), (block.Index, block.Label));
        Assert.Equal("performance 12,840, thinking 40 tokens per second", block.Spoken);
        Assert.StartsWith("Perf 12,840 · ", block.Top, StringComparison.Ordinal);
    }

    [Fact]
    public void ARestingScoreStaysPaleAfterWorkAndStandsAloneOnceThinkingHasReadZero()
    {
        var hold = new ThinkingZeroHold();
        var snapshot = Snapshot(
            Agent("a1", "nova", PerfForm.Resting, 1540),
            Agent("a2", "atlas", PerfForm.Resting, 400));
        var first = BarIntelligenceBlock.From(Chosen, snapshot, ZhHant, hold, 0)!;

        // Nothing generated: the thinking number is `–` and the words leave it out.
        Assert.Equal(("1,540", "–"), (first.Index, first.Thinking));
        Assert.Equal((PerfForm.Resting, true, true), (first.Form, first.IndexDim, first.ThinkingDim));
        Assert.False(first.ThinkingHidden);
        Assert.Equal("性能 1,540，閒置中，2 個 agent 中最高", first.Spoken);

        var alone = BarIntelligenceBlock.From(Chosen, snapshot, ZhHant, hold, 3)!;
        Assert.True(alone.ThinkingHidden);
        Assert.Equal(first.Spoken, alone.Spoken);
        Assert.Equal("性能 1,540 · 最高：nova", alone.Top);
    }

    [Fact]
    public void NoScoreReadsADashAndItsOwnWordsWithoutATooltipLine()
    {
        var quiet = BarIntelligenceBlock.Of(IntelligenceSums.Zero, ZhHant);
        Assert.Equal(("–", "–", PerfForm.None), (quiet.Index, quiet.Thinking, quiet.Form));
        Assert.Equal("性能：尚無分數", quiet.Spoken);
        Assert.Null(quiet.Top);
        Assert.Empty(quiet.ProvisionalDots(0, 18, 20, 10, 1, false));

        // Agents that think before any agent has a score: the thinking words stay after intel.none.
        var reading = BarIntelligenceBlock.Of(new IntelligenceSums(300, 30, 300), En);
        Assert.Equal(("–", "10"), (reading.Index, reading.Thinking));
        Assert.Equal("performance: no score yet, thinking 10 tokens per second", reading.Spoken);
        Assert.False(reading.ThinkingZero);
    }

    [Fact]
    public void AProvisionalScoreSaysSoAndNamesTheSourceWithoutAProject()
    {
        var snapshot = Snapshot(Agent("a1", string.Empty, PerfForm.Live, 912.4, provisional: true, thinking: 64));
        var block = BarIntelligenceBlock.From(Chosen, snapshot, ZhHant)!;
        Assert.True(block.Provisional);
        Assert.Equal("性能 912，初步（樣本少），思考每秒 64 個 token", block.Spoken);
        Assert.Equal(ZhHant.Format("perf.tooltip.top", "912", ZhHant[DisplayText.SourceKey(SourceKind.ClaudeCode)]), block.Top);
    }

    [Fact]
    public void TheNumberCellIsAsWideAsTheWidestCompactScoreOrRate()
    {
        var block = BarIntelligenceBlock.Of(IntelligenceSums.Zero, ZhHant);
        var cells = block.Cells(text => text.Length * 7, number => number.Length * 6);
        Assert.Equal(5 * 6, cells.Number);
        Assert.Equal(cells.Label + 3 + cells.Number + 3 + cells.Unit, cells.Width);
    }

    [Fact]
    public void TheScoreEndsWhereTheThinkingUnitEnds()
    {
        // DESIGN 10: line 1 has no unit, so its score shares the block's right edge with line 2's tok/s.
        var cells = BarIntelligenceBlock.Of(IntelligenceSums.Zero, ZhHant).Cells(text => text.Length * 7, number => number.Length * 6);
        Assert.Equal(cells.Label + 3 + cells.Number + 3 + cells.Unit, cells.ScoreRight);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void TheProvisionalMarkSitsInTheRoomLineTwoLeavesOnTheDevicePixelGrid(double scale)
    {
        var block = BarIntelligenceBlock.Of(new PerfSetReading(PerfForm.Live, 912, true, 1, null, null), TestSums.Of(300, 912), ZhHant);
        const double left = 40.3;
        const double width = 12.2;
        const double baseline = 17.6;
        var dots = block.ProvisionalDots(left, width, baseline, 10, scale, highContrast: false);
        var mark = PerfProvisionalMark.Underline(10, width, scale, barBlock: true);
        Assert.Equal(mark.Centers.Count, dots.Count);
        Assert.All(dots, dot =>
        {
            Assert.Equal(mark.Diameter, dot.Diameter, 9);
            Assert.Equal(Math.Round(dot.X * scale), dot.X * scale, 6);
            Assert.Equal(Math.Round(dot.Y * scale), dot.Y * scale, 6);

            // Centred 0.17 × 10 below the baseline, within the half pixel the grid moves it.
            Assert.InRange(dot.Y + dot.Diameter / 2 - (baseline + 1.7), -0.5 / scale - 1e-9, 0.5 / scale + 1e-9);
        });
        Assert.InRange(dots[0].X, left - 0.5 / scale, left + 0.5 / scale);
        Assert.InRange(dots[^1].X + dots[^1].Diameter, left + width - 0.5 / scale, left + width + 0.5 / scale);

        // High contrast: the dots half a point thicker before rounding to the device pixel.
        var contrast = block.ProvisionalDots(left, width, baseline, 10, scale, highContrast: true);
        Assert.Equal(PerfProvisionalMark.Diameter(10, scale, highContrast: true), contrast[0].Diameter, 9);
        Assert.True(contrast[0].Diameter >= dots[0].Diameter);
    }

    [Fact]
    public void AScoreFromTenOrMoreRequestsHasNoProvisionalMark()
    {
        var block = BarIntelligenceBlock.Of(TestSums.Of(300, 1213), ZhHant);
        Assert.False(block.Provisional);
        Assert.Empty(block.ProvisionalDots(0, 12, 17, 10, 1.5, false));
    }
}
