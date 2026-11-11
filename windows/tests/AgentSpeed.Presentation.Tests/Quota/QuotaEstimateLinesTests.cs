using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Core.Network;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Estimate;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests.Quota;

/// <summary>The estimate on the dashboard's quota lines (DESIGN 4 <i>Percent</i>, <i>Amount</i>, <i>Tooltip</i>, <i>Accessibility</i>; QUOTAS 10.7, 10.8).</summary>
public sealed class QuotaEstimateLinesTests
{
    private const long Minute = 60_000;
    private const long Hour = 60 * Minute;
    private const long Now = 1794338773000;
    private const long Dollar = 1_000_000;
    private const string Host = "A0000000-0000-4000-8000-000000000001";
    private static readonly StringTable Table = Localizer.LoadEmbeddedTable();
    private static readonly Localizer En = new(Table, LanguageCodes.En);
    private static readonly Localizer ZhHant = new(Table, LanguageCodes.ZhHant);

    private static readonly QuotaWindow Week = QuotaWindow.Of(QuotaWindowKind.SevenDay, 86, Now + 50 * Hour);
    private static readonly QuotaWindow Five = QuotaWindow.Of(QuotaWindowKind.FiveHour, 41, Now + 3 * Hour);

    private static CostDisplay Cost(Localizer? loc = null, string currency = "USD", decimal? twd = null)
    {
        RatesInUse? rates = twd is { } rate
            ? new RatesInUse(
                new ExchangeRateSet("open.er-api.com", "https://open.er-api.com/v6/latest/USD", 1_792_627_200_000, 1_792_630_000_000,
                    new Dictionary<string, decimal> { ["USD"] = 1, ["TWD"] = rate }),
                CurrencyTable.Bundled.FallbackRates)
            : null;
        return new CostDisplay(loc ?? En, CurrencyTable.Bundled, rates) { Settings = CostSettings.Default with { Currency = currency } };
    }

    private static QuotaWindowEstimate Estimate(QuotaWindow window, double live = 86, long used = 161 * Dollar, long? cap = 5742 * Dollar,
        long? low = 5600 * Dollar, long? high = 5900 * Dollar, EstimateConfidence confidence = EstimateConfidence.High, double? coverage = 0.634,
        bool vendorPassed = false, long? resetsAtMs = -1, string? plan = "claude_max", string? tier = "default_claude_max_20x", int windows = 9) =>
        new(Now - Minute, live, live - window.UsedPercent >= 0.5, resetsAtMs == -1 ? window.ResetsAtMs : resetsAtMs, vendorPassed, used, 0, cap, low,
            high, confidence, coverage, cap is { } k ? (long)(k * live / 100) : null, windows, plan, tier);

    private static QuotaState State(SourceKind source, QuotaSnapshot snapshot, params (QuotaWindow Window, QuotaWindowEstimate Estimate)[] estimates) => new()
    {
        Snapshots = ImmutableDictionary<SourceKind, QuotaSnapshot>.Empty.Add(source, snapshot),
        Active = [source],
        Estimates = estimates.ToImmutableDictionary(e => new QuotaEstimateKey(null, source, EstimateNames.WindowId(e.Window)), e => e.Estimate),
    };

    private static IReadOnlyList<QuotaLine> Lines(params (QuotaWindow Window, QuotaWindowEstimate Estimate)[] estimates) =>
        Lines(En, Cost(), Now, estimates);

    private static IReadOnlyList<QuotaLine> Lines(Localizer loc, CostDisplay cost, long now, params (QuotaWindow Window, QuotaWindowEstimate Estimate)[] estimates)
    {
        var snapshot = new QuotaSnapshot(SourceKind.ClaudeCode, Now - Minute, [Five, Week]);
        var state = State(SourceKind.ClaudeCode, snapshot, estimates);
        return QuotaLines.Of(snapshot, now, loc, TimeZoneInfo.Utc, new QuotaEstimateSource(state, cost));
    }

    // MARK: Amount

    [Fact]
    public void TheAmountShowsTheUsedAmountThenTheEstimatedCapWithoutItsSymbol()
    {
        var week = Lines((Week, Estimate(Week)))[1];
        Assert.Equal("$161.00", week.Amount);
        Assert.Equal(" / ≈5,742.00", week.AmountCap);
    }

