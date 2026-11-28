using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Estimate;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Routes;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests;

/// <summary>
/// The taskbar 額度 block (DESIGN 10, Menu bar and taskbar, <i>Quota</i>; issue #81): its stored choice and percent, the first
/// two windows used or remaining with their estimate mark, pale while old or signed out, red from 95 % used, <c>額度 –</c>
/// while unavailable, and the words the tooltip and the label read.
/// </summary>
public class BarQuotaBlockTests
{
    private const long Minute = 60_000;
    private const long Hour = 60 * Minute;
    private const long Now = 1794338773000;
    private static readonly StringTable Table = Localizer.LoadEmbeddedTable();
    private static readonly Localizer ZhHant = new(Table, LanguageCodes.ZhHant);
    private static readonly Localizer En = new(Table, LanguageCodes.En);
    private static readonly AppSettings Codex = AppSettings.Default with { BarQuota = SourceKind.Codex };

    private static readonly QuotaWindow Five = QuotaWindow.Of(QuotaWindowKind.FiveHour, 68, Now + 3 * Hour + 12 * Minute);
    private static readonly QuotaWindow Week = QuotaWindow.Of(QuotaWindowKind.SevenDay, 42, Now + 50 * Hour);

    private static QuotaState State(SourceKind source, long observedMs, params QuotaWindow[] windows) => new()
    {
        Snapshots = ImmutableDictionary<SourceKind, QuotaSnapshot>.Empty.Add(source, new QuotaSnapshot(source, observedMs, windows)),
    };

    private static BarQuotaBlock Block(AppSettings settings, QuotaState state, Localizer? loc = null) =>
        BarQuotaBlock.From(settings, state, Now, loc ?? ZhHant, TimeZoneInfo.Utc)!;

    private static string Reset(QuotaWindow window, Localizer loc) => QuotaText.ResetLong(window.ResetsAtMs!.Value, Now, loc, TimeZoneInfo.Utc);

    // MARK: Settings

