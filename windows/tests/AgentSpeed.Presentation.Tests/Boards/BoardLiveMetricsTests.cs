using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Boards.Demo;
using AgentSpeed.Presentation.Boards.Live;
using AgentSpeed.Presentation.Boards.Scene;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings.Boards;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Presentation.Titles;
using static AgentSpeed.Presentation.Tests.Boards.BoardGroupingTests;

namespace AgentSpeed.Presentation.Tests.Boards;

/// <summary>DESIGN 2c, Live metrics (性能 · 思考 · 費用): layout B per style and size, switches, rows, wording.</summary>
public class BoardLiveMetricsTests
{
    private static readonly Localizer Zh = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);
    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);

    // The Hero table: ranking starts and heights per style and size.
    [Theory]
    [InlineData(BoardStyle.Glass, BoardSize.S, 104, 138)]
    [InlineData(BoardStyle.Glass, BoardSize.M, 116, 186)]
    [InlineData(BoardStyle.Glass, BoardSize.L, 128, 254)]
    [InlineData(BoardStyle.Timing, BoardSize.S, 109, 139)]
    [InlineData(BoardStyle.Timing, BoardSize.M, 121, 187)]
    [InlineData(BoardStyle.Timing, BoardSize.L, 131, 257)]
    [InlineData(BoardStyle.Neon, BoardSize.S, 108, 136)]
    [InlineData(BoardStyle.Neon, BoardSize.M, 118, 186)]
    [InlineData(BoardStyle.Neon, BoardSize.L, 128, 256)]
    public void TheRankingStartsAndRunsAsTheHeroTableSays(BoardStyle style, BoardSize size, double top, double height)
    {
        var metrics = BoardMetrics.For(style, size, BoardReadouts.All);
        Assert.Equal(top, metrics.ListTop, 6);
        Assert.Equal(height, metrics.ListHeight, 6);
        Assert.True(metrics.Gauge);
        Assert.Equal(style == BoardStyle.Timing ? 16 : 14, metrics.StartY, 6);
        Assert.Equal(height - metrics.StartY, metrics.Room, 6);
        Assert.Equal(size == BoardSize.S ? BoardMetrics.For(style, size).GroupHeight : size == BoardSize.L ? 40 : 38, metrics.GroupHeight, 6);
    }

    [Theory]
    [InlineData(BoardStyle.Glass, BoardSize.S, 12, 78, 14)]
    [InlineData(BoardStyle.Glass, BoardSize.M, 14, 86, 16)]
    [InlineData(BoardStyle.Glass, BoardSize.L, 16, 94, 18)]
    [InlineData(BoardStyle.Timing, BoardSize.S, 14, 72, 10)]
    [InlineData(BoardStyle.Timing, BoardSize.M, 16, 80, 12)]
    [InlineData(BoardStyle.Timing, BoardSize.L, 18, 88, 12)]
    public void GlassAndTimingMoveTheirHistoryToAFullWidthStrip(BoardStyle style, BoardSize size, double top, double band, double strip)
    {
        var metrics = BoardMetrics.For(style, size, BoardReadouts.All);
        var hero = BoardHeroLayout.For(style, size, BoardReadouts.All);
        Assert.Equal(top, metrics.HeroTop, 6);
        Assert.Equal(band, metrics.HeroHeight, 6);
        Assert.Equal(0, hero.History.Left, 6);
        Assert.Equal(hero.Width, hero.History.Width, 6);
        Assert.Equal(strip, hero.History.Height, 6);
        Assert.Equal(style == BoardStyle.Timing ? band + 8 : band, hero.History.Top, 6);
    }

    [Theory]
    [InlineData(BoardSize.S, 92)]
    [InlineData(BoardSize.M, 100)]
    [InlineData(BoardSize.L, 108)]
    public void NeonsHeroGrowsAndKeepsItsLedBar(BoardSize size, double band)
    {
        var hero = BoardHeroLayout.For(BoardStyle.Neon, size, BoardReadouts.All);
        Assert.Equal(band, hero.Height, 6);
        Assert.Equal(band - 6, hero.History.Bottom, 6);
        Assert.Equal(hero.Width, hero.History.Width, 6);
        // The left column is centred in the band less the bar's 17 pt.
        var column = 1.08 * hero.NumberSize + 8 + 12;
        Assert.Equal((band - 17 - column) / 2, hero.NumberTop, 6);
    }

    [Theory]
    [InlineData(BoardStyle.Glass)]
    [InlineData(BoardStyle.Timing)]
    [InlineData(BoardStyle.Neon)]
    public void EveryReadoutOffKeepsTheOriginalHeroAndTheSecondLine(BoardStyle style)
    {
        foreach (var size in BoardSizes.All)
        {
            Assert.Equal(BoardMetrics.For(style, size), BoardMetrics.For(style, size, BoardReadouts.None));
            Assert.Equal(BoardHeroLayout.For(style, size), BoardHeroLayout.For(style, size, BoardReadouts.None));
            // The original hero and list; group rows keep their second line at M and L (8 taller), S stays one line.
            var none = BoardMetrics.For(style, size, BoardReadouts.None);
            var all = BoardMetrics.For(style, size, BoardReadouts.All);
            Assert.False(none.Gauge);
            Assert.False(none.IntelligenceColumn);
            Assert.Equal(size != BoardSize.S, none.TwoLineRows);
            Assert.Equal(size != BoardSize.S, all.TwoLineRows);
            Assert.Equal(all.GroupHeight, none.GroupHeight, 6);
        }
    }

    [Theory]
    [InlineData(BoardStyle.Glass, BoardSize.M, 6, 8, 44, 39, 0)]
    [InlineData(BoardStyle.Neon, BoardSize.S, 6, 8, 44, 34, 0)]
    [InlineData(BoardStyle.Timing, BoardSize.S, 4, 7, 40, 34, 0)]
    [InlineData(BoardStyle.Timing, BoardSize.L, 4, 7, 42, 40, 34)]
    public void RowsGainThe性能ColumnBeforeTheValue(BoardStyle style, BoardSize size, double pad, double gap, double value, double cell, double interval)
    {
        var columns = BoardRowColumns.For(style, size, BoardReadouts.All);
        Assert.Equal(new BoardRowColumns(pad, gap, value, cell, interval), columns);
        Assert.Equal(pad + value + gap, columns.IntelligenceRight, 6);
        var plain = BoardRowColumns.For(style, size, BoardReadouts.All with { Intelligence = false });
        Assert.Equal(style == BoardStyle.Timing && size == BoardSize.S ? 44 : 46, plain.Value, 6);
        Assert.Equal(0, plain.Intelligence, 6);
        Assert.Equal(style == BoardStyle.Timing && size != BoardSize.S ? 38 : 0, plain.Interval, 6);
    }

    // The second line holds only the detail: from under the name, under the capsule column, up to the first figure column's
    // left edge — the figures, centred on the row, hold their columns on both lines.
    [Theory]
    [InlineData(BoardStyle.Glass, BoardSize.M)]
    [InlineData(BoardStyle.Timing, BoardSize.L)]
    [InlineData(BoardStyle.Neon, BoardSize.M)]
    public void TheDetailStopsAtTheFirstFigureColumn(BoardStyle style, BoardSize size)
    {
        var metrics = BoardMetrics.For(style, size, BoardReadouts.All);
        foreach (var readouts in new[] { BoardReadouts.All, BoardReadouts.None, BoardReadouts.All with { Intelligence = false } })
        {
            var columns = BoardRowColumns.For(style, size, readouts);
            var width = metrics.ListWidth;
            var first = columns.Interval > 0 ? columns.IntervalRight + columns.Interval
                : columns.Intelligence > 0 ? columns.IntelligenceRight + columns.Intelligence
                : columns.ValueRight + columns.Value;
            Assert.Equal(width - first, columns.DetailRight(width), 6);
            Assert.True(columns.DetailRight(width) > width - columns.CapsuleRight, "under the capsule column");
        }
    }

    // The per-style tables: heights with 思考 shown and hidden, and the baselines.
    [Theory]
    [InlineData(BoardStyle.Glass, BoardSize.S, 75, 58, 51, 68)]
    [InlineData(BoardStyle.Glass, BoardSize.M, 84, 67, 62, 79)]
    [InlineData(BoardStyle.Glass, BoardSize.L, 90, 73, 69, 86)]
    [InlineData(BoardStyle.Timing, BoardSize.S, 51, 36, 32, 47)]
    [InlineData(BoardStyle.Timing, BoardSize.M, 58, 41, 36.5, 53.5)]
    [InlineData(BoardStyle.Timing, BoardSize.L, 65, 46, 41, 60)]
    [InlineData(BoardStyle.Neon, BoardSize.S, 74.5, 57.5, 50.5, 67.5)]
    [InlineData(BoardStyle.Neon, BoardSize.M, 83, 65, 59, 77)]
    [InlineData(BoardStyle.Neon, BoardSize.L, 87.5, 69.5, 64.5, 82.5)]
    public void TheSubGaugeFollowsItsStylesTable(BoardStyle style, BoardSize size, double height, double hidden, double thinking, double cost)
    {
        var spec = BoardGaugeSpec.For(style, size);
        Assert.Equal(height, BoardGaugeLayout.Height(style, spec, BoardReadouts.All, true), 6);
        Assert.Equal(hidden, BoardGaugeLayout.Height(style, spec, BoardReadouts.All, false), 6);
        var shown = Place(style, size, BoardReadouts.All, 0);
        Assert.Equal(thinking, shown.ThinkingBaseline - shown.Top, 6);
        Assert.Equal(cost, shown.CostBaseline - shown.Top, 6);
        // Hidden: 費用 takes 思考's baseline and the sub-gauge re-centres.
        var gone = Place(style, size, BoardReadouts.All, 1);
        Assert.Equal(thinking, gone.CostBaseline - gone.Top, 6);
        Assert.Equal(shown.Top + (height - hidden) / 2, gone.Top, 6);
    }

    [Theory]
    [InlineData(BoardStyle.Glass, BoardSize.M, 112, 86)]
    [InlineData(BoardStyle.Timing, BoardSize.M, 134, 80)]
    [InlineData(BoardStyle.Neon, BoardSize.L, 128, 108)]
    public void TheSubGaugeIsRightAlignedAndCentredInItsBand(BoardStyle style, BoardSize size, double nominal, double band)
    {
        var placement = Place(style, size, BoardReadouts.All, 0, band: band);
        Assert.Equal(nominal, placement.Width, 6);
        Assert.Equal(300, placement.Left + placement.Width, 6);
        var room = style == BoardStyle.Neon ? band - 17 : band;
        Assert.Equal((room - placement.Height) / 2, placement.Top, 6);
    }

    [Fact]
    public void AWiderTableGrowsTheSubGaugeToTheLeft()
    {
        var narrow = Place(BoardStyle.Glass, BoardSize.M, BoardReadouts.All, 0);
        var wide = BoardGaugeLayout.Place(BoardStyle.Glass, BoardSize.M, BoardReadouts.All, new BoardGaugeCells(40, 8, 60, 30), 300, 86, 0, 1);
        Assert.Equal(112, narrow.Width, 6);
        Assert.Equal(40 + 4 + 8 + 1 + 60 + 3 + 30, wide.Width, 6);
        Assert.Equal(300, wide.Left + wide.Width, 6);
        Assert.Equal(wide.Left, wide.TableLeft, 6);
    }

    [Theory]
    [InlineData(BoardStyle.Glass)]
    [InlineData(BoardStyle.Neon)]
    [InlineData(BoardStyle.Timing)]
    public void SwitchedOffReadoutsTakeNoRoom(BoardStyle style)
    {
        var spec = BoardGaugeSpec.For(style, BoardSize.M);
        var only = BoardReadouts.None with { Intelligence = true };
        Assert.Equal(spec.MainHeight, BoardGaugeLayout.Height(style, spec, only, true), 6);
        // 性能 off: the table alone (timing: the box starts with 思考 at the small row height).
        var table = BoardReadouts.None with { Thinking = true, Cost = true };
        var expected = style == BoardStyle.Timing ? 2 * spec.RowPitch : BoardGaugeSpec.RowHeight + spec.RowPitch;
        Assert.Equal(expected, BoardGaugeLayout.Height(style, spec, table, true), 6);
        var cost = BoardReadouts.None with { Intelligence = true, Cost = true };
        var withCost = Place(style, BoardSize.M, cost, 0);
        Assert.Equal(style == BoardStyle.Timing ? 36.5 : style == BoardStyle.Glass ? 62 : 59, withCost.CostBaseline - withCost.Top, 6);
    }

    [Fact]
    public void ThinkingHidesAfterThreeSecondsAtZeroAndReturnsAtOnce()
    {
        var hold = new BoardThinkingHold();
        var zero = Hero("0", quiet: false, zero: true);
        var busy = Hero("12", quiet: false, zero: false);
        Assert.False(hold.Update(BoardReadouts.All, zero, 10));
        Assert.False(hold.Update(BoardReadouts.All, zero, 12.5));
        Assert.True(hold.Update(BoardReadouts.All, zero, 13));
        // Quiet changes nothing: a hidden 思考 stays hidden.
        Assert.True(hold.Update(BoardReadouts.All, Hero("–", quiet: true, zero: false), 20));
        Assert.False(hold.Update(BoardReadouts.All, busy, 20.5));
        // With 思考 the only hero readout it never hides.
        var only = BoardReadouts.None with { Thinking = true };
        Assert.False(hold.Update(only, zero, 30));
        Assert.False(hold.Update(only, zero, 40));
        // Switching it off and on shows it.
        Assert.False(hold.Update(BoardReadouts.All, zero, 41));
        Assert.False(hold.Update(BoardReadouts.All with { Thinking = false }, zero, 50));
        Assert.False(hold.Update(BoardReadouts.All, zero, 51));
    }

    [Fact]
    public void TheHeroReadsIntelligenceThinkingAndCost()
    {
        var texts = new BoardTexts(Zh, TitleTheme.Vehicles, new CostDisplay(Zh));
        var sums = TestSums.Of(2121, 1286, 3 * 174, 2121);
        var score = new PerfSetReading(PerfForm.Live, 12_840, false, 4, null, null);
        var hero = texts.Hero(sums, score, new CostReading(24_000, 1, 707, CostTally.Zero, 0, [], [], CostState.Priced));

        // The hero reads the full form; glass's dial draws the compact one itself.
        Assert.Equal("12,840", hero.Intelligence);
        Assert.Equal(12_840, hero.Shown);
        Assert.Same(score, hero.Score);
        Assert.Equal("174", hero.Thinking);
        Assert.False(hero.Quiet);
        Assert.Equal("0.024", hero.Cost.Number);
        Assert.Equal(Zh.Format("cost.unitPerSecond", "US$"), hero.Cost.Unit);
        Assert.Equal(BoardMoneyState.Value, hero.Cost.State);

        var quiet = texts.Hero(IntelligenceSums.Zero, PerfSetReading.None, CostReading.Zero);
        Assert.Equal(("–", "–", true), (quiet.Intelligence, quiet.Thinking, quiet.Quiet));
        Assert.Equal(("0", BoardMoneyState.Zero), (quiet.Cost.Number, quiet.Cost.State));
        Assert.Equal("0", quiet.SpokenThinking);

        // Quiet is Σ w = 0 alone: a resting score stays pale after work, and a live one stays live though nothing generates.
        var resting = texts.Hero(IntelligenceSums.Zero, new PerfSetReading(PerfForm.Resting, 1540, false, 2, null, null), CostReading.Zero);
        Assert.Equal(("1,540", "–", true), (resting.Intelligence, resting.Thinking, resting.Quiet));
        var provisional = texts.Hero(IntelligenceSums.Zero, new PerfSetReading(PerfForm.Live, 912, true, 1, null, null), CostReading.Zero);
        Assert.Equal(("912", PerfForm.Live, true), (provisional.Intelligence, provisional.Score.Form, provisional.Quiet));
        Assert.True(provisional.Score.IsProvisional);

        var partial = texts.Hero(sums, score, new CostReading(24_000, 0.5, 707, CostTally.Zero, 0, [], [], CostState.Partial));
        Assert.True(partial.Cost.AtLeast);
        Assert.Equal(Zh.Format("a11y.orb.costAtLeast", "0.024"), partial.Cost.SpokenNumber);
        var unknown = texts.Hero(sums, score, new CostReading(0, 0, 707, CostTally.Zero, 0, [], [], CostState.NothingPriced));
        Assert.Equal(("–", BoardMoneyState.Unknown, false), (unknown.Cost.Number, unknown.Cost.State, unknown.Cost.HasUnit));
        var tag = texts.Hero(sums, score, new CostReading(0, 1, 0, CostTally.Zero, 0, [], [SourceKind.ClaudeCode], CostState.SubscriptionOnly));
        Assert.Equal((Zh["cost.subscription.tag"], BoardMoneyState.Tag), (tag.Cost.Number, tag.Cost.State));
        var zero = texts.Hero(TestSums.Of(300, 15, 0, 300), score, CostReading.Zero);
        Assert.True(zero.ThinkingZero);
        Assert.Equal("0", zero.Thinking);
    }

    [Fact]
    public void TheBoardsLabelAddsTheSwitchedOnReadouts()
    {
        var feed = new BoardDemoFeed(BoardSimScenario.Team) { Localizer = Zh, Readouts = BoardReadouts.All };
        var tick = feed.Step(BoardStyle.Glass, BoardSize.M);
        var board = new BoardDefinition("b");
        var label = BoardAccessibility.Board(board, tick, "噴射客機", Zh);
        var words = tick.Gauge.Score.Words(Zh)!;
        Assert.StartsWith(tick.Gauge.Intelligence, words, StringComparison.Ordinal);
        Assert.StartsWith(BoardAccessibility.Board(board, tick.ValueText, "噴射客機", Zh), label, StringComparison.Ordinal);
        Assert.Contains(Zh.Format("a11y.orb.intel", words, tick.Gauge.Thinking), label, StringComparison.Ordinal);
        Assert.EndsWith(Zh.Format("a11y.board.cost", tick.Gauge.Cost.SpokenNumber, Zh["currency.USD"]), label, StringComparison.Ordinal);

        var hidden = tick with { ThinkingHidden = true };
        Assert.Contains(Zh.Format("a11y.orb.intel.only", words), BoardAccessibility.Board(board, hidden, "t", Zh), StringComparison.Ordinal);
        var noIntel = tick with { Readouts = BoardReadouts.All with { Intelligence = false, Cost = false } };
        Assert.EndsWith(Zh.Format("a11y.board.thinking", tick.Gauge.Thinking), BoardAccessibility.Board(board, noIntel, "t", Zh),
            StringComparison.Ordinal);
        var plain = tick with { Readouts = BoardReadouts.None };
        Assert.Equal(BoardAccessibility.Board(board, tick.ValueText, "t", Zh), BoardAccessibility.Board(board, plain, "t", Zh));
    }

    [Fact]
    public void TheExampleLabelReadsAsTheSpecWritesIt()
    {
        var tick = new BoardTick(707, "707", SpeedTier.Boost, 2000, SpeedMetric.Output, true, [], [], false)
        {
            Readouts = BoardReadouts.All,
            Gauge = new BoardHeroReadouts(new PerfSetReading(PerfForm.Live, 1286, false, 4, null, null), "174", false, false,
                new BoardMoney("0.024", "US$/s", false, BoardMoneyState.Value, "0.024", Zh["currency.USD"])),
        };
        Assert.Equal("排行看板，全部來源：每秒 707 個 token，噴射客機，性能 1,286，4 個 agent 中最高，思考每秒 174 個 token，費用每秒 0.024 美元（API 等值）",
            BoardAccessibility.Board(new BoardDefinition("b"), tick, "噴射客機", Zh));
    }

    // DESIGN 12, Accessibility: provisional, idle and the highest of N, and intel.none in place of the 性能 words while none.
    [Fact]
    public void TheBoardsLabelSpeaksTheScoreWords()
    {
        BoardTick Tick(PerfSetReading score, bool quiet = false) => new(707, "707", SpeedTier.Boost, 2000, SpeedMetric.Output, true, [], [], false)
        {
            Readouts = BoardReadouts.All with { Cost = false },
            Gauge = new BoardHeroReadouts(score, quiet ? "–" : "174", quiet, false, BoardHeroReadouts.Empty.Cost),
        };
        var board = new BoardDefinition("b");
        var prefix = "排行看板，全部來源：每秒 707 個 token，噴射客機，";
        Assert.Equal(prefix + "性能 912，初步（樣本少），思考每秒 174 個 token",
            BoardAccessibility.Board(board, Tick(new PerfSetReading(PerfForm.Live, 912, true, 1, null, null)), "噴射客機", Zh));
        Assert.Equal(prefix + "性能 1,540，閒置中，2 個 agent 中最高，思考每秒 0 個 token",
            BoardAccessibility.Board(board, Tick(new PerfSetReading(PerfForm.Resting, 1540, false, 2, null, null), quiet: true), "噴射客機", Zh));
        Assert.Equal(prefix + "性能 12,840，思考每秒 174 個 token",
            BoardAccessibility.Board(board, Tick(new PerfSetReading(PerfForm.Live, 12_840, false, 1, null, null)), "噴射客機", Zh));
        Assert.Equal(prefix + "性能：尚無分數，思考每秒 174 個 token", BoardAccessibility.Board(board, Tick(PerfSetReading.None), "噴射客機", Zh));
        Assert.Equal(prefix + "性能：尚無分數",
            BoardAccessibility.Board(board, Tick(PerfSetReading.None) with { ThinkingHidden = true }, "噴射客機", Zh));
    }

    [Fact]
    public void GroupRowsShowTheirHighestMemberAndSubRowsTheirOwn()
    {
        var texts = new BoardTexts(Zh, TitleTheme.Vehicles, new CostDisplay(Zh));
        var root = Agent("r", 120) with { Intelligence = Scored(720, PerfForm.Live, false, "r") };
        var sub = Agent("s", 60, "r") with { Intelligence = Scored(12_840, PerfForm.Live, true, "s") };
        var group = BoardGrouping.Group([root, sub], SpeedMetric.Output)[0];
        var entry = new BoardEntry(BoardEntryKind.Group, "r", group, root, 1, 0, 38) { TwoLine = true };
        var row = texts.Row(entry, BoardReadouts.All)!;

        // Rows show the compact form and say the full one.
        Assert.Equal("12.8k", row.Intelligence);
        Assert.True(row.Live);

        // The group's provisional mark follows the member that supplies its value.
        Assert.True(row.IsProvisional);
        Assert.Same(sub, row.Score.Agent);
        Assert.Equal("性能 12,840，初步（樣本少），2 個 agent 中最高", row.Spoken);

        // The row's label names no cost: its 性能 words follow the speed.
        Assert.Equal(Zh.Format("a11y.board.row", 1, root.Project, SpeedFormat.Speed(group.Live)) + "，" + row.Spoken + "，" + Zh.Format("boards.subCount", 1),
            BoardAccessibility.Row(entry with { Readout = row }, Zh));

        // A sub row shows its sub-agent's own value; one agent is never "the highest of".
        var own = texts.Row(new BoardEntry(BoardEntryKind.Sub, "s", group, sub, 0, 38, 22), BoardReadouts.All)!;
        Assert.Equal(("12.8k", "性能 12,840，初步（樣本少）"), (own.Intelligence, own.Spoken));
        var rootRow = texts.Row(new BoardEntry(BoardEntryKind.Sub, "r", group, root, 0, 38, 22), BoardReadouts.All)!;
        Assert.Equal(("720", false), (rootRow.Intelligence, rootRow.IsProvisional));

        // An idle group: the highest resting score, pale.
        var idle = root with { Live = Rate.Zero, Intelligence = Scored(1540, PerfForm.Resting, false, "r") };
        var idleSub = sub with { Live = Rate.Zero, Intelligence = Scored(640, PerfForm.Resting, true, "s") };
        var resting = BoardGrouping.Group([idle, idleSub], SpeedMetric.Output)[0];
        var quiet = texts.Row(new BoardEntry(BoardEntryKind.Group, "r", resting, idle, 1, 0, 38), BoardReadouts.All)!;
        Assert.Equal(("1,540", false, false), (quiet.Intelligence, quiet.Live, quiet.IsProvisional));
        Assert.Equal("性能 1,540，閒置中，2 個 agent 中最高", quiet.Spoken);

        // None: a dash, no words; live before resting whatever the numbers.
        var none = texts.Row(new BoardEntry(BoardEntryKind.Group, "n", BoardGrouping.Group([Agent("n", 5)], SpeedMetric.Output)[0], null, 2, 0, 38),
            BoardReadouts.All)!;
        Assert.Equal(("–", false, string.Empty), (none.Intelligence, none.Live, none.Spoken));
        var mixed = BoardGrouping.Group([idle, sub with { Intelligence = Scored(300, PerfForm.Live, false, "s") }],
            SpeedMetric.Output)[0];
        Assert.Equal("300", texts.Row(new BoardEntry(BoardEntryKind.Group, "r", mixed, idle, 1, 0, 38), BoardReadouts.All)!.Intelligence);

        // 性能 off: no words; the more lines carry no cell.
        Assert.Empty(texts.Row(entry, BoardReadouts.All with { Intelligence = false })!.Spoken);
        Assert.Null(texts.Row(new BoardEntry(BoardEntryKind.MoreGroups, "#rest", group, null, 0, 0, 22), BoardReadouts.All));
    }

    // An agent's 性能 with the given score and form (supplied by `id`), provisional or not.
    internal static AgentIntelligence Scored(double score, PerfForm form, bool provisional, string id)
    {
        var value = (form == PerfForm.Resting ? TestSums.Resting(score, provisional) : TestSums.Live(score, provisional)) with { AgentId = id };
        return new AgentIntelligence(IntelligenceSums.Zero with { Score = value }, value);
    }

    // Lines that would carry a cost: the 費用 label or the cost words, the subscription tag, a currency unit.
    private static IEnumerable<string> CostLines(IEnumerable<string> lines) =>
        lines.Where(l => l.Contains(En["cost.glyph"], StringComparison.Ordinal) || l.Contains(En["cost.unknown"], StringComparison.Ordinal) ||
            l.Contains(En["cost.partial"], StringComparison.Ordinal) || l.Contains(En["cost.subscription.tag"], StringComparison.Ordinal) ||
            (l.Contains("/s", StringComparison.Ordinal) && !l.Contains("tok/s", StringComparison.Ordinal)));

    [Fact]
    public void ABoardWhoseSourcesAreAllSubscriptionsShowsTheTag()
    {
        var display = new CostDisplay(En) { Settings = Settings.CostSettings.Default with { Subscriptions = SourceSet.Everything } };
        var texts = new BoardTexts(En, TitleTheme.Vehicles, display);
        var board = new BoardDefinition("b");
        var feed = new BoardLiveFeed(board);
        feed.Configure(board, SpeedMetric.Output, 2000, 0);
        var agent = Agent("a", 300);
        var tick = feed.Update(BoardLiveFeedTests.Snapshot(0, agent), 0, texts);
        Assert.Equal(En["cost.subscription.tag"], tick.CostText);
        Assert.Equal(BoardMoneyState.Tag, tick.Gauge.Cost.State);
        // The tag is the hero's alone: neither the group's tooltip nor its score card names a cost or a subscription.
        var group = tick.Entries.First(e => e.Kind == BoardEntryKind.Group);
        Assert.Empty(CostLines(BoardTooltip.Lines(group, En)));
        Assert.Empty(CostLines(BoardScoreCards.Row(group, tick.Metric, En)!.Subject.FactLines!));

        // Including subscriptions counts them again.
        display.Settings = display.Settings with { IncludeSubscriptions = true };
        Assert.Equal("$12.34", feed.Update(BoardLiveFeedTests.Snapshot(1, agent), 1, texts).CostText);
    }

    // The demo's hero after 40 ticks of the team story, as board-metrics.html?scenario=team&steps=40 shows it; its 費用 comes
    // from the sim's agents, the engine keeping no cost per agent.
    [Fact]
    public void TheDemoHeroShowsThePrototypesNumbers()
    {
        var feed = new BoardDemoFeed(BoardSimScenario.Team) { Localizer = Zh, Readouts = BoardReadouts.All };
        BoardTick tick = null!;
        for (var i = 0; i < 40; i++)
        {
            tick = feed.Step(BoardStyle.Glass, BoardSize.M);
        }

        Assert.Equal("707", tick.ValueText);
        // The simulation's highest performance score (a demo value per agent id, in full form) over the team's thinking rate.
        Assert.Equal(("4,272", "174"), (tick.Gauge.Intelligence, tick.Gauge.Thinking));
        Assert.Equal(("0.024", BoardMoneyState.Value), (tick.Gauge.Cost.Number, tick.Gauge.Cost.State));
        Assert.All(tick.Entries.Where(e => e.Kind is BoardEntryKind.Group or BoardEntryKind.Sub),
            e => Assert.Empty(CostLines(BoardScoreCards.Row(e, tick.Metric, Zh)!.Subject.FactLines!)));

        var subscriptions = new BoardDemoFeed(BoardSimScenario.Team) { Localizer = Zh, Readouts = BoardReadouts.All, Billable = SourceSet.Empty };
        Assert.Equal(BoardMoneyState.Tag, subscriptions.Step(BoardStyle.Glass, BoardSize.M).Gauge.Cost.State);
    }

    // Rows kept with the readouts on (the team story as the prototype shows it: five groups).
    [Theory]
    [InlineData(BoardStyle.Glass, BoardSize.S, 3, true, 0)]
    [InlineData(BoardStyle.Glass, BoardSize.M, 4, true, 0)]
    [InlineData(BoardStyle.Glass, BoardSize.L, 5, false, 0)]
    [InlineData(BoardStyle.Timing, BoardSize.M, 4, true, 0)]
    [InlineData(BoardStyle.Timing, BoardSize.L, 5, false, 1)]
    [InlineData(BoardStyle.Neon, BoardSize.M, 4, true, 0)]
    [InlineData(BoardStyle.Neon, BoardSize.S, 3, true, 0)]
    public void TheRowsKeptFollowTheTable(BoardStyle style, BoardSize size, int groups, bool more, int subs)
    {
        var feed = new BoardDemoFeed(BoardSimScenario.Team) { Localizer = Zh, Readouts = BoardReadouts.All };
        BoardTick tick = null!;
        for (var i = 0; i < 40; i++)
        {
            tick = feed.Step(style, size);
        }

        Assert.Equal(groups, tick.Entries.Count(e => e.Kind == BoardEntryKind.Group));
        Assert.Equal(more, tick.Entries.Any(e => e.Kind == BoardEntryKind.MoreGroups));
        Assert.Equal(subs, tick.Entries.Count(e => e.Kind == BoardEntryKind.Sub));
        // Group rows have their second line at M and L; S never draws it.
        Assert.All(tick.Entries.Where(e => e.Kind == BoardEntryKind.Group), e => Assert.Equal(size != BoardSize.S, e.TwoLine));
        Assert.All(tick.Entries.Where(e => e.Kind is BoardEntryKind.Group or BoardEntryKind.Sub), e => Assert.NotNull(e.Readout));
        var metrics = BoardMetrics.For(style, size, tick.Readouts);
        var last = tick.Entries[^1];
        Assert.True(last.Y + last.Height <= metrics.StartY + metrics.Room + 1e-6, "no row is cut");
    }

    [Fact]
    public void TheLiveFeedCarriesTheSwitchesAndTheThinkingHold()
    {
        var board = new BoardDefinition("b");
        var feed = new BoardLiveFeed(board);
        feed.Configure(board, SpeedMetric.Output, 2000, 0);
        var texts = new BoardTexts(En, TitleTheme.Vehicles, new CostDisplay(En));
        var thinking = Agent("a", 300) with { Intelligence = new AgentIntelligence(TestSums.Of(900, 60, 0, 900), TestSums.Live(60)) };
        var snapshot = BoardLiveFeedTests.Snapshot(0, thinking) with
        {
            Sources = [.. BoardLiveFeedTests.Snapshot(0, thinking).Sources.Select(s => s with { Intelligence = s.Kind == SourceKind.ClaudeCode
                ? TestSums.Of(900, 60, 0, 900) : IntelligenceSums.Zero })],
        };
        var first = feed.Update(snapshot, 0, texts);
        Assert.Equal(BoardReadouts.All, first.Readouts);
        Assert.True(first.Gauge.ThinkingZero);
        Assert.False(first.ThinkingHidden);
        Assert.False(feed.Update(snapshot, 2.5, texts).ThinkingHidden);
        Assert.True(feed.Update(snapshot, 3, texts).ThinkingHidden);

        feed.Configure(board with { Readouts = BoardReadouts.None with { Thinking = true } }, SpeedMetric.Output, 2000, 0);
        var only = feed.Relayout(texts)!;
        Assert.False(only.ThinkingHidden);
        Assert.Equal(BoardReadouts.None with { Thinking = true }, only.Readouts);
    }

    private static BoardHeroReadouts Hero(string thinking, bool quiet, bool zero) =>
        new(new PerfSetReading(PerfForm.Live, 600, false, 1, null, null), thinking, quiet, zero, BoardHeroReadouts.Empty.Cost);

    private static BoardGaugePlacement Place(BoardStyle style, BoardSize size, BoardReadouts readouts, double glide, double band = 86) =>
        BoardGaugeLayout.Place(style, size, readouts, new BoardGaugeCells(18, 7, 36, 24), 300, band, glide, 1 - glide);
}