    [Fact]
    public void AnAccountWithOnlyALowerBoundShowsAtLeast()
    {
        var week = Lines((Week, Estimate(Week, cap: null, low: 737 * Dollar, high: null, confidence: EstimateConfidence.Low)))[1];
        Assert.Equal(("$161.00", " / ≥737.00"), (week.Amount, week.AmountCap));
    }

    [Fact]
    public void ConfidenceNoneShowsOnlyTheUsedAmountAndNothingWithoutSpend()
    {
        var none = Estimate(Week, cap: null, low: null, high: null, confidence: EstimateConfidence.None, coverage: null);
        var spent = Lines((Week, none))[1];
        Assert.Equal(("$161.00", string.Empty), (spent.Amount, spent.AmountCap));

        var idle = Lines((Week, none with { UsedMicroUsd = 0 }))[1];
        Assert.Equal((string.Empty, string.Empty), (idle.Amount, idle.AmountCap));
    }

    [Fact]
    public void ACapWithoutSpendStillShowsTheZero()
    {
        var week = Lines((Week, Estimate(Week, used: 0)))[1];
        Assert.Equal(("$0.00", " / ≈5,742.00"), (week.Amount, week.AmountCap));
    }

    [Fact]
    public void AWindowWithoutAnEstimateKeepsTodaysLine()
    {
        var lines = Lines((Week, Estimate(Week)));
        var five = lines[0];
        Assert.Equal((string.Empty, string.Empty, false, false), (five.Amount, five.AmountCap, five.Estimated, five.HasEstimate));
        Assert.Empty(five.Details);
        Assert.Equal("5h 41% used, resets in 3 h", five.Spoken);
        Assert.Equal(five.Spoken, five.CardSpoken);
    }

    [Fact]
    public void WithoutAMoneyFormatterTheLiveValuesStayAndTheAmountsGo()
    {
        var snapshot = new QuotaSnapshot(SourceKind.ClaudeCode, Now - Minute, [Week]);
        var state = State(SourceKind.ClaudeCode, snapshot, (Week, Estimate(Week, live: 88.6)));
        var line = Assert.Single(QuotaLines.Of(snapshot, Now, En, TimeZoneInfo.Utc, new QuotaEstimateSource(state, null)));
        Assert.Equal((89, true, string.Empty), (line.Percent, line.Estimated, line.Amount));
        Assert.DoesNotContain(line.Details, d => d.Contains('$', StringComparison.Ordinal));
    }

    // MARK: Live percent

    [Fact]
    public void AnExtrapolatedPercentLeadsWithApproxAndTheBarAndToneFollowIt()
    {
        var week = Lines((Week, Estimate(Week, live: 94.6)))[1];
        Assert.True(week.Estimated);
        Assert.Equal(95, week.Percent);
        Assert.Equal(0.946, week.Bar, 9);
        Assert.Equal(QuotaTone.Critical, week.Tone);
        Assert.Equal("95%", week.PercentText);
    }

    [Fact]
    public void AVendorValueNewerThanTheBuildRaisesTheLivePercentAndEndsTheApprox()
    {
        var snapshot = new QuotaSnapshot(SourceKind.ClaudeCode, Now - Minute, [Week with { UsedPercent = 90 }]);
        var state = State(SourceKind.ClaudeCode, snapshot, (Week, Estimate(Week, live: 88.4)));
        var line = Assert.Single(QuotaLines.Of(snapshot, Now, En, TimeZoneInfo.Utc, new QuotaEstimateSource(state, Cost())));
        Assert.Equal((90, false), (line.Percent, line.Estimated));
        Assert.True(line.HasEstimate);
    }

    [Fact]
    public void ALivePercentWithinHalfAPointIsTheVendorsAndNotEstimated()
    {
        var week = Lines((Week, Estimate(Week, live: 86.3)))[1];
        Assert.False(week.Estimated);
        Assert.Equal(86, week.Percent);
    }

