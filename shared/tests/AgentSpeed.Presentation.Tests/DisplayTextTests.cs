using AgentSpeed.Core;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Tests;

public class DisplayTextTests
{
    private static readonly StringTable Embedded = Localizer.LoadEmbeddedTable();

    private static Localizer For(string language) => new(Embedded, language);

    private static AgentStatus Agent(int i) => new(
        $"/tmp/agent-{i}.jsonl", SourceKind.ClaudeCode, "proj", null, AgentPhase.Generating, false, Rate.Zero, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void SpeedLineUsesTheSpeedTitleInTraditionalChinese()
    {
        Assert.Equal("128 tok/s · 噴射客機", DisplayText.SpeedLine(128, SpeedTitle.Jetliner, For(LanguageCodes.ZhHant)));
        Assert.Equal("1.28k tok/s · 火箭", DisplayText.SpeedLine(1280, SpeedTitle.Rocket, For(LanguageCodes.ZhHant)));
        Assert.Equal("0 tok/s · 待機", DisplayText.SpeedLine(0, SpeedTitle.Idle, For(LanguageCodes.ZhHant)));
    }

    [Fact]
    public void SpeedLineUsesTheSpeedTitleInOtherLanguages()
    {
        Assert.Equal("4.2 tok/s · WALKING", DisplayText.SpeedLine(4.2, SpeedTitle.Walk, For(LanguageCodes.En)));
        Assert.Equal("200 tok/s · 战斗机", DisplayText.SpeedLine(200, SpeedTitle.FighterJet, For(LanguageCodes.ZhHans)));
        Assert.Equal("200 tok/s · 戦闘機", DisplayText.SpeedLine(200, SpeedTitle.FighterJet, For(LanguageCodes.Ja)));
    }

    [Fact]
    public void AccessibilityLabelReadsTheSpeedTitle()
    {
        Assert.Equal("每秒 128 個 token，噴射客機", DisplayText.Accessibility(128, SpeedTitle.Jetliner, For(LanguageCodes.ZhHant)));
        Assert.Equal("每秒 128 个 token，喷气客机", DisplayText.Accessibility(128, SpeedTitle.Jetliner, For(LanguageCodes.ZhHans)));
        Assert.Equal("128 tokens per second, JETLINER", DisplayText.Accessibility(128, SpeedTitle.Jetliner, For(LanguageCodes.En)));
        Assert.Equal("毎秒 4.2 トークン、徒歩", DisplayText.Accessibility(4.2, SpeedTitle.Walk, For(LanguageCodes.Ja)));
        Assert.Equal("每秒 0 個 token，待機", DisplayText.Accessibility(0, SpeedTitle.Idle, For(LanguageCodes.ZhHant)));
    }

    [Theory]
    [InlineData(SpeedTitle.Idle, "IDLE", "待機")]
    [InlineData(SpeedTitle.Walk, "WALKING", "步行")]
    [InlineData(SpeedTitle.EBike, "E-BIKE", "電動自行車")]
    [InlineData(SpeedTitle.SportsCar, "SPORTS CAR", "跑車")]
    [InlineData(SpeedTitle.BulletTrain, "BULLET TRAIN", "高鐵")]
    [InlineData(SpeedTitle.Hypersonic, "HYPERSONIC", "極音速飛機")]
    [InlineData(SpeedTitle.Rocket, "ROCKET", "火箭")]
    public void TitleLabelIsUppercaseInEnglishAndUnchangedInChinese(SpeedTitle title, string english, string traditional)
    {
        Assert.Equal(english, DisplayText.TitleLabel(title, For(LanguageCodes.En)));
        Assert.Equal(traditional, DisplayText.TitleLabel(title, For(LanguageCodes.ZhHant)));
        Assert.Equal(traditional, DisplayText.TitleName(title, For(LanguageCodes.ZhHant)));
    }

    [Fact]
    public void EveryTitleKeyHasAllFourLanguages()
    {
        foreach (var title in Enum.GetValues<SpeedTitle>())
        {
            var key = DisplayText.TitleKey(title);
            Assert.True(Embedded.Contains(key), key);
            foreach (var language in LanguageCodes.All)
            {
                Assert.True(Embedded.TryGet(key, language, out var text) && text.Length > 0, $"{key} [{language}]");
            }
        }
    }

    [Fact]
    public void TitleThresholdsFollowTheRedlineCompactAndWithoutUnit()
    {
        Assert.Equal("48", DisplayText.TitleThreshold(SpeedTitle.BulletTrain, 400));
        Assert.Equal("0.5", DisplayText.TitleThreshold(SpeedTitle.Walk, 400));
        Assert.Equal("8", DisplayText.TitleThreshold(SpeedTitle.Bicycle, 400));
        Assert.Equal("2", DisplayText.TitleThreshold(SpeedTitle.Bicycle, 100));
        Assert.Equal("4.5", DisplayText.TitleThreshold(SpeedTitle.EBike, 150));
        Assert.Equal("1.00k", DisplayText.TitleThreshold(SpeedTitle.Rocket, 1000));
        Assert.Equal("4.00k", DisplayText.TitleThreshold(SpeedTitle.Bicycle, 200_000));
        Assert.Equal("200k", DisplayText.TitleThreshold(SpeedTitle.Rocket, 200_000));
        Assert.Equal("1.40M", DisplayText.TitleThreshold(SpeedTitle.Hypersonic, 2_000_000));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0.049, true)]
    [InlineData(0.05, false)]
    [InlineData(0.2, false)]
    [InlineData(double.NaN, true)]
    [InlineData(-1, true)]
    public void NegligibleSpeedIsNeverShownAsZero(double value, bool negligible)
    {
        Assert.Equal(negligible, DisplayText.IsNegligibleSpeed(value));
    }