    [Fact]
    public void TheChoiceIsOffAndUsedByDefaultAndStoredWithTheTaskbar()
    {
        Assert.Null(AppSettings.Default.BarQuota);
        Assert.Equal(BarQuotaPercent.Used, AppSettings.Default.BarQuotaPercent);
        Assert.Null(SettingsSerializer.Deserialize("{}").BarQuota);

        var json = SettingsSerializer.Serialize(AppSettings.Default);
        Assert.Contains("\"quota\": \"off\"", json, StringComparison.Ordinal);
        Assert.Contains("\"quotaPercent\": \"used\"", json, StringComparison.Ordinal);

        foreach (var source in QuotaSettings.Sources)
        {
            var settings = AppSettings.Default with { BarQuota = source, BarQuotaPercent = BarQuotaPercent.Remaining };
            var read = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
            Assert.Equal((source, BarQuotaPercent.Remaining), (read.BarQuota, read.BarQuotaPercent));
        }

        var stored = SettingsSerializer.Deserialize("""{"taskbar":{"quota":"qoderCli","quotaPercent":"remaining"}}""");
        Assert.Equal((SourceKind.QoderCli, BarQuotaPercent.Remaining), (stored.BarQuota, stored.BarQuotaPercent));
        Assert.Contains("\"quota\": \"claudeCode\"", SettingsSerializer.Serialize(AppSettings.Default with { BarQuota = SourceKind.ClaudeCode }),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"taskbar":{"quota":"cursor","quotaPercent":"left"}}""")]
    [InlineData("""{"taskbar":{"quota":"deepseek","quotaPercent":1}}""")]
    [InlineData("""{"taskbar":{"quota":true,"quotaPercent":null}}""")]
    [InlineData("""{"taskbar":{"quota":"Codex","quotaPercent":"Remaining"}}""")]
    public void AnUnknownOrNonQuotaValueReadsAsTheDefault(string json)
    {
        var read = SettingsSerializer.Deserialize(json);
        Assert.Equal(((SourceKind?)null, BarQuotaPercent.Used), (read.BarQuota, read.BarQuotaPercent));
    }

    [Fact]
    public void OnlyAQuotaSourceIsAChoice()
    {
        Assert.Equal(13, QuotaSettings.Sources.Count);
        Assert.Null((AppSettings.Default with { BarQuota = SourceKind.DeepSeek }).Validated().BarQuota);
        Assert.Equal(BarQuotaPercent.Used, (AppSettings.Default with { BarQuotaPercent = (BarQuotaPercent)7 }).Validated().BarQuotaPercent);
        Assert.Equal("off", BarQuotaSettings.Key(SourceKind.OpenCode));
        Assert.Equal(["codex", "claudeCode", "claudeDesktop", "gemini", "antigravity", "workbuddy", "zcode", "kimi", "grokBuild", "droid", "devin",
            "qoder", "qoderCli"], QuotaSettings.Sources.Select(s => BarQuotaSettings.Key(s)));
        Assert.All(QuotaSettings.Sources, s => Assert.Equal(s, BarQuotaSettings.Parse(BarQuotaSettings.Key(s))));
    }

    // MARK: Lines

    [Fact]
    public void NothingShowsWhileOffOrGlyphOnly()
    {
        var state = State(SourceKind.Codex, Now - Minute, Five, Week);
        Assert.Null(BarQuotaBlock.From(AppSettings.Default, state, Now, ZhHant));
        Assert.Null(BarQuotaBlock.From(Codex with { BarMode = BarDisplayMode.GlyphOnly }, state, Now, ZhHant));
        Assert.NotNull(BarQuotaBlock.From(Codex with { BarMode = BarDisplayMode.ValueOnly }, state, Now, ZhHant));
        Assert.Null(BarQuotaBlock.ShownSource(Codex with { BarMode = BarDisplayMode.GlyphOnly }));
        Assert.Equal(SourceKind.Codex, BarQuotaBlock.ShownSource(Codex));
    }

    [Fact]
    public void TheFirstTwoWindowsInQuotasOrderAreDrawn()
    {
        var model = QuotaWindow.Of(QuotaWindowKind.ModelWeek, 12, Now + 60 * Hour, name: "Fable");
        var block = Block(Codex, State(SourceKind.Codex, Now - Minute, model, Week, Five));
        Assert.Equal(BarQuotaForm.Windows, block.Form);
        Assert.Equal([("5h", "68%"), ("7d", "42%")], block.Lines.Select(l => (l.Label, l.Text)));
        Assert.False(block.Alone);
        Assert.All(block.Lines, l => Assert.False(l.Pale || l.Critical || l.Estimated));

        // Every window is in the tooltip and the words, the third one too.
        Assert.Equal(3, block.TooltipLines.Count);
        Assert.Equal("Fable 7d 已用 12%，" + Reset(model, ZhHant), block.TooltipLines[2]);
    }

    [Fact]
    public void OneWindowStandsAloneCentredLikeTheIntelligenceBlocksLineOne()
    {
        var block = Block(Codex, State(SourceKind.Codex, Now - Minute, Week));
        var line = Assert.Single(block.Lines);
        Assert.Equal(("7d", "42%"), (line.Label, line.Text));
        Assert.True(block.Alone);
        Assert.Equal(20 + 3.5, block.FirstBaseline(20, 7, 12));
        Assert.Equal(20 - 6 + 3.5, Block(Codex, State(SourceKind.Codex, Now - Minute, Five, Week)).FirstBaseline(20, 7, 12));
    }

    [Fact]
    public void RemainingIsAHundredMinusTheShownPercent()
    {
        var state = State(SourceKind.Codex, Now - Minute, Five, Week);
        var used = Block(Codex, state);
        var remaining = Block(Codex with { BarQuotaPercent = BarQuotaPercent.Remaining }, state);
        Assert.Equal(["68%", "42%"], used.Lines.Select(l => l.Text));
        Assert.Equal(["32%", "58%"], remaining.Lines.Select(l => l.Text));
        Assert.Equal("5h 已用 68%，3 小時 12 分後重置", used.TooltipLines[0]);
        Assert.Equal("5h 剩餘 32%，3 小時 12 分後重置", remaining.TooltipLines[0]);
        Assert.Equal("5h 32% left, resets in 3 h 12 min", Block(Codex with { BarQuotaPercent = BarQuotaPercent.Remaining }, state, En).TooltipLines[0]);

        // A passed reset reads 0 % used and 100 % remaining, and a window without a reset reads no reset.
        var passed = State(SourceKind.Codex, Now - Minute, Five with { ResetsAtMs = Now - Minute }, Week with { ResetsAtMs = null });
        var left = Block(Codex with { BarQuotaPercent = BarQuotaPercent.Remaining }, passed);
        Assert.Equal(["100%", "58%"], left.Lines.Select(l => l.Text));
        Assert.Equal(["5h 剩餘 100%", "7d 剩餘 58%"], left.TooltipLines);
        Assert.Equal(["5h 已用 0%", "7d 已用 42%"], Block(Codex, passed).TooltipLines);
    }

    [Fact]
    public void AnExtrapolatedPercentKeepsItsMarkEitherWay()
    {
        var estimate = new QuotaWindowEstimate(Now - Minute, 70.4, true, Five.ResetsAtMs, false, 0, 0, null, null, null, EstimateConfidence.None,
            null, null, 0, null, null);
        var state = State(SourceKind.Codex, Now - Minute, Five, Week) with
        {
            Estimates = ImmutableDictionary<QuotaEstimateKey, QuotaWindowEstimate>.Empty
                .Add(new QuotaEstimateKey(null, SourceKind.Codex, EstimateNames.WindowId(Five)), estimate),
        };
        var used = Block(Codex, state);
        Assert.Equal(("70%", true, "≈70%"), (used.Lines[0].Number, used.Lines[0].Estimated, used.Lines[0].Text));
        Assert.False(used.Lines[1].Estimated);
        Assert.Equal("5h 已用 70%，3 小時 12 分後重置、估計值", used.TooltipLines[0]);
        Assert.Equal("≈30%", Block(Codex with { BarQuotaPercent = BarQuotaPercent.Remaining }, state).Lines[0].Text);
        Assert.Equal("5h 70% used, resets in 3 h 12 min, estimated", Block(Codex, state, En).TooltipLines[0]);
    }

    // MARK: Colors

    [Fact]
    public void FromNinetyFivePercentUsedACurrentNumberIsRed()
    {
        var state = State(SourceKind.Codex, Now - Minute, Five with { UsedPercent = 95 }, Week with { UsedPercent = 94.4 });
        var block = Block(Codex, state);
        Assert.Equal([true, false], block.Lines.Select(l => l.Critical));

        // Remaining reads 5 %: still red, the used percent decides.
        var remaining = Block(Codex with { BarQuotaPercent = BarQuotaPercent.Remaining }, state);
        Assert.Equal(("5%", true), (remaining.Lines[0].Text, remaining.Lines[0].Critical));

        // An old value is pale and never red.
        var old = Block(Codex, State(SourceKind.Codex, Now - 2 * Hour, Five with { UsedPercent = 99 }));
        Assert.Equal((true, false), (old.Lines[0].Pale, old.Lines[0].Critical));
    }

    [Fact]
    public void AWindowOverAnHourOldIsPaleAndSaysHowOld()
    {
        var fresh = Block(Codex, State(SourceKind.Codex, Now - 59 * Minute, Five, Week));
        Assert.All(fresh.Lines, l => Assert.False(l.Pale));

        var old = Block(Codex, State(SourceKind.Codex, Now - 2 * Hour, Five, Week));
        Assert.All(old.Lines, l => Assert.True(l.Pale));
        Assert.Equal("5h 已用 68%，3 小時 12 分後重置 · 2 小時前更新", old.TooltipLines[0]);
        Assert.Equal("5h 68% used, resets in 3 h 12 min · updated 2 h ago", Block(Codex, State(SourceKind.Codex, Now - 2 * Hour, Five), En).TooltipLines[0]);

        // A joined snapshot's window carries its own time.
        var joined = Block(Codex, State(SourceKind.Codex, Now - Minute, Five with { ObservedMs = Now - 3 * Hour }, Week));
        Assert.Equal([true, false], joined.Lines.Select(l => l.Pale));
    }

    [Theory]
    [InlineData(QuotaStatusKind.NoCredential, true)]
    [InlineData(QuotaStatusKind.NoBrowser, true)]
    [InlineData(QuotaStatusKind.Expired, true)]
    [InlineData(QuotaStatusKind.Denied, true)]
    [InlineData(QuotaStatusKind.Failed, false)]
    [InlineData(QuotaStatusKind.NotRunning, false)]
    [InlineData(QuotaStatusKind.NetworkOff, false)]
    public void ASignInThatNoLongerWorksMakesEveryNumberPale(QuotaStatusKind kind, bool pale)
    {
        var kimi = AppSettings.Default with { BarQuota = SourceKind.Kimi };
        var state = State(SourceKind.Kimi, Now - Minute, Five with { UsedPercent = 97 }, Week) with
        {
            RouteStatuses = ImmutableDictionary<QuotaRoute, QuotaRouteStatus>.Empty.Add(QuotaRoute.Kimi, QuotaRouteStatus.Of(kind)),
        };
        var block = Block(kimi, state);
        Assert.All(block.Lines, l => Assert.Equal(pale, l.Pale));
        Assert.Equal(!pale, block.Lines[0].Critical);
        Assert.Equal(BarQuotaBlock.SignedOut(kind), pale);

        // The status is the tooltip's last line and ends the words.
        var status = QuotaStatusText.Text(QuotaRouteStatus.Of(kind), QuotaRoute.Kimi, Now, ZhHant, zone: TimeZoneInfo.Utc);
        Assert.Equal(status, block.TooltipLines[^1]);
        Assert.Equal(3, block.TooltipLines.Count);
        Assert.EndsWith("、" + status, block.Spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatusIsTheOneSettingsShows()
    {
        // The network switch off: every route that asks over the network says so, and a stale refusal no longer pales.
        var kimi = AppSettings.Default with { BarQuota = SourceKind.Kimi, NetworkEnabled = false };
        var denied = State(SourceKind.Kimi, Now - Minute, Five) with
        {
            RouteStatuses = ImmutableDictionary<QuotaRoute, QuotaRouteStatus>.Empty.Add(QuotaRoute.Kimi, QuotaRouteStatus.Denied("api.kimi.com")),
        };
        var off = Block(kimi, denied);
        Assert.Equal("網路已關閉（關於 → 網路）", off.TooltipLines[^1]);
        Assert.False(off.Lines[0].Pale);

        // ZCode says nothing before a key is saved.
        var zcode = AppSettings.Default with { BarQuota = SourceKind.ZCode, Quota = QuotaSettings.Default with { Zcode = true } };
        var noKey = QuotaState.Empty with
        {
            RouteStatuses = ImmutableDictionary<QuotaRoute, QuotaRouteStatus>.Empty.Add(QuotaRoute.Zcode, QuotaRouteStatus.Of(QuotaStatusKind.NoCredential)),
        };
        Assert.Empty(Block(zcode, noKey).TooltipLines);
        Assert.Equal(["找不到登入資料"], Block(zcode, noKey with { ZcodeKeySaved = true }).TooltipLines);
    }

    [Fact]
    public void AnOkStatusSaysNothingAndQoderCliReadsQodersRoute()
    {
        var kimi = AppSettings.Default with { BarQuota = SourceKind.Kimi };
        var ok = State(SourceKind.Kimi, Now - Minute, Five) with
        {
            RouteStatuses = ImmutableDictionary<QuotaRoute, QuotaRouteStatus>.Empty.Add(QuotaRoute.Kimi, QuotaRouteStatus.Ok(Now - Minute)),
        };
        Assert.Equal(["5h 已用 68%，3 小時 12 分後重置"], Block(kimi, ok).TooltipLines);
        Assert.Equal("Kimi Code CLI 額度：5h 已用 68%，3 小時 12 分後重置", Block(kimi, ok).Spoken);

        var qoder = AppSettings.Default with { BarQuota = SourceKind.QoderCli, Quota = QuotaSettings.Default with { Qoder = true } };
        var credits = QuotaWindow.Of(QuotaWindowKind.Credits, 25, null);
        var state = State(SourceKind.QoderCli, Now - Minute, credits) with
        {
            RouteStatuses = ImmutableDictionary<QuotaRoute, QuotaRouteStatus>.Empty.Add(QuotaRoute.Qoder, QuotaRouteStatus.Of(QuotaStatusKind.NoBrowser)),
        };
        var block = Block(qoder, state);
        var line = Assert.Single(block.Lines);
        Assert.Equal(("點數", "25%", true), (line.Label, line.Text, line.Pale));
        Assert.StartsWith("Qoder CLI 額度：點數 已用 25%、", block.Spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void HighContrastColorsNumbersInTheTextColorWithoutRed()
    {
        Assert.Equal(BarQuotaInk.Number, BarQuotaBlock.InkOf(number: true, pale: false, critical: false, highContrast: false));
        Assert.Equal(BarQuotaInk.Critical, BarQuotaBlock.InkOf(number: true, pale: false, critical: true, highContrast: false));
        Assert.Equal(BarQuotaInk.Secondary, BarQuotaBlock.InkOf(number: true, pale: true, critical: false, highContrast: false));
        Assert.Equal(BarQuotaInk.Secondary, BarQuotaBlock.InkOf(number: false, pale: false, critical: false, highContrast: false));

        Assert.Equal(BarQuotaInk.Text, BarQuotaBlock.InkOf(number: true, pale: false, critical: true, highContrast: true));
        Assert.Equal(BarQuotaInk.Text, BarQuotaBlock.InkOf(number: true, pale: false, critical: false, highContrast: true));
        Assert.Equal(BarQuotaInk.GrayText, BarQuotaBlock.InkOf(number: true, pale: true, critical: false, highContrast: true));
        Assert.Equal(BarQuotaInk.GrayText, BarQuotaBlock.InkOf(number: false, pale: false, critical: false, highContrast: true));
    }

    // MARK: Unavailable

    [Fact]
    public void ASourceWhoseQuotaIsOffReadsQuotaDashAndSaysWhere()
    {
        var disabled = Codex with { CodexEnabled = false };
        var state = State(SourceKind.Codex, Now - Minute, Five, Week);
        foreach (var settings in new[] { disabled, Codex with { Quota = QuotaSettings.Default with { Codex = false } } })
        {
            var block = Block(settings, state);
            Assert.Equal(BarQuotaForm.Off, block.Form);
            var line = Assert.Single(block.Lines);
            Assert.Equal(("額度", "–", true, false), (line.Label, line.Text, line.Pale, line.Critical));
            Assert.Equal("Codex：額度已關閉（資料來源）", block.Spoken);
            Assert.Equal("Codex：額度已關閉（資料來源）", block.TooltipHeader);
            Assert.Empty(block.TooltipLines);
        }

        var en = Block(disabled, state, En);
        Assert.Equal(("Quota", "–"), (en.Lines[0].Label, en.Lines[0].Text));
        Assert.Equal("Codex: quota is off (Data Sources)", en.Spoken);
    }

    [Fact]
    public void NoWindowReadsQuotaDashNeverZero()
    {
        var gemini = AppSettings.Default with { BarQuota = SourceKind.Gemini };
        foreach (var state in new[] { QuotaState.Empty, State(SourceKind.Gemini, Now - Minute) })
        {
            var block = Block(gemini, state);
            Assert.Equal(BarQuotaForm.None, block.Form);
            Assert.Equal([("額度", "–")], block.Lines.Select(l => (l.Label, l.Text)));
            Assert.True(block.Alone);
            Assert.Equal("Gemini CLI：尚無額度資料", block.Spoken);
            Assert.Equal(block.Spoken, block.TooltipHeader);
            Assert.DoesNotContain(block.Lines, l => l.Text.Contains('0', StringComparison.Ordinal));
        }

        var signedOut = QuotaState.Empty with
        {
            RouteStatuses = ImmutableDictionary<QuotaRoute, QuotaRouteStatus>.Empty.Add(QuotaRoute.Gemini, QuotaRouteStatus.Of(QuotaStatusKind.NoCredential)),
        };
        var none = Block(gemini, signedOut);
        Assert.Equal("Gemini CLI：尚無額度資料、找不到登入資料", none.Spoken);
        Assert.Equal(["找不到登入資料"], none.TooltipLines);
        Assert.Equal("Gemini CLI: no quota data yet, No sign-in found", Block(gemini, signedOut, En).Spoken);
    }

    // MARK: Words

    [Fact]
    public void TheWordsReadEveryWindowInZhHantAndEnglish()
    {
        var state = State(SourceKind.Codex, Now - Minute, Five, Week);
        var zh = Block(Codex, state);
        var week = Reset(Week, ZhHant);
        Assert.Equal("Codex 額度：5h 已用 68%，3 小時 12 分後重置、7d 已用 42%，" + week, zh.Spoken);
        Assert.Equal("Codex 額度 · " + QuotaText.Updated(Now - Minute, Now, ZhHant, TimeZoneInfo.Utc), zh.TooltipHeader);
        Assert.Equal(["5h 已用 68%，3 小時 12 分後重置", "7d 已用 42%，" + week], zh.TooltipLines);

        var en = Block(Codex, state, En);
        Assert.Equal("Codex quota: 5h 68% used, resets in 3 h 12 min, 7d 42% used, " + Reset(Week, En), en.Spoken);
        Assert.Equal("Codex quota · " + QuotaText.Updated(Now - Minute, Now, En, TimeZoneInfo.Utc), en.TooltipHeader);
        Assert.Equal(LanguageCodes.En, en.LanguageCode);
    }

    [Fact]
    public void TheQuotaWordsFollowTheNetworkAndIntelligenceWords()
    {
        var traffic = new BarTrafficBlock(new TrafficFormat.Parts("1.28", "MB/s"), new TrafficFormat.Parts("64.0", "KB/s"), false,
            "下載每秒 1.28 MB，上傳每秒 64.0 KB");
        var intelligence = BarIntelligenceBlock.Of(TestSums.Of(300, 86, 30, 300), ZhHant);
        var quota = Block(Codex, State(SourceKind.Codex, Now - Minute, Five));
        Assert.Equal("532 tok/s，下載每秒 1.28 MB，上傳每秒 64.0 KB，性能 86，思考每秒 10 個 token，Codex 額度：5h 已用 68%，3 小時 12 分後重置",
            BarReadouts.Append("532 tok/s", "，", traffic, intelligence, quota));
        Assert.Equal("v，Codex 額度：5h 已用 68%，3 小時 12 分後重置", BarReadouts.Append("v", "，", null, null, quota));
        Assert.Equal("v · 性能：尚無分數", BarReadouts.Append("v", " · ", null, BarIntelligenceBlock.Of(IntelligenceSums.Zero, ZhHant), null));
    }

    // MARK: Cells

    [Fact]
    public void TheNumberCellIsAtLeastAsWideAsAHundredPercentAndTheLabelCellAsTheWiderLabel()
    {
        static double Width(string text) => text.Length * 6.0;
        var block = Block(Codex, State(SourceKind.Codex, Now - Minute, Five, QuotaWindow.Of(QuotaWindowKind.ModelWeek, 3, null, name: "Fable")));
        Assert.Equal(new BarQuotaCells(Width("Fable 7d"), Width("100%")), block.Cells(Width, Width));

        var estimate = new QuotaWindowEstimate(Now - Minute, 99.8, true, Five.ResetsAtMs, false, 0, 0, null, null, null, EstimateConfidence.None,
            null, null, 0, null, null);
        var wide = Block(Codex, State(SourceKind.Codex, Now - Minute, Five with { UsedPercent = 99 }) with
        {
            Estimates = ImmutableDictionary<QuotaEstimateKey, QuotaWindowEstimate>.Empty
                .Add(new QuotaEstimateKey(null, SourceKind.Codex, EstimateNames.WindowId(Five)), estimate),
        });
        Assert.Equal("≈100%", wide.Lines[0].Text);
        Assert.Equal(Width("≈100%"), wide.Cells(Width, Width).Number);
        Assert.Equal(Width("Fable 7d") + BarQuotaCells.LabelGap + Width("100%"), block.Cells(Width, Width).Width);
        Assert.Equal(new BarQuotaCells(5, 9), new BarQuotaCells(5, 2).Max(new BarQuotaCells(1, 9)));
    }
}
