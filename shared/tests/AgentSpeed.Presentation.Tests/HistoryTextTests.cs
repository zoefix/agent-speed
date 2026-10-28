using AgentSpeed.Core;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;
using static AgentSpeed.Presentation.Tests.TestSupport.HistoryData;

namespace AgentSpeed.Presentation.Tests;

public class HistoryTextTests
{
    private static readonly Localizer ZhHant = For(LanguageCodes.ZhHant);
    private static readonly Localizer ZhHans = For(LanguageCodes.ZhHans);
    private static readonly Localizer En = For(LanguageCodes.En);
    private static readonly Localizer Ja = For(LanguageCodes.Ja);

    private static Localizer Language(string code) => code switch
    {
        LanguageCodes.ZhHans => ZhHans,
        LanguageCodes.En => En,
        LanguageCodes.Ja => Ja,
        _ => ZhHant,
    };

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "2026/9/22 – 10/21 · 30 天")]
    [InlineData(LanguageCodes.ZhHans, "2026/9/22 – 10/21 · 30 天")]
    [InlineData(LanguageCodes.En, "Sep 22 – Oct 21, 2026 · 30 days")]
    [InlineData(LanguageCodes.Ja, "2026/9/22 – 10/21 · 30 日間")]
    public void RangeSubtitleWritesTheYearOnce(string language, string expected)
    {
        var range = new DayRange(D(2026, 9, 22), D(2026, 10, 21));
        Assert.Equal(expected, HistoryText.RangeSubtitle(range, Today, Language(language)));
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "2025/12/30 – 2026/1/2 · 4 天")]
    [InlineData(LanguageCodes.ZhHans, "2025/12/30 – 2026/1/2 · 4 天")]
    [InlineData(LanguageCodes.En, "Dec 30, 2025 – Jan 2, 2026 · 4 days")]
    [InlineData(LanguageCodes.Ja, "2025/12/30 – 2026/1/2 · 4 日間")]
    public void RangeSubtitleAcrossYearsWritesBothYears(string language, string expected)
    {
        var range = new DayRange(D(2025, 12, 30), D(2026, 1, 2));
        Assert.Equal(expected, HistoryText.RangeSubtitle(range, Today, Language(language)));
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "今天 · 2026/10/21（週三）")]
    [InlineData(LanguageCodes.ZhHans, "今天 · 2026/10/21（周三）")]
    [InlineData(LanguageCodes.En, "Today · Oct 21, 2026, Wed")]
    [InlineData(LanguageCodes.Ja, "今日 · 2026/10/21（水）")]
    public void RangeSubtitleSpellsOutToday(string language, string expected)
    {
        Assert.Equal(expected, HistoryText.RangeSubtitle(DayRange.Single(Today), Today, Language(language)));
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "昨天 · 2026/10/20（週二）")]
    [InlineData(LanguageCodes.En, "Yesterday · Oct 20, 2026, Tue")]
    [InlineData(LanguageCodes.Ja, "昨日 · 2026/10/20（火）")]
    public void RangeSubtitleSpellsOutYesterday(string language, string expected)
    {
        Assert.Equal(expected, HistoryText.RangeSubtitle(DayRange.Single(D(2026, 10, 20)), Today, Language(language)));
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "2026/10/14（週三）")]
    [InlineData(LanguageCodes.En, "Oct 14, 2026, Wed")]
    public void RangeSubtitleForAnotherDayNamesTheWeekday(string language, string expected)
    {
        Assert.Equal(expected, HistoryText.RangeSubtitle(DayRange.Single(D(2026, 10, 14)), Today, Language(language)));
    }

    [Fact]
    public void VersusThePreviousDay()
    {
        Assert.Equal("比前一天 +12%", HistoryText.VersusPreviousDay(0.12, ZhHant));
        Assert.Equal("比前一天 −8%", HistoryText.VersusPreviousDay(-0.08, ZhHans));
        Assert.Equal("+12% vs the day before", HistoryText.VersusPreviousDay(0.12, En));
        Assert.Equal("前日比 0%", HistoryText.VersusPreviousDay(0, Ja));
    }

    [Fact]
    public void BreakdownBarsAreNamedForScreenReaders()
    {
        var source = new BreakdownRow(SourceKind.Codex, null, 312_345, 151, 0.38, 1, 88.2);
        Assert.Equal("Codex：輸出：312,345，佔比：38%，平均速度：88 tok/s，請求：151", HistoryText.Accessible(source, ZhHant));
        Assert.Equal("Codex: Output: 312,345, Share: 38%, Avg speed: 88 tok/s, Requests: 151", HistoryText.Accessible(source, En));
        Assert.Equal("Output: 312,345, Share: 38%, Avg speed: No measurement, Requests: 151",
            HistoryText.BreakdownValue(source with { Speed = null }, En));

        var model = new BreakdownRow(SourceKind.Codex, "gpt-6.1-sol", 900, 3, 0.004, 0.2, 45);
        Assert.Equal("gpt-6.1-sol: Output: 900, Share: <1%, Avg speed: 45 tok/s, Requests: 3", HistoryText.Accessible(model, En));
        var other = new BreakdownRow(SourceKind.Codex, null, 120, 1, 0.1, 0.1, null, OtherModels: true);
        Assert.Equal("其他模型", HistoryText.BreakdownName(other, ZhHant));
        Assert.Equal("Other models", HistoryText.BreakdownName(other, En));
        Assert.Equal("その他のモデル", HistoryText.BreakdownName(other, Ja));
        Assert.Equal("Antigravity", HistoryText.BreakdownName(source with { Source = SourceKind.Antigravity }, En));
    }

    [Fact]
    public void ModelsNameTheirSpeedWhenMeasured()
    {
        Assert.Equal("claude-opus-5-5 · 83 tok/s", HistoryText.ModelWithSpeed(new ModelUsage("claude-opus-5-5", 4, 900, 830, 10_000), En));
        Assert.Equal("gpt-5.5", HistoryText.ModelWithSpeed(new ModelUsage("gpt-5.5", 4, 900), En));
        Assert.Equal("gpt-5.5", HistoryText.ModelWithSpeed(new ModelUsage("gpt-5.5", 4, 900, 0, 5_000), En));
    }

    [Theory]
    [InlineData(1, "1 天", "1 day", "1 日間")]
    [InlineData(2, "2 天", "2 days", "2 日間")]
    [InlineData(366, "366 天", "366 days", "366 日間")]
    public void DayCounts(int count, string traditional, string english, string japanese)
    {
        Assert.Equal(traditional, HistoryText.Days(count, ZhHant));
        Assert.Equal(english, HistoryText.Days(count, En));
        Assert.Equal(japanese, HistoryText.Days(count, Ja));
    }

    [Fact]
    public void DatesInEveryLanguage()
    {
        var day = D(2026, 10, 3);
        Assert.Equal("2026/10/3", HistoryText.Date(day, ZhHant));
        Assert.Equal("Oct 3, 2026", HistoryText.Date(day, En));
        Assert.Equal("2026/10/3", HistoryText.Date(day, Ja));
        Assert.Equal("10/3", HistoryText.MonthDay(day, ZhHant));
        Assert.Equal("10/3", HistoryText.MonthDay(day, ZhHans));
        Assert.Equal("Oct 3", HistoryText.MonthDay(day, En));
        Assert.Equal("10/3", HistoryText.MonthDay(day, Ja));
    }

    [Fact]
    public void ShortDateKeepsTheYearOutsideTheCurrentYear()
    {
        Assert.Equal("10/3", HistoryText.ShortDate(D(2026, 10, 3), Today, ZhHant));
        Assert.Equal("2025/10/3", HistoryText.ShortDate(D(2025, 10, 3), Today, ZhHant));
        Assert.Equal("Oct 3, 2025", HistoryText.ShortDate(D(2025, 10, 3), Today, En));
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "週二")]
    [InlineData(LanguageCodes.ZhHans, "周二")]
    [InlineData(LanguageCodes.En, "Tue")]
    [InlineData(LanguageCodes.Ja, "火")]
    public void WeekdayFollowsTheLanguage(string language, string expected)
    {
        Assert.Equal(expected, HistoryText.Weekday(D(2026, 10, 20), Language(language)));
    }

    [Fact]
    public void TableDateNamesTodayAndYesterday()
    {
        Assert.Equal("今天", HistoryText.TableDate(Today, Today, ZhHant));
        Assert.Equal("昨天", HistoryText.TableDate(Today.AddDays(-1), Today, ZhHant));
        Assert.Equal("Today", HistoryText.TableDate(Today, Today, En));
        Assert.Equal("Yesterday", HistoryText.TableDate(Today.AddDays(-1), Today, En));
        Assert.Equal("今日", HistoryText.TableDate(Today, Today, Ja));
        Assert.Equal("昨日", HistoryText.TableDate(Today.AddDays(-1), Today, Ja));
    }

    [Theory]
    [InlineData(LanguageCodes.ZhHant, "10/19（週一）", "2025/10/19（週日）")]
    [InlineData(LanguageCodes.ZhHans, "10/19（周一）", "2025/10/19（周日）")]
    [InlineData(LanguageCodes.En, "Oct 19, Mon", "Oct 19, 2025, Sun")]
    [InlineData(LanguageCodes.Ja, "10/19（月）", "2025/10/19（日）")]
    public void TableDateShowsTheWeekday(string language, string thisYear, string lastYear)
    {
        var loc = Language(language);
        Assert.Equal(thisYear, HistoryText.TableDate(D(2026, 10, 19), Today, loc));
        Assert.Equal(lastYear, HistoryText.TableDate(D(2025, 10, 19), Today, loc));
    }

    [Fact]
    public void LongDateHasTheYearAndWeekday()
    {
        Assert.Equal("2026/10/20（週二）", HistoryText.LongDate(D(2026, 10, 20), ZhHant));
        Assert.Equal("Oct 20, 2026, Tue", HistoryText.LongDate(D(2026, 10, 20), En));
        Assert.Equal("2026/10/20（火）", HistoryText.LongDate(D(2026, 10, 20), Ja));
    }

    [Fact]
    public void MonthLabels()
    {
        Assert.Equal("10月", HistoryText.Month(D(2026, 10, 1), false, ZhHant));
        Assert.Equal("2026年1月", HistoryText.Month(D(2026, 1, 1), true, ZhHant));
        Assert.Equal("Oct", HistoryText.Month(D(2026, 10, 1), false, En));
        Assert.Equal("Jan 2026", HistoryText.Month(D(2026, 1, 1), true, En));
        Assert.Equal("10月", HistoryText.Month(D(2026, 10, 1), false, Ja));
        Assert.Equal("2026年1月", HistoryText.Month(D(2026, 1, 1), true, Ja));
    }

    [Fact]
    public void FullNumbersAreGrouped()
    {
        Assert.Equal("1,234,567", HistoryText.FullNumber(1_234_567, ZhHant));
        Assert.Equal("1,234,567", HistoryText.FullNumber(1_234_567, En));
        Assert.Equal("0", HistoryText.FullNumber(0, Ja));
    }

    [Fact]
    public void SpeedHasAUnitOrSaysThereIsNoMeasurement()
    {
        Assert.Equal("85 tok/s", HistoryText.Speed(85.4, En));
        Assert.Equal("1.28k tok/s", HistoryText.Speed(1280, ZhHant));
        Assert.Equal("4.2 tok/s", HistoryText.Speed(4.2, Ja));
        Assert.Equal("無實測", HistoryText.Speed(null, ZhHant));
        Assert.Equal("No measurement", HistoryText.Speed(0, En));
        Assert.Equal("実測なし", HistoryText.Speed(double.NaN, Ja));
        Assert.Equal("无实测", HistoryText.Speed(-3, ZhHans));
    }

    [Theory]
    [InlineData(0.92, "92%")]
    [InlineData(0.125, "13%")]
    [InlineData(0.0, "0%")]
    [InlineData(0.004, "<1%")]
    [InlineData(0.005, "1%")]
    [InlineData(1.0, "100%")]
    [InlineData(1.5, "100%")]
    [InlineData(-0.2, "0%")]
    public void PercentIsWhole(double ratio, string expected)
    {
        Assert.Equal(expected, HistoryText.Percent(ratio));
    }

    [Fact]
    public void PercentWithoutAValueIsADash()
    {
        Assert.Equal("—", HistoryText.Percent(null));
        Assert.Equal("—", HistoryText.Percent(double.NaN));
    }

    [Theory]
    [InlineData(0.12, "+12%")]
    [InlineData(-0.08, "−8%")]
    [InlineData(0.004, "+0.4%")]
    [InlineData(-0.004, "−0.4%")]
    [InlineData(0.0004, "0%")]
    [InlineData(0.0, "0%")]
    [InlineData(-1.0, "−100%")]
    [InlineData(12.3456, "+1,235%")]
    [InlineData(double.NaN, "—")]
    public void ChangeIsSigned(double ratio, string expected)
    {
        Assert.Equal(expected, HistoryText.Change(ratio));
    }

    [Fact]
    public void ChangeIgnoresTheCurrentCulture()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("+0.4%", HistoryText.Change(0.004));
            Assert.Equal("+1,235%", HistoryText.Change(12.3456));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void SecondaryLines()
    {
        Assert.Equal("比前一段期間 +12%", HistoryText.VersusPrevious(0.12, ZhHant));
        Assert.Equal("+12% vs previous period", HistoryText.VersusPrevious(0.12, En));
        Assert.Equal("活躍 18 天", HistoryText.ActiveDays(18, ZhHant));
        Assert.Equal("Active on 18 days", HistoryText.ActiveDays(18, En));
        Assert.Equal("單日最高 10/3", HistoryText.BusiestDay(D(2026, 10, 3), Today, ZhHant));
        Assert.Equal("Busiest day Oct 3", HistoryText.BusiestDay(D(2026, 10, 3), Today, En));
        Assert.Equal("單日最高 2025/10/3", HistoryText.BusiestDay(D(2025, 10, 3), Today, ZhHant));
    }

    [Fact]
    public void TopModelsShowTwoNamesAndCountTheRest()
    {
        ModelUsage[] models = [new("opus", 1, 30), new("gpt", 1, 20), new("flash", 1, 10), new("pro", 1, 5)];
        Assert.Equal("opus, gpt +2", HistoryText.TopModels(models, ZhHant));
        Assert.Equal("opus, gpt, flash +1", HistoryText.TopModels(models, En, 3));
        Assert.Equal("opus +3", HistoryText.TopModels(models, En, 0));
        Assert.Equal("opus, gpt", HistoryText.TopModels(models[..2], Ja));
        Assert.Equal("opus", HistoryText.TopModels(models[..1], Ja));
        Assert.Equal("—", HistoryText.TopModels([], En));
    }

    [Fact]
    public void UpdatedIsRelativeWithinTheHour()
    {
        var now = Local(2026, 10, 21, 15, 0);
        Assert.Equal("更新於 剛剛", HistoryText.Updated(now, now, ZhHant));
        Assert.Equal("更新於 剛剛", HistoryText.Updated(now.AddMinutes(3), now, ZhHant));
        Assert.Equal("更新於 30 秒前", HistoryText.Updated(now.AddSeconds(-30), now, ZhHant));
        Assert.Equal("更新於 5 分鐘前", HistoryText.Updated(now.AddMinutes(-5), now, ZhHant));
        Assert.Equal("Updated 59m ago", HistoryText.Updated(now.AddMinutes(-59), now, En));
    }

    [Fact]
    public void UpdatedShowsTheTimeThenTheDate()
    {
        var now = Local(2026, 10, 21, 15, 0);
        var earlier = now.AddHours(-2);
        Assert.Equal($"更新於 {earlier.ToLocalTime().ToString("t", ZhHant.Culture)}", HistoryText.Updated(earlier, now, ZhHant));

        var before = now.AddDays(-2);
        Assert.Equal($"Updated Oct 19, 2026 {before.ToLocalTime().ToString("t", En.Culture)}", HistoryText.Updated(before, now, En));
    }

    [Fact]
    public void AnalyzingProgress()
    {
        Assert.Equal("正在分析歷史紀錄… 42%", HistoryText.Analyzing(0.425, ZhHant));
        Assert.Equal("Analyzing history… 100%", HistoryText.Analyzing(1.7, En));
        Assert.Equal("Analyzing history… 0%", HistoryText.Analyzing(double.NaN, En));
        Assert.Equal("Analyzing history… 0%", HistoryText.Analyzing(-1, En));
        Assert.Equal("12 / 340 個檔案", HistoryText.AnalyzingFiles(12, 340, ZhHant));
        Assert.Equal("12 of 340 files", HistoryText.AnalyzingFiles(12, 340, En));
        Assert.Equal("0 of 0 files", HistoryText.AnalyzingFiles(-1, -5, En));
    }

    [Fact]
    public void LabelledAndJoinedParts()
    {
        Assert.Equal("輸出：1.2M", HistoryText.Labelled("輸出", "1.2M", ZhHant));
        Assert.Equal("Output: 1.2M", HistoryText.Labelled("Output", "1.2M", En));
        Assert.Equal("a，b", HistoryText.JoinParts(["a", "", "b"], ZhHant));
        Assert.Equal("a, b", HistoryText.JoinParts(["a", "b"], En));
        Assert.Equal("a、b", HistoryText.JoinParts(["a", "b"], Ja));
    }

    [Fact]
    public void AccessibleSentenceNamesEveryValue()
    {
        var day = Summary(D(2026, 10, 20), output: 1_234_567, requests: 1234, input: 2_000_000, cached: 1_800_000,
            measuredOutput: 85_000, measuredMs: 1_000_000, peak: 412, agents: 3,
            models: [new ModelUsage("opus", 1, 900_000, 80_000, 1_000_000), new ModelUsage("gpt", 1, 300_000)]);
        var text = HistoryText.Accessible(day, Today, ZhHant);

        Assert.StartsWith("2026/10/20（週二）：", text, StringComparison.Ordinal);
        Assert.Contains("輸出：1.23M", text, StringComparison.Ordinal);
        Assert.Contains("輸入（含快取）：2.00M", text, StringComparison.Ordinal);
        Assert.Contains("請求：1,234", text, StringComparison.Ordinal);
        Assert.Contains("快取命中：90%", text, StringComparison.Ordinal);
        Assert.Contains("平均速度：85 tok/s", text, StringComparison.Ordinal);
        Assert.Contains("峰值：412 tok/s", text, StringComparison.Ordinal);
        Assert.Contains("Agent：3", text, StringComparison.Ordinal);
        Assert.Contains("主要模型：opus · 80 tok/s, gpt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("進行中", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessibleSentenceCountsTheModelsBeyondThree()
    {
        var day = Summary(D(2026, 10, 20), models:
        [
            new ModelUsage("a", 1, 400), new ModelUsage("b", 1, 300), new ModelUsage("c", 1, 200), new ModelUsage("d", 1, 100),
            new ModelUsage("e", 1, 50),
        ]);
        Assert.Contains("Top models: a, b, c, +2", HistoryText.Accessible(day, Today, En), StringComparison.Ordinal);
    }

    [Fact]
    public void AccessibleSentenceForTodayWithoutPeakOrModels()
    {
        var day = Summary(Today, measuredMs: 0);
        var text = HistoryText.Accessible(day, Today, En);

        Assert.StartsWith("Oct 21, 2026, Wed In progress: ", text, StringComparison.Ordinal);
        Assert.Contains("Avg speed: No measurement", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Peak", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Top models", text, StringComparison.Ordinal);
    }

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