    [Theory]
    [InlineData(SpeedTier.Idle, "IDLE", "待機")]
    [InlineData(SpeedTier.Cruise, "CRUISE", "巡航")]
    [InlineData(SpeedTier.Boost, "BOOST", "加速")]
    [InlineData(SpeedTier.Overdrive, "OVERDRIVE", "狂飆")]
    [InlineData(SpeedTier.Redline, "REDLINE", "極限")]
    public void TierLabelIsUppercaseInEnglishAndUnchangedInChinese(SpeedTier tier, string english, string traditional)
    {
        Assert.Equal(english, DisplayText.TierLabel(tier, For(LanguageCodes.En)));
        Assert.Equal(traditional, DisplayText.TierLabel(tier, For(LanguageCodes.ZhHant)));
        Assert.Equal(traditional, DisplayText.TierName(tier, For(LanguageCodes.ZhHant)));
    }

    [Fact]
    public void TierLabelUppercasesMixedCaseLatinText()
    {
        var table = StringTable.Parse("""{ "tier.boost": { "en": "Boost", "zh-Hant": "加速", "ja": "Boost" } }""");
        Assert.Equal("BOOST", DisplayText.TierLabel(SpeedTier.Boost, new Localizer(table, LanguageCodes.En)));
        Assert.Equal("Boost", DisplayText.TierName(SpeedTier.Boost, new Localizer(table, LanguageCodes.En)));
        Assert.Equal("Boost", DisplayText.TierLabel(SpeedTier.Boost, new Localizer(table, LanguageCodes.Ja)));
        Assert.Equal("加速", DisplayText.TierLabel(SpeedTier.Boost, new Localizer(table, LanguageCodes.ZhHant)));
    }

    [Theory]
    [InlineData(SpeedTier.Idle, "tier.idle")]
    [InlineData(SpeedTier.Cruise, "tier.cruise")]
    [InlineData(SpeedTier.Boost, "tier.boost")]
    [InlineData(SpeedTier.Overdrive, "tier.overdrive")]
    [InlineData(SpeedTier.Redline, "tier.redline")]
    [InlineData((SpeedTier)99, "tier.idle")]
    public void TierKeys(SpeedTier tier, string key)
    {
        Assert.Equal(key, DisplayText.TierKey(tier));
        Assert.True(Embedded.Contains(key));
    }