    [Fact]
    public void AfterTheVendorsResetTheLineCountsToTheNextInstance()
    {
        var next = Week.ResetsAtMs!.Value + 7 * 24 * Hour;
        var later = Week.ResetsAtMs!.Value + 2 * Hour;
        var estimate = Estimate(Week, live: 3.2, used: 18 * Dollar, vendorPassed: true, resetsAtMs: next) with { Estimated = true };
        var week = Lines(En, Cost(), later, (Week, estimate))[1];
        Assert.Equal((3, true, true), (week.Percent, week.Estimated, week.Passed));
        Assert.Equal(QuotaText.ResetLong(next, later, En, TimeZoneInfo.Utc), week.ResetLong);
        Assert.Equal(QuotaText.ResetShort(next, later, En, TimeZoneInfo.Utc), week.ResetShort);
        Assert.Equal(1 - (next - later) / (10080 * 60_000.0), week.Pace!.Value, 9);
    }

    [Fact]
    public void AfterTheResetWithoutAFirstResponseTheLineShowsZeroWithoutAReset()
    {
        var after = Five.ResetsAtMs!.Value + Minute;
        var estimate = Estimate(Five, live: 0, used: 0, vendorPassed: true, resetsAtMs: null) with { Estimated = false };
        var five = Lines(En, Cost(), after, (Five, estimate))[0];
        Assert.Equal((0, false, string.Empty, string.Empty), (five.Percent, five.Estimated, five.ResetLong, five.ResetShort));
        Assert.Null(five.Pace);
        Assert.True(five.HasEstimate);
    }

    [Fact]
    public void AnEstimateWhoseResetHasPassedIsStaleAndTheVendorsValuesShow()
    {
        var after = Week.ResetsAtMs!.Value + Minute;
        var week = Lines(En, Cost(), after, (Week, Estimate(Week, live: 90)))[1];
        Assert.Equal((0, false, true, false), (week.Percent, week.Estimated, week.Passed, week.HasEstimate));
        Assert.Equal(string.Empty, week.Amount);
    }

    [Fact]
    public void AnEstimateFromBeforeTheVendorsNewValueAfterAResetIsStale()
    {
        var week = Lines((Week, Estimate(Week, live: 5, vendorPassed: true, resetsAtMs: Week.ResetsAtMs!.Value + 7 * 24 * Hour)))[1];
        Assert.Equal((86, false, false), (week.Percent, week.Estimated, week.HasEstimate));
    }

    // MARK: Tooltip

