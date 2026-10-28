using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;
using static AgentSpeed.Presentation.Tests.TestSupport.HistoryData;

namespace AgentSpeed.Presentation.Tests;

public class HistorySummaryTests
{
    private static readonly DayRange Week = new(D(2026, 10, 15), Today);

    private static readonly DaySummary[] Days =
    [
        Summary(D(2026, 10, 16), output: 1000, requests: 4, input: 10_000, measuredOutput: 1000, measuredMs: 10_000),
        Summary(D(2026, 10, 18), output: 3000, requests: 6, input: 50_000, measuredOutput: 2000, measuredMs: 40_000),
        Summary(D(2026, 10, 20), output: 3000, requests: 2, input: 20_000, measuredOutput: 0, measuredMs: 0),
    ];

    private static readonly DaySummary[] Previous =
    [
        Summary(D(2026, 10, 9), output: 5000, measuredOutput: 5000, measuredMs: 100_000),
    ];

    [Fact]
    public void FourCardsInOrder()
    {
        var cards = HistorySummary.Build(Days, Previous, Week, Today, For(LanguageCodes.ZhHant));
        Assert.Equal([SummaryMetric.Output, SummaryMetric.Input, SummaryMetric.Requests, SummaryMetric.Speed], cards.Select(c => c.Metric));
        Assert.Equal(["輸出 token", "輸入 token（含快取）", "請求", "平均速度"], cards.Select(c => c.Label));
        Assert.All(cards, c => Assert.Equal(Week.DayCount, c.Spark.Count));
    }

    [Fact]
    public void LabelKeysExistInEveryLanguage()
    {
        foreach (var language in LanguageCodes.All)
        {
            var loc = For(language);
            foreach (var metric in Enum.GetValues<SummaryMetric>())
            {
                Assert.NotEqual(HistorySummary.LabelKey(metric), loc[HistorySummary.LabelKey(metric)]);
            }
        }
    }

    [Fact]
    public void ValuesAreCompactWithExactTooltips()
    {
        var cards = HistorySummary.Build(Days, Previous, Week, Today, For(LanguageCodes.ZhHant));
        Assert.Equal(("7.00k", "7,000", string.Empty), (cards[0].Value, cards[0].FullValue, cards[0].Unit));
        Assert.Equal(("80.0k", "80,000"), (cards[1].Value, cards[1].FullValue));
        Assert.Equal(("12", "12"), (cards[2].Value, cards[2].FullValue));
        Assert.Equal(("60", "tok/s", "60 tok/s"), (cards[3].Value, cards[3].Unit, cards[3].FullValue));
    }

    [Fact]
    public void SecondaryLinesCompareWithThePreviousPeriod()
    {
        var cards = HistorySummary.Build(Days, Previous, Week, Today, For(LanguageCodes.ZhHant));

        Assert.Equal("比前一段期間 +40%", cards[0].Secondary);
        Assert.Equal(0.4, cards[0].Change!.Value, 9);
        Assert.Equal("單日最高 10/18", cards[1].Secondary);
        Assert.Null(cards[1].Change);
        Assert.Equal("活躍 3 天", cards[2].Secondary);
        Assert.Null(cards[2].Change);
        Assert.Equal("比前一段期間 +20%", cards[3].Secondary);
        Assert.Equal(0.2, cards[3].Change!.Value, 9);
    }

    [Fact]
    public void WithoutAPreviousPeriodOutputShowsTheBusiestDay()
    {
        var cards = HistorySummary.Build(Days, [], Week, Today, For(LanguageCodes.En));

        Assert.Equal("Busiest day Oct 20", cards[0].Secondary);
        Assert.Null(cards[0].Change);
        Assert.Equal("Busiest day Oct 18", cards[1].Secondary);
        Assert.Equal("Active on 3 days", cards[2].Secondary);
        Assert.Equal(string.Empty, cards[3].Secondary);
        Assert.Null(cards[3].Change);
    }

    [Fact]
    public void DecreasesAreNegativeChanges()
    {
        DaySummary[] busier = [Summary(D(2026, 10, 9), output: 14_000, measuredOutput: 14_000, measuredMs: 70_000)];
        var cards = HistorySummary.Build(Days, busier, Week, Today, For(LanguageCodes.En));
        Assert.Equal(-0.5, cards[0].Change!.Value, 9);
        Assert.Equal("−50% vs previous period", cards[0].Secondary);
        Assert.Equal(-0.7, cards[3].Change!.Value, 9);
    }

    [Fact]
    public void SparksHaveOneValuePerDayWithSpeedGaps()
    {
        var cards = HistorySummary.Build(Days, Previous, Week, Today, For(LanguageCodes.ZhHant));
        Assert.Equal(new double?[] { 0, 1000, 0, 3000, 0, 3000, 0 }, cards[0].Spark);
        Assert.Equal(new double?[] { 0, 10_000, 0, 50_000, 0, 20_000, 0 }, cards[1].Spark);
        Assert.Equal(new double?[] { 0, 4, 0, 6, 0, 2, 0 }, cards[2].Spark);
        Assert.Equal(new double?[] { null, 100, null, 50, null, null, null }, cards[3].Spark);
    }