    [Fact]
    public void SourceAndPhaseKeysExist()
    {
        foreach (var source in Enum.GetValues<SourceKind>())
        {
            Assert.True(Embedded.Contains(DisplayText.SourceKey(source)), DisplayText.SourceKey(source));
        }

        foreach (var phase in Enum.GetValues<AgentPhase>())
        {
            Assert.True(Embedded.Contains(DisplayText.PhaseKey(phase)), DisplayText.PhaseKey(phase));
        }

        Assert.Equal("source.claudeCode", DisplayText.SourceKey(SourceKind.ClaudeCode));
        Assert.Equal("phase.tools", DisplayText.PhaseKey(AgentPhase.Tools));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayText.SourceKey((SourceKind)99));
    }

    [Theory]
    [InlineData(SourceKind.Codex, "source.codex", "Codex")]
    [InlineData(SourceKind.ClaudeCode, "source.claudeCode", "Claude Code")]
    [InlineData(SourceKind.ClaudeDesktop, "source.claudeDesktop", "Claude Desktop")]
    [InlineData(SourceKind.DeepSeek, "source.deepseek", "DSH")]
    [InlineData(SourceKind.OpenCode, "source.opencode", "OpenCode")]
    [InlineData(SourceKind.Gemini, "source.gemini", "Gemini CLI")]
    [InlineData(SourceKind.Omp, "source.omp", "omp")]
    [InlineData(SourceKind.Antigravity, "source.antigravity", "Antigravity")]
    [InlineData(SourceKind.WorkBuddy, "source.workbuddy", "WorkBuddy")]
    [InlineData(SourceKind.WorkBuddyAi, "source.workbuddyAi", "WorkBuddy AI")]
    [InlineData(SourceKind.ZCode, "source.zcode", "ZCode")]
    [InlineData(SourceKind.Kimi, "source.kimi", "Kimi Code CLI")]
    [InlineData(SourceKind.GrokBuild, "source.grokBuild", "Grok Build")]
    public void EverySourceHasItsOwnName(SourceKind source, string key, string english)
    {
        Assert.Equal(key, DisplayText.SourceKey(source));
        Assert.Equal(english, For(LanguageCodes.En)[key]);
    }

    [Fact]
    public void DeepSeekStringsExistInEveryLanguage()
    {
        foreach (var language in LanguageCodes.All)
        {
            Assert.True(Embedded.TryGet("source.deepseek", language, out _), language);
            Assert.True(Embedded.TryGet("settings.sources.dshHint", language, out var hint), language);
            Assert.Contains("dsh", hint, StringComparison.Ordinal);
            Assert.True(Embedded.TryGet("state.noSources", language, out var noSources), language);
            Assert.False(string.IsNullOrWhiteSpace(noSources), language);
        }
    }

    [Fact]
    public void OmpAndAntigravityStringsExistInEveryLanguage()
    {
        foreach (var language in LanguageCodes.All)
        {
            Assert.True(Embedded.TryGet("source.omp", language, out var omp), language);
            Assert.Equal("omp", omp);
            Assert.True(Embedded.TryGet("source.antigravity", language, out var antigravity), language);
            Assert.Equal("Antigravity", antigravity);
            Assert.True(Embedded.TryGet("settings.sources.ompHint", language, out var ompHint), language);
            Assert.Contains("~/.omp/agent/sessions", ompHint, StringComparison.Ordinal);
            Assert.True(Embedded.TryGet("settings.sources.antigravityHint", language, out var antigravityHint), language);
            Assert.Contains("~/.gemini/antigravity-cli", antigravityHint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WorkBuddyStringsAreTheAppNamesInEveryLanguage()
    {
        foreach (var language in LanguageCodes.All)
        {
            Assert.True(Embedded.TryGet("source.workbuddy", language, out var workBuddy), language);
            Assert.Equal("WorkBuddy", workBuddy);
            Assert.True(Embedded.TryGet("source.workbuddyAi", language, out var workBuddyAi), language);
            Assert.Equal("WorkBuddy AI", workBuddyAi);
            Assert.True(Embedded.TryGet("settings.sources.workbuddyHint", language, out var hint), language);
            Assert.Contains("~/.workbuddy", hint, StringComparison.Ordinal);
            Assert.DoesNotContain("~/.workbuddy-ai", hint, StringComparison.Ordinal);
            Assert.True(Embedded.TryGet("settings.sources.workbuddyAiHint", language, out var aiHint), language);
            Assert.Contains("~/.workbuddy-ai", aiHint, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0, "剛剛", "just now")]
    [InlineData(4.9, "剛剛", "just now")]
    [InlineData(5, "5 秒前", "5s ago")]
    [InlineData(59.9, "59 秒前", "59s ago")]
    [InlineData(60, "1 分鐘前", "1m ago")]
    [InlineData(125, "2 分鐘前", "2m ago")]
    [InlineData(7200, "120 分鐘前", "120m ago")]
    public void TimeAgoThresholds(double seconds, string traditional, string english)
    {
        var elapsed = TimeSpan.FromSeconds(seconds);
        Assert.Equal(traditional, DisplayText.TimeAgo(elapsed, For(LanguageCodes.ZhHant)));
        Assert.Equal(english, DisplayText.TimeAgo(elapsed, For(LanguageCodes.En)));
    }

    [Fact]
    public void TodaySummaryAndAgentContext()
    {
        var snapshot = SpeedSnapshot.Empty with
        {
            Today = new TokenTotals(Output: 1_234_567),
            Agents = [Agent(1), Agent(2), Agent(3)],
            GeneratingCount = 3,
            ToolsCount = 1,
        };

        Assert.Equal("今日 1.23M · 3 個 agent", DisplayText.TodaySummary(snapshot, For(LanguageCodes.ZhHant)));
        Assert.Equal("Today 1.23M · 3 agents", DisplayText.TodaySummary(snapshot, For(LanguageCodes.En)));
        Assert.Equal("生成中 3 · 工具 1", DisplayText.AgentContext(snapshot, For(LanguageCodes.ZhHant)));
        Assert.Equal("3 generating · 1 in tools", DisplayText.AgentContext(snapshot, For(LanguageCodes.En)));
        Assert.Throws<ArgumentNullException>(() => DisplayText.TodaySummary(null!, For(LanguageCodes.En)));
        Assert.Throws<ArgumentNullException>(() => DisplayText.AgentContext(null!, For(LanguageCodes.En)));
    }

    [Fact]
    public void WarningsAreLocalized()
    {
        var zh = For(LanguageCodes.ZhHant);
        var partial = DisplayText.Warning(new MonitorWarning.PartialHistory(), zh);
        var unreadable = DisplayText.Warning(new MonitorWarning.UnreadableFiles(3), zh);
        var malformed = DisplayText.Warning(new MonitorWarning.MalformedLines(12), zh);

        Assert.Equal(zh["warning.partialHistory"], partial);
        Assert.NotEqual("warning.partialHistory", partial);
        Assert.Contains("3", unreadable, StringComparison.Ordinal);
        Assert.DoesNotContain("%d", unreadable, StringComparison.Ordinal);
        Assert.Contains("12", malformed, StringComparison.Ordinal);

        var unfinalized = DisplayText.Warning(new MonitorWarning.UnfinalizedUsage(4), zh);
        Assert.StartsWith("4 ", unfinalized, StringComparison.Ordinal);
        Assert.DoesNotContain("%d", unfinalized, StringComparison.Ordinal);
        Assert.NotEqual("warning.unfinalized", unfinalized);

        Assert.Equal("OpenCode 的紀錄格式太舊，請更新該工具", DisplayText.Warning(new MonitorWarning.UnsupportedFormat(SourceKind.OpenCode), zh));
        Assert.Equal("Gemini CLI uses a log format that is too old; please update it",
            DisplayText.Warning(new MonitorWarning.UnsupportedFormat(SourceKind.Gemini), For(LanguageCodes.En)));
    }
}
