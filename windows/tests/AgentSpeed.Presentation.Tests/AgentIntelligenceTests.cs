using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

/// <summary>
/// Per-agent 性能 on the dashboard (DESIGN 4, 性能 column; METRICS 6.9) and the taskbar 性能 block (DESIGN 10, Menu bar and
/// taskbar).
/// </summary>
public class AgentIntelligenceTests
{
    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);
    private static readonly Localizer ZhHant = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);

    [Fact]
    public void ALiveRowShowsItsScoreInCompactFormAndSaysItInFull()
    {
        var cell = AgentIntelligenceCell.Of(Agent(PerfForm.Live, 12_840.4), ZhHant);
        Assert.Equal(PerfForm.Live, cell.Form);
        Assert.Equal("12.8k", cell.Text);
        Assert.Equal("性能 12,840", cell.Spoken);
        Assert.Equal((12_840, false), (cell.Shown, cell.Provisional));
    }

    [Fact]
    public void AnIdleProvisionalRowIsPaleAndDotted()
    {
        var cell = AgentIntelligenceCell.Of(Agent(PerfForm.Resting, 912.4, provisional: true), ZhHant);
        Assert.Equal(PerfForm.Resting, cell.Form);
        Assert.Equal("912", cell.Text);
        Assert.Equal("性能 912，初步（樣本少），閒置中", cell.Spoken);
        Assert.True(cell.Provisional);
    }

    [Fact]
    public void ARowWithoutAScoreShowsADashAndSaysNothing()
    {
        var cell = AgentIntelligenceCell.Of(Agent(PerfForm.None, null), En);
        Assert.Equal(PerfForm.None, cell.Form);
        Assert.Equal("–", cell.Text);
        Assert.Equal(string.Empty, cell.Spoken);
        Assert.Equal((null, false), (cell.Shown, cell.Provisional));
    }

    [Fact]
    public void ASubAgentShowsItsOwnValue()
    {
        var parent = Agent(PerfForm.Resting, 1612.25, id: "p");
        var child = Agent(PerfForm.Live, 824, id: "c") with { IsSubagent = true, ParentId = "p" };
        Assert.Equal(("1,612", PerfForm.Resting), Pair(AgentIntelligenceCell.Of(parent, En)));
        Assert.Equal(("824", PerfForm.Live), Pair(AgentIntelligenceCell.Of(child, En)));
    }

    [Fact]
    public void AGroupTakesTheHighestLiveScoreOfItsMembers()
    {
        var root = Agent(PerfForm.Resting, 4098.75, id: "root");
        var low = Agent(PerfForm.Live, 824, id: "low") with { ParentId = "root" };
        var high = Agent(PerfForm.Live, 1266.5, provisional: true, id: "high") with { ParentId = "root" };
        var group = AgentIntelligence.Group(root, [low, high]);
        Assert.Equal((PerfForm.Live, 1267, "high", true), (group.Form, group.Shown, group.Score.AgentId, group.Provisional));

        var resting = AgentIntelligence.Group(root, [Agent(PerfForm.None, null, id: "quiet")]);
        Assert.Equal((PerfForm.Resting, 4099, "root"), (resting.Form, resting.Shown, resting.Score.AgentId));
    }

    [Theory]
    [InlineData(34.5, 20.9, 35)]
    [InlineData(31.2, 21.4, 32)]
    [InlineData(20, 22.6, 23)]
    public void TheCellIsTheWiderOf8888AndTheCaptionRoundedUp(double number, double caption, double width)
    {
        Assert.Equal(width, AgentIntelligenceColumn.Width(number, caption));
        Assert.Equal(width + 6, AgentIntelligenceColumn.Footprint(width));
        Assert.Equal("8,888", AgentIntelligenceColumn.WidestNumber);
    }

    [Fact]
    public void TheTaskbarBlockFollowsItsSwitchAndNeedsNoMeasuring()
    {
        var chosen = AppSettings.Default with { BarIntelligence = true };
        Assert.NotNull(BarIntelligenceBlock.From(chosen, Snapshot(IntelligenceSums.Zero), En));
        Assert.NotNull(BarIntelligenceBlock.From(chosen with { Traffic = chosen.Traffic with { Enabled = false } }, Snapshot(IntelligenceSums.Zero), En));
        Assert.Null(BarIntelligenceBlock.From(chosen with { BarMode = BarDisplayMode.GlyphOnly }, Snapshot(IntelligenceSums.Zero), En));
        Assert.Null(BarIntelligenceBlock.From(AppSettings.Default, Snapshot(IntelligenceSums.Zero), En));

        // Either network choice keeps it: the two blocks show together.
        foreach (var bar in new[] { BarTraffic.Off, BarTraffic.Machine, BarTraffic.Agents })
        {
            Assert.NotNull(BarIntelligenceBlock.From(chosen with { Traffic = chosen.Traffic with { Bar = bar } }, Snapshot(IntelligenceSums.Zero), En));
        }

        Assert.Null(BarTrafficBlock.From(chosen, new Core.Traffic.TrafficSnapshot(), En));
    }

    [Fact]
    public void TheTaskbarBlockReadsTheHighestScoreOverTheThinkingRateOfAllSources()
    {
        var chosen = AppSettings.Default with { BarIntelligence = true };
        // 300 tokens in the window with 30 of reasoning: 30 / 3 s = 10 tok/s.
        var block = BarIntelligenceBlock.From(chosen, Snapshot(new IntelligenceSums(300, 30, 300) { Score = Live(1213.25) }), ZhHant)!;
        Assert.Equal(("性能", "1,213", "思考", "10", "tok/s"), (block.Label, block.Index, block.ThinkingLabel, block.Thinking, block.Unit));
        Assert.Equal(PerfForm.Live, block.Form);
        Assert.False(block.IndexDim);
        Assert.False(block.ThinkingDim);
        Assert.Equal("性能 1,213，思考每秒 10 個 token", block.Spoken);

        var english = BarIntelligenceBlock.Of(new IntelligenceSums(300, 0, 300) { Score = Live(12_840) }, En);
        Assert.Equal(("Perf", "12.8k", "TH", "0"), (english.Label, english.Index, english.ThinkingLabel, english.Thinking));
        Assert.True(english.ThinkingDim);
        Assert.True(english.HasScore);
    }

    [Fact]
    public void AQuietBlockStaysWithDashesAndItsUnit()
    {
        var block = BarIntelligenceBlock.Of(IntelligenceSums.Zero, ZhHant);
        Assert.Equal(("–", "–", "tok/s"), (block.Index, block.Thinking, block.Unit));
        Assert.False(block.HasScore);
        Assert.True(block.IndexDim);
        Assert.True(block.ThinkingDim);
        Assert.Equal(ZhHant["intel.none"], block.Spoken);
    }

    [Fact]
    public void TheBlocksCellsNeverChangeWithTheValues()
    {
        // Tabular digits: every digit 6 wide; '.', 'k' and '–' narrower or as wide as one digit.
        static double Number(string text) => text.Sum(c => char.IsDigit(c) ? 6.0 : c switch { '.' => 2.5, ',' => 2.5, 'k' => 5.5, 'M' => 9, _ => 6 });
        static double Text(string text) => text.Length * 7.0;
        var widths = new[] { "–", "0", "100", "9", "4.2", "1,286", "1.28k", "12.8k" }
            .Select(value => new BarIntelligenceBlock("Perf", "TH", value, value, "tok/s", PerfForm.Live, false, string.Empty).Cells(Text, Number))
            .ToArray();
        Assert.All(widths, cells => Assert.Equal(widths[0], cells));
        Assert.Equal(Number("8,888"), widths[0].Number);
        Assert.Equal(28 + 3 + Number("8,888") + 3 + 35, widths[0].Width);

        // From 1 M tok/s the number widens its cell.
        var wide = new BarIntelligenceBlock("Perf", "TH", "100", "1.28M", "tok/s", PerfForm.Live, false, string.Empty).Cells(Text, Number);
        Assert.Equal(Number("1.28M"), wide.Number);
    }


    private static PerfSetValue Live(double score) => TestSums.Live(score);

    private static AgentStatus Agent(PerfForm form, double? score, bool provisional = false, string id = "a")
    {
        var set = score is { } s ? (form == PerfForm.Resting ? TestSums.Resting(s, provisional) : TestSums.Live(s, provisional)) with { AgentId = id }
            : PerfSetValue.None;
        return new(id, SourceKind.Codex, "atlas", "model", form == PerfForm.Live ? AgentPhase.Generating : AgentPhase.Idle, false,
            Rate.Zero, null, DateTimeOffset.UnixEpoch)
        {
            Intelligence = new AgentIntelligence(IntelligenceSums.Zero with { Score = set }, set),
        };
    }

    private static (string, PerfForm) Pair(AgentIntelligenceCell cell) => (cell.Text, cell.Form);

    private static SpeedSnapshot Snapshot(IntelligenceSums sums) => new(
        DateTimeOffset.UnixEpoch, MonitorState.Running, Rate.Zero, 0, Rate.Zero, null, null, TokenTotals.Zero,
        [new SourceStatus(SourceKind.ClaudeCode, true, string.Empty, true, 1, Rate.Zero, TokenTotals.Zero, 0) { Intelligence = sums }],
        [], 0, 0, [], null, []);
}