    [Fact]
    public void TheTooltipListsTheEstimateInDesignOrderWithTheNoteLast()
    {
        var week = Lines((Week, Estimate(Week, live: 88.4)))[1];
        Assert.Equal(
        [
            "$161.00 used on this computer",
            "Cap about $5,742.00 (range $5,600.00–$5,900.00)",
            "About $5,075.93 used by the whole account",
            "This computer saw 63% of this window's usage",
            "High confidence · Based on 9 windows",
            "Plan: claude_max · default_claude_max_20x",
            En["quota.estimate.live"],
        ], week.Details);
        Assert.Equal(En["quota.estimate.note"], week.Note);
        Assert.True(week.HasEstimate);
        Assert.StartsWith("Updated ", week.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void AtLeastLeavesOutTheRangeAndTheAccountAndUnknownsAreLeftOut()
    {
        var estimate = Estimate(Week, cap: null, low: 737 * Dollar, high: null, confidence: EstimateConfidence.Low, coverage: null, plan: null, tier: null,
            windows: 2);
        var week = Lines((Week, estimate))[1];
        Assert.Equal(["$161.00 used on this computer", "Cap at least $737.00", "Low confidence · Based on 2 windows"], week.Details);
    }

    [Fact]
    public void ThePublishedRangeAlwaysHoldsTheCapAndAPlanWithoutATierStandsAlone()
    {
        var week = Lines((Week, Estimate(Week, low: 5800 * Dollar, high: 5700 * Dollar, confidence: EstimateConfidence.Medium, coverage: 0.996, tier: null)))[1];
        Assert.Contains("Cap about $5,742.00 (range $5,742.00–$5,742.00)", week.Details);
        Assert.Contains("This computer saw 100% of this window's usage", week.Details);
        Assert.Contains("Medium confidence · Based on 9 windows", week.Details);
        Assert.Contains("Plan: claude_max", week.Details);
    }

    [Fact]
    public void AnotherCurrencyAddsTheUsdAmountToTheTooltip()
    {
        var loc = ZhHant;
        var week = Lines(loc, Cost(loc, "TWD", 32.5m), Now, (Week, Estimate(Week, used: 160 * Dollar)))[1];
        Assert.Equal("NT$5,200", week.Amount);
        Assert.Equal(" / ≈186,615", week.AmountCap);
        Assert.Equal("本機已用 NT$5,200（US$160.00）", week.Details[0]);
        Assert.StartsWith("上限約 NT$186,615（US$5,742.00）（範圍 NT$182,000–NT$191,750）", week.Details[1], StringComparison.Ordinal);
    }

    // MARK: Accessibility

    [Fact]
    public void TheRowSpeaksTheAmountWithTheCapAndTheEstimate()
    {
        var lines = Lines((Week, Estimate(Week, live: 88.4)));
        var window = En.Format("a11y.quota.window", "7d", 88, QuotaText.ResetLong(Week.ResetsAtMs!.Value, Now, En, TimeZoneInfo.Utc));
        Assert.Equal(window + ", 161.00 US Dollar used, cap about 5,742.00 US Dollar, estimated", lines[1].Spoken);
        Assert.Equal(window + ", estimated", lines[1].CardSpoken);
        Assert.Equal("5h 41% used, resets in 3 h, " + lines[1].Spoken, QuotaLines.Spoken(lines, En));
    }

    [Fact]
    public void AtLeastSpeaksTheUsedAmountAloneInChinese()
    {
        var loc = ZhHant;
        var estimate = Estimate(Week, cap: null, low: 737 * Dollar, high: null, confidence: EstimateConfidence.Low);
        var week = Lines(loc, Cost(loc), Now, (Week, estimate))[1];
        Assert.EndsWith("、已用 161.00 美元", week.Spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("美元", week.CardSpoken, StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowWithoutAnAmountSpeaksOnlyItsEstimate()
    {
        var none = Estimate(Week, live: 90, used: 0, cap: null, low: null, high: null, confidence: EstimateConfidence.None, coverage: null);
        var week = Lines((Week, none))[1];
        Assert.Equal(En.Format("a11y.quota.window", "7d", 90, week.ResetLong) + ", estimated", week.Spoken);
        Assert.Equal(week.Spoken, week.CardSpoken);
    }

    // MARK: Hosts

    [Fact]
    public void AHostRowReadsItsOwnEstimatesAndNotThisComputers()
    {
        RemoteQuotaReport Report(SourceKind source, params QuotaWindow[] windows) =>
            new(Host, new QuotaSnapshot(source, Now - 3 * Minute, windows), true, true, QuotaRouteStatus.Ok(Now - 3 * Minute));
        var state = new QuotaState
        {
            Snapshots = ImmutableDictionary<SourceKind, QuotaSnapshot>.Empty.Add(SourceKind.Codex, new QuotaSnapshot(SourceKind.Codex, Now, [Week])),
            Hosts = ImmutableDictionary<string, HostQuotaState>.Empty.Add(Host, new HostQuotaState
            {
                Reports = ImmutableDictionary<SourceKind, RemoteQuotaReport>.Empty
                    .Add(SourceKind.Codex, Report(SourceKind.Codex, Week))
                    .Add(SourceKind.Kimi, Report(SourceKind.Kimi, Five)),
            }),
            Estimates = ImmutableDictionary<QuotaEstimateKey, QuotaWindowEstimate>.Empty
                .Add(new QuotaEstimateKey(Host, SourceKind.Codex, EstimateNames.WindowId(Week)), Estimate(Week, live: 88.4, used: 42 * Dollar))
                .Add(new QuotaEstimateKey(null, SourceKind.Kimi, EstimateNames.WindowId(Five)), Estimate(Five, used: 9 * Dollar)),
        };

        var groups = HostQuotaGroups.Of(state, Host, Now, En, TimeZoneInfo.Utc, Cost());
        var codex = Assert.Single(groups[0].Lines);
        Assert.Equal(("$42.00", " / ≈5,742.00", true), (codex.Amount, codex.AmountCap, codex.Estimated));
        var kimi = Assert.Single(groups[1].Lines);
        Assert.Equal((string.Empty, false), (kimi.Amount, kimi.HasEstimate));
        Assert.Contains("42.00 US Dollar used, cap about 5,742.00 US Dollar, estimated", HostQuotaGroups.Spoken(groups, En), StringComparison.Ordinal);

        var local = QuotaLines.Of(state.Snapshots[SourceKind.Codex], Now, En, TimeZoneInfo.Utc, new QuotaEstimateSource(state, Cost()));
        Assert.False(Assert.Single(local).HasEstimate);
    }
}
