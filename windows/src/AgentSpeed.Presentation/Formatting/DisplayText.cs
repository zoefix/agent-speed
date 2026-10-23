using AgentSpeed.Core;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Formatting;

/// <summary>User-visible text built from live data. Never includes conversation content.</summary>
public static class DisplayText
{
    public static string TierKey(SpeedTier tier) => tier switch
    {
        SpeedTier.Cruise => "tier.cruise",
        SpeedTier.Boost => "tier.boost",
        SpeedTier.Overdrive => "tier.overdrive",
        SpeedTier.Redline => "tier.redline",
        _ => "tier.idle",
    };

    public static string TierName(SpeedTier tier, Localizer localizer) => localizer[TierKey(tier)];

    /// <summary>Tier label as drawn on the orb: uppercase for Latin scripts.</summary>
    public static string TierLabel(SpeedTier tier, Localizer localizer)
    {
        var name = TierName(tier, localizer);
        return localizer.UsesLatinScript ? name.ToUpper(localizer.Culture) : name;
    }

    public static string TitleKey(SpeedTitle title) => SpeedTitles.Key(title);

    public static string TitleName(SpeedTitle title, Localizer localizer) => localizer[TitleKey(title)];

    /// <summary>Speed title as drawn under the value: uppercase for Latin scripts.</summary>
    public static string TitleLabel(SpeedTitle title, Localizer localizer)
    {
        var name = TitleName(title, localizer);
        return localizer.UsesLatinScript ? name.ToUpper(localizer.Culture) : name;
    }

    /// <summary>
    /// Where a title starts at <paramref name="redline"/>, compact and without the unit: <c>8</c>, <c>4.5</c>, <c>48</c>,
    /// <c>24k</c>. Thresholds are exact, so a whole number below 10 drops the speed formatter's trailing <c>.0</c>.
    /// </summary>
    public static string TitleThreshold(SpeedTitle title, double redline)
    {
        var threshold = SpeedTitles.Threshold(title, redline);
        return threshold is >= 1 and < 10 && Math.Abs(threshold - Math.Round(threshold)) < 1e-9
            ? Math.Round(threshold).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : SpeedFormat.Speed(threshold);
    }

    /// <summary>Live values below this would display as <c>0</c> and are shown as a dash or a phase glyph instead.</summary>
    public const double ZeroSpeedThreshold = 0.05;

    /// <summary>The live value is too small to show as a number (DESIGN section 4, items 3–4).</summary>
    public static bool IsNegligibleSpeed(double value) => !double.IsFinite(value) || value < ZeroSpeedThreshold;

    public static string SourceKey(SourceKind source) => source switch
    {
        SourceKind.Codex => "source.codex",
        SourceKind.ClaudeCode => "source.claudeCode",
        SourceKind.ClaudeDesktop => "source.claudeDesktop",
        SourceKind.DeepSeek => "source.deepseek",
        SourceKind.OpenCode => "source.opencode",
        SourceKind.Gemini => "source.gemini",
        SourceKind.Antigravity => "source.antigravity",
        SourceKind.AntigravityCli => "source.antigravityCli",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    public static string PhaseKey(AgentPhase phase) => phase switch
    {
        AgentPhase.Generating => "phase.generating",
        AgentPhase.Tools => "phase.tools",
        _ => "phase.idle",
    };

    /// <summary><c>128 tok/s · 噴射客機</c></summary>
    public static string SpeedLine(double value, SpeedTitle title, Localizer localizer) =>
        $"{SpeedFormat.Speed(value)} {localizer["unit.tokS"]} · {TitleName(title, localizer)}";

    /// <summary><c>每秒 128 個 token，噴射客機</c></summary>
    public static string Accessibility(double value, SpeedTitle title, Localizer localizer) =>
        localizer.Format("a11y.orb", SpeedFormat.Speed(value), TitleName(title, localizer));

    /// <summary><c>今日 1.2M · 3 個 agent</c></summary>
    public static string TodaySummary(SpeedSnapshot snapshot, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return localizer.Format("today.summary", SpeedFormat.Tokens(snapshot.Today.Output), snapshot.Agents.Count);
    }

    /// <summary><c>生成中 3 · 工具 1</c></summary>
    public static string AgentContext(SpeedSnapshot snapshot, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return localizer.Format("agents.context", snapshot.GeneratingCount, snapshot.ToolsCount);
    }

    /// <summary><c>+4 個 agent</c>: the disclosure row for agents the collapsed list leaves out.</summary>
    public static string ExpandAgents(int hidden, Localizer localizer) => localizer.Format("agents.expand", hidden);

    /// <summary><c>顯示全部 10 個 agent</c>: the disclosure row's accessibility label.</summary>
    public static string ExpandAgentsAccessibility(int total, Localizer localizer) => localizer.Format("agents.expand.a11y", total);

    public static string TimeAgo(TimeSpan elapsed, Localizer localizer)
    {
        if (elapsed < TimeSpan.FromSeconds(5))
        {
            return localizer["time.justNow"];
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return localizer.Format("time.secondsAgo", (int)elapsed.TotalSeconds);
        }

        return localizer.Format("time.minutesAgo", (int)elapsed.TotalMinutes);
    }

    public static string Warning(MonitorWarning warning, Localizer localizer) => warning switch
    {
        MonitorWarning.PartialHistory => localizer["warning.partialHistory"],
        MonitorWarning.UnreadableFiles u => localizer.Format("warning.unreadable", u.Count),
        MonitorWarning.MalformedLines m => localizer.Format("warning.malformed", m.Count),
        MonitorWarning.UnfinalizedUsage u => localizer.Format("warning.unfinalized", u.Count),
        MonitorWarning.UnsupportedFormat f => localizer.Format("warning.unsupportedFormat", localizer[SourceKey(f.Source)]),
        _ => string.Empty,
    };
}
