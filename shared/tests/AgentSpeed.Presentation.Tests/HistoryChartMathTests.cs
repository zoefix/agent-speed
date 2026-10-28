using AgentSpeed.Core;
using AgentSpeed.Presentation.History;
using static AgentSpeed.Presentation.Tests.TestSupport.HistoryData;

namespace AgentSpeed.Presentation.Tests;

public class HistoryChartMathTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(1, 1)]
    [InlineData(1.2, 2)]
    [InlineData(2, 2)]
    [InlineData(2.2, 2.5)]
    [InlineData(2.5, 2.5)]
    [InlineData(3, 5)]
    [InlineData(5, 5)]
    [InlineData(6, 10)]
    [InlineData(9.99, 10)]
    [InlineData(0.03, 0.05)]
    [InlineData(250, 250)]
    [InlineData(1000, 1000)]
    [InlineData(1001, 2000)]
    [InlineData(237.5, 250)]
    [InlineData(1_300_000, 2_000_000)]
    public void NiceStepRoundsUpToOneTwoTwoAndAHalfOrFive(double value, double expected)
    {
        Assert.Equal(expected, HistoryChartMath.NiceStep(value), 9);
    }

    [Fact]
    public void TicksWithoutDataAreAUnitScale()
    {
        Assert.Equal(new AxisTicks(1, 4), HistoryChartMath.Ticks(0));
        Assert.Equal(new AxisTicks(1, 4), HistoryChartMath.Ticks(-5));
        Assert.Equal(new AxisTicks(1, 3), HistoryChartMath.Ticks(double.NaN, 3));
    }

    [Theory]
    [InlineData(950, 4, 250, 4)]
    [InlineData(1000, 4, 250, 4)]
    [InlineData(1001, 4, 500, 3)]
    [InlineData(10, 4, 2.5, 4)]
    [InlineData(3, 4, 1, 3)]
    [InlineData(0.4, 4, 0.1, 4)]
    [InlineData(7, 0, 10, 1)]
    [InlineData(1_234_567, 4, 500_000, 3)]
    public void TicksUseANiceStep(double dataMax, int maxIntervals, double step, int intervals)
    {
        var ticks = HistoryChartMath.Ticks(dataMax, maxIntervals);
        Assert.Equal(step, ticks.Step, 9);
        Assert.Equal(intervals, ticks.Intervals);
        Assert.Equal(step * intervals, ticks.Max, 9);
        Assert.Equal(0, ticks.Value(0));
        Assert.Equal(step * 2, ticks.Value(2), 9);
    }

    [Fact]
    public void TicksAlwaysCoverTheDataWithinTheIntervalLimit()
    {
        var random = new Random(11);
        for (var i = 0; i < 2000; i++)
        {
            var dataMax = Math.Pow(10, random.NextDouble() * 12 - 3);
            var maxIntervals = random.Next(1, 7);
            var ticks = HistoryChartMath.Ticks(dataMax, maxIntervals);
            Assert.InRange(ticks.Intervals, 1, maxIntervals);
            Assert.True(ticks.Max >= dataMax * (1 - 1e-9), $"{dataMax} → {ticks}");
            Assert.True(ticks.Max < dataMax * 5.0001 + 1e-9, $"{dataMax} → {ticks} wastes too much space");
        }
    }

    [Theory]
    [InlineData(130, 4, 50)]
    [InlineData(200, 4, 50)]
    [InlineData(201, 4, 100)]
    [InlineData(42, 3, 20)]
    [InlineData(0, 3, 1)]
    [InlineData(-1, 2, 1)]
    public void AlignedTicksKeepTheGivenIntervalCount(double dataMax, int intervals, double step)
    {
        var ticks = HistoryChartMath.AlignedTicks(dataMax, intervals);
        Assert.Equal(intervals, ticks.Intervals);
        Assert.Equal(step, ticks.Step, 9);
        Assert.True(ticks.Max >= dataMax);
    }

    [Fact]
    public void AlignedTicksNeedAtLeastOneInterval()
    {
        Assert.Equal(1, HistoryChartMath.AlignedTicks(50, 0).Intervals);
        Assert.Equal(50, HistoryChartMath.AlignedTicks(50, 0).Max, 9);
    }

    [Fact]
    public void StackOrderPutsTheLargestSourceFirst()
    {
        DaySummary[] days =
        [
            Summary(D(2026, 10, 20), sources: [new(SourceKind.Codex, 100), new(SourceKind.ClaudeCode, 500), new(SourceKind.Gemini, 50)]),
            Summary(D(2026, 10, 21), sources: [new(SourceKind.Codex, 450), new(SourceKind.DeepSeek, 50), new(SourceKind.OpenCode, 0)]),
        ];

        Assert.Equal([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.DeepSeek, SourceKind.Gemini], HistoryChartMath.StackOrder(days));
        Assert.Empty(HistoryChartMath.StackOrder([]));
    }

    [Fact]
    public void BarsKeepAGapInsideTheirSlot()
    {
        var slots = HistoryChartMath.Slots(10, 300);
        Assert.Equal(30, slots.Slot, 9);
        Assert.Equal(21.6, slots.BarWidth, 9);
        Assert.Equal(10, slots.Count);
        Assert.Equal(4.2, slots.Left(0), 9);
        Assert.Equal(34.2, slots.Left(1), 9);
        Assert.Equal(15, slots.Center(0), 9);
        Assert.Equal(285, slots.Center(9), 9);
    }

    [Fact]
    public void BarsNeverGrowWiderThanTheMaximum()
    {
        var slots = HistoryChartMath.Slots(2, 300);
        Assert.Equal(28, slots.BarWidth, 9);
        Assert.Equal(61, slots.Left(0), 9);

        Assert.Equal(12, HistoryChartMath.Slots(2, 300, maxBarWidth: 12).BarWidth, 9);
    }

    [Theory]
    [InlineData(5, 20, 2.88)]
    [InlineData(4, 14, 2.5)]
    public void NarrowSlotsKeepAOnePixelGap(int count, double width, double bar)
    {
        Assert.Equal(bar, HistoryChartMath.Slots(count, width).BarWidth, 9);
    }

    [Fact]
    public void DenseRangesDrawTouchingBars()
    {
        var slots = HistoryChartMath.Slots(600, 800);
        Assert.Equal(800 / 600.0, slots.Slot, 9);
        Assert.Equal(slots.Slot, slots.BarWidth, 9);
        Assert.Equal(slots.Slot * 5, slots.Left(5), 9);

        var subpixel = HistoryChartMath.Slots(2000, 1000);
        Assert.Equal(0.5, subpixel.BarWidth, 9);
    }

    [Fact]
    public void SlotsWithoutRoomAreEmpty()
    {
        Assert.Equal(new BarSlots(0, 0, 0), HistoryChartMath.Slots(0, 100));
        Assert.Equal(new BarSlots(0, 0, 3), HistoryChartMath.Slots(3, 0));
        Assert.Equal(new BarSlots(0, 0, 3), HistoryChartMath.Slots(3, double.NaN));
        Assert.Equal(new BarSlots(0, 0, 0), HistoryChartMath.Slots(-2, 100));
    }

    [Fact]
    public void SlotsSurviveAnUnusableMaximumWidth()
    {
        Assert.Equal(1, HistoryChartMath.Slots(2, 300, maxBarWidth: 0.5).BarWidth, 9);
        Assert.Equal(1, HistoryChartMath.Slots(2, 300, maxBarWidth: 0).BarWidth, 9);
        Assert.Equal(108, HistoryChartMath.Slots(2, 300, maxBarWidth: double.NaN).BarWidth, 9);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(29.9, 0)]
    [InlineData(30, 1)]
    [InlineData(299, 9)]
    [InlineData(5000, 9)]
    [InlineData(double.NaN, 0)]
    public void IndexAtClampsToTheRange(double x, int index)
    {
        Assert.Equal(index, HistoryChartMath.Slots(10, 300).IndexAt(x));
    }

    [Fact]
    public void IndexAtWithoutSlotsIsZero()
    {
        Assert.Equal(0, HistoryChartMath.Slots(0, 300).IndexAt(10));
        Assert.Equal(0, HistoryChartMath.Slots(4, 0).IndexAt(10));
    }

    [Fact]
    public void StackAccumulatesFromTheBaseline()
    {
        Assert.Equal([20.0, 60.0, 120.0], HistoryChartMath.Stack([10, 20, 30], 100, 200));
        Assert.Equal([160.0, 200.0], HistoryChartMath.Stack([80, 50], 100, 200));
        Assert.Equal([0.0, 20.0, 20.0], HistoryChartMath.Stack([-5, 10, double.NaN], 100, 200));
        Assert.Equal([0.0, 0.0], HistoryChartMath.Stack([10, 20], 0, 200));
        Assert.Equal([0.0, 0.0], HistoryChartMath.Stack([10, 20], 100, 0));
        Assert.Empty(HistoryChartMath.Stack([], 100, 200));
    }

    [Fact]
    public void RunsBreakWhereValuesAreMissing()
    {
        Assert.Equal([(0, 1), (2, 2), (6, 1)], HistoryChartMath.Runs(new double?[] { 1, null, 2, 3, null, null, 4 }));
        Assert.Equal([(1, 3)], HistoryChartMath.Runs(new double?[] { null, 1, 2, 3 }));
        Assert.Equal([(0, 1), (2, 1)], HistoryChartMath.Runs(new double?[] { 5, double.NaN, 6 }));
        Assert.Empty(HistoryChartMath.Runs(new double?[] { null, null }));
        Assert.Empty(HistoryChartMath.Runs(Array.Empty<double?>()));
    }

    [Fact]
    public void MarksForThirtyDays()
    {
        var range = new DayRange(D(2026, 9, 22), D(2026, 10, 21));
        var marks = HistoryChartMath.Marks(range, 20);

        Assert.Equal([9], marks.Rules);
        Assert.Equal(
            [new AxisLabel(0, D(2026, 9, 22), AxisLabelKind.Month), new AxisLabel(9, D(2026, 10, 1), AxisLabelKind.Month)],
            marks.Months);
        Assert.Equal(Enumerable.Range(0, 15).Select(i => 1 + 2 * i), marks.Days.Select(d => d.Index));
        Assert.Equal(29, marks.Days[^1].Index);
        Assert.Equal(Today, marks.Days[^1].Day);
        Assert.All(marks.Days, d => Assert.Equal(AxisLabelKind.Day, d.Kind));
        Assert.All(marks.Days, d => Assert.Equal(range.DayAt(d.Index), d.Day));
    }

    [Fact]
    public void LeadingMonthLabelIsDroppedNextToABoundary()
    {
        var marks = HistoryChartMath.Marks(new DayRange(D(2026, 9, 29), D(2026, 10, 28)), 10);
        Assert.Equal([new AxisLabel(2, D(2026, 10, 1), AxisLabelKind.Month)], marks.Months);
    }

    [Fact]
    public void RangesInsideOneMonthStillNameTheMonth()
    {
        var range = new DayRange(D(2026, 10, 15), Today);
        var marks = HistoryChartMath.Marks(range, 100);
        Assert.Empty(marks.Rules);
        Assert.Equal([new AxisLabel(0, D(2026, 10, 15), AxisLabelKind.Month)], marks.Months);
        Assert.Equal(Enumerable.Range(0, 7), marks.Days.Select(d => d.Index));
    }

    [Fact]
    public void NewYearIsMarkedAsAYearLabel()
    {
        var marks = HistoryChartMath.Marks(new DayRange(D(2025, 12, 15), D(2026, 1, 20)), 16);
        var boundary = Assert.Single(marks.Rules);
        Assert.Equal(17, boundary);
        Assert.Contains(new AxisLabel(17, D(2026, 1, 1), AxisLabelKind.Year), marks.Months);
    }

    [Fact]
    public void LongRangesThinMonthLabelsButKeepEveryRule()
    {
        var range = new DayRange(D(2025, 1, 1), D(2026, 12, 31));
        var marks = HistoryChartMath.Marks(range, 1);

        Assert.Equal(23, marks.Rules.Count);
        Assert.All(marks.Rules, i => Assert.Equal(1, range.DayAt(i).Day));
        var labelled = marks.Months.Where(m => m.Index > 0).Select(m => m.Day.Month).ToArray();
        Assert.All(labelled, month => Assert.Equal(1, month % 2));
        Assert.Contains(new AxisLabel(range.IndexOf(D(2026, 1, 1)), D(2026, 1, 1), AxisLabelKind.Year), marks.Months);
        Assert.Empty(marks.Days);
    }

    [Fact]
    public void VeryDenseRangesOnlyLabelYears()
    {
        var range = new DayRange(D(2023, 1, 1), D(2026, 12, 31));
        var marks = HistoryChartMath.Marks(range, 0.1);

        var years = marks.Months.Where(m => m.Index > 0).ToArray();
        Assert.All(years, m => Assert.Equal(AxisLabelKind.Year, m.Kind));
        Assert.Equal([2024, 2025, 2026], years.Select(m => m.Day.Year));
        Assert.Equal(years.Select(m => m.Index), marks.Rules);
    }

    [Fact]
    public void LabelsAreNeverCloserThanTheMinimumSpacing()
    {
        var random = new Random(5);
        for (var trial = 0; trial < 300; trial++)
        {
            var start = D(2024, 1, 1).AddDays(random.Next(0, 600));
            var range = new DayRange(start, start.AddDays(random.Next(0, 800)));
            var slot = 0.2 + random.NextDouble() * 60;
            var marks = HistoryChartMath.Marks(range, slot);

            var days = marks.Days.Select(d => d.Index).ToArray();
            for (var i = 1; i < days.Length; i++)
            {
                Assert.True((days[i] - days[i - 1]) * slot >= 44 * 0.75 - 1e-9);
            }

            if (days.Length > 0)
            {
                Assert.Equal(range.DayCount - 1, days[^1]);
            }

            var boundaries = marks.Months.Where(m => m.Index > 0).Select(m => m.Index).ToArray();
            for (var i = 1; i < boundaries.Length; i++)
            {
                Assert.True((boundaries[i] - boundaries[i - 1]) * slot >= 44 * 0.9);
            }
        }
    }

    [Fact]
    public void MarksWithoutRoomAreEmpty()
    {
        var empty = HistoryChartMath.Marks(new DayRange(D(2026, 1, 1), D(2026, 3, 1)), 0);
        Assert.Empty(empty.Rules);
        Assert.Empty(empty.Months);
        Assert.Empty(empty.Days);
        Assert.Empty(HistoryChartMath.Marks(new DayRange(D(2026, 1, 1), D(2026, 3, 1)), double.NaN).Months);
    }

    [Fact]
    public void BlendInterpolatesTowardTheTarget()
    {
        Assert.Equal([5.0, 15.0], HistoryChartMath.Blend([0, 10], [10, 20], 0.5));
        Assert.Equal([0.0, 10.0], HistoryChartMath.Blend([0, 10], [10, 20], 0));
        Assert.Equal([10.0, 20.0], HistoryChartMath.Blend([0, 10], [10, 20], 1));
        Assert.Equal([7.0, 10.0], HistoryChartMath.Blend([4], [10, 20], 0.5));
        Assert.Equal([10.0], HistoryChartMath.Blend([4, 99, 99], [10], 1));
        Assert.Equal([12.0], HistoryChartMath.Blend([10], [20], 0.2));
    }
}