    [Fact]
    public void EmptyRange()
    {
        var cards = HistorySummary.Build([], [], Week, Today, For(LanguageCodes.ZhHant));

        Assert.Equal("0", cards[0].Value);
        Assert.Equal(string.Empty, cards[0].Secondary);
        Assert.Equal("0", cards[1].Value);
        Assert.Equal(string.Empty, cards[1].Secondary);
        Assert.Equal(string.Empty, cards[2].Secondary);
        Assert.Equal("—", cards[3].Value);
        Assert.Equal(string.Empty, cards[3].Unit);
        Assert.Equal("無實測", cards[3].FullValue);
        Assert.All(cards, c => Assert.Null(c.Change));
        Assert.All(cards[3].Spark, Assert.Null);
        Assert.All(cards[0].Spark, v => Assert.Equal(0, v));
    }

    [Fact]
    public void DaysWithoutOutputNameNoBusiestDay()
    {
        DaySummary[] inputOnly = [Summary(D(2026, 10, 18), output: 0, requests: 1, input: 100, measuredMs: 0)];
        var cards = HistorySummary.Build(inputOnly, [], Week, Today, For(LanguageCodes.ZhHant));

        Assert.Equal(string.Empty, cards[0].Secondary);
        Assert.Equal("單日最高 10/18", cards[1].Secondary);
        Assert.Equal("活躍 1 天", cards[2].Secondary);
    }

    [Fact]
    public void ASingleDayComparesEveryCardWithTheDayBefore()
    {
        var day = DayRange.Single(Today);
        DaySummary[] today = [Summary(Today, output: 3000, requests: 6, input: 40_000, measuredOutput: 3000, measuredMs: 30_000)];
        DaySummary[] yesterday = [Summary(D(2026, 10, 20), output: 2000, requests: 8, input: 40_000, measuredOutput: 2000, measuredMs: 10_000)];
        var cards = HistorySummary.Build(today, yesterday, day, Today, For(LanguageCodes.ZhHant));

        Assert.Equal(["比前一天 +50%", "比前一天 0%", "比前一天 −25%", "比前一天 −50%"], cards.Select(c => c.Secondary));
        Assert.Equal(0.5, cards[0].Change!.Value, 9);
        Assert.Equal(0, cards[1].Change!.Value, 9);
        Assert.Equal(-0.25, cards[2].Change!.Value, 9);
        Assert.Equal(-0.5, cards[3].Change!.Value, 9);
        Assert.Equal("+50% vs the day before", HistorySummary.Build(today, yesterday, day, Today, For(LanguageCodes.En))[0].Secondary);
    }

    [Fact]
    public void ASingleDayWithoutTheDayBeforeHasNoSecondaryLines()
    {
        DaySummary[] today = [Summary(Today, output: 3000)];
        var cards = HistorySummary.Build(today, [], DayRange.Single(Today), Today, For(LanguageCodes.ZhHant));
        Assert.All(cards, c => Assert.Equal(string.Empty, c.Secondary));
        Assert.All(cards, c => Assert.Null(c.Change));
    }

    [Fact]
    public void ASingleDaysSparklinesShowItsWeekWithTheDayHighlighted()
    {
        var day = DayRange.Single(D(2026, 10, 20));
        DaySummary[] selected = [Summary(D(2026, 10, 20), output: 900, measuredOutput: 900, measuredMs: 9_000)];
        DaySummary[] week =
        [
            Summary(D(2026, 10, 14), output: 100, measuredOutput: 100, measuredMs: 1_000),
            Summary(D(2026, 10, 17), output: 400, measuredOutput: 0, measuredMs: 0),
            selected[0],
        ];
        var cards = HistorySummary.Build(selected, [], day, Today, For(LanguageCodes.ZhHant), week);

        Assert.All(cards, c => Assert.Equal(HistoryRanges.SingleDaySparkDays, c.Spark.Count));
        Assert.All(cards, c => Assert.Equal(6, c.Highlight));
        Assert.Equal(new double?[] { 100, 0, 0, 400, 0, 0, 900 }, cards[0].Spark);
        Assert.Equal(new double?[] { 100, null, null, null, null, null, 100 }, cards[3].Spark);
        Assert.Equal("900", cards[0].Value);
    }

    [Fact]
    public void SeveralDaysHighlightNoDay()
    {
        var cards = HistorySummary.Build(Days, Previous, Week, Today, For(LanguageCodes.ZhHant));
        Assert.All(cards, c => Assert.Null(c.Highlight));
    }
}
