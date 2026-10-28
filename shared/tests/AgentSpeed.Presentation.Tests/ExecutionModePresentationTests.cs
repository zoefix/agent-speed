using AgentSpeed.Core;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Tests;

public sealed class ExecutionModePresentationTests
{
    private static readonly Localizer Loc = new(Localizer.LoadEmbeddedTable(), "en");
    [Fact]
    public void FormatterKeepsSelectedAndServedEvidenceAndWorkflowDimensionsDistinct()
    {
        var selected = ExecutionModeNative.OpenAiTier("priority", ExecutionModeEvidence.Selected, ExecutionModeScope.Request);
        var served = ExecutionModeNative.OpenAiTier("default");
        var modes = selected.Merge(served).Merge(ExecutionModeNative.Workflow("fast"));
        var detail = ExecutionModeText.Detail(modes, Loc);
        Assert.Contains("Selected by task", detail, StringComparison.Ordinal);
        Assert.Contains("Served response", detail, StringComparison.Ordinal);
        Assert.Contains("priority", detail, StringComparison.Ordinal);
        var summary = ExecutionModeText.Summary(modes, Loc);
        Assert.Contains("Service tier: Standard", summary, StringComparison.Ordinal);
        Assert.Contains("Workflow: Fast", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Service tier: Fast", summary, StringComparison.Ordinal);
    }
    [Fact]
    public void ConfiguredDefaultsExplainTheirLimitAndMultipleAgentsNeverPickAnArbitraryMode()
    {
        var configured = ExecutionModeNative.OpenAiTier("ultrafast", ExecutionModeEvidence.Configured, ExecutionModeScope.Configuration);
        Assert.Contains(Loc["mode.defaultNote"], ExecutionModeText.Detail(configured, Loc), StringComparison.Ordinal);
        Assert.Equal(Loc["mode.unknown"], ExecutionModeText.Summary(ExecutionModes.Empty, Loc));
        Assert.Equal(Loc["mode.mixed"], ExecutionModeText.Aggregate([ExecutionModeNative.ClaudeSpeed("fast"), ExecutionModeNative.ClaudeSpeed("standard")], Loc));
    }
    [Fact]
    public void ModeCsvIsIndependentQuotesUnsafeModelsAndUsesTheExistingWeightedAverage()
    {
        var modes = new[]
        {
            new ExecutionModeUsage("=cmd,model", SourceKind.ClaudeCode, ExecutionModeKind.ServiceTier, "fast", "served", "anthropic", 1, 10, 10, 1000),
            new ExecutionModeUsage("=cmd,model", SourceKind.ClaudeCode, ExecutionModeKind.ServiceTier, "fast", "served", "anthropic", 1, 30, 30, 1000),
            new ExecutionModeUsage("=cmd,model", SourceKind.ClaudeCode, ExecutionModeKind.ServiceTier, "unknown", "unknown", null, 1, 50, 0, 0),
        };
        var day = new DaySummary(new(2026, 10, 24), 3, 0, 0, 0, 90, 0, 40, 2000, 0, 1, null, [], []) { Modes = modes };
        var original = HistoryCsv.Build([day with { Modes = [] }], null);
        Assert.Equal(original, HistoryCsv.Build([day], null));
        var csv = ExecutionModeCsv.Build([day]);
        Assert.StartsWith("date,source,model,kind,mode,evidence,provider,requests,output_tokens,measured_output_tokens,measured_ms,avg_speed_tok_s\r\n", csv, StringComparison.Ordinal);
        Assert.Contains("2026-10-24,claudeCode,\"'=cmd,model\",serviceTier,fast,served,anthropic,2,40,40,2000,20.0\r\n", csv, StringComparison.Ordinal);
        Assert.Contains(",unknown,unknown,,1,50,0,0,\r\n", csv, StringComparison.Ordinal);
    }
    [Fact]
    public void DailyAggregationKeepsSourceAndEvidenceInSeparateGroups()
    {
        var first = new DailyUsage("2026-10-24", SourceKind.ClaudeCode, new TokenTotals(1, 0, 0, 0, 10), 10, 1000, 0, 1, null, [])
        { Modes = [new("m", SourceKind.ClaudeCode, ExecutionModeKind.ServiceTier, "fast", "served", "anthropic", 1, 10, 10, 1000)] };
        var second = first with { Source = SourceKind.Omp, Modes = [new("m", SourceKind.Omp, ExecutionModeKind.ServiceTier, "fast", "recorded", "anthropic", 1, 10, 10, 1000)] };
        var day = Assert.Single(HistoryAggregator.Daily([first, second], null));
        Assert.Equal(2, day.Modes.Count);
        Assert.Equal(day.Requests, day.Modes.Sum(m => m.Requests));
        Assert.Equal(day.AverageSpeed, day.Modes.Sum(m => m.MeasuredOutput) / (day.Modes.Sum(m => m.MeasuredMs) / 1000.0));
        Assert.Equal(2, ExecutionModeText.VisibleUsage(day.Modes).Count());
    }
    [Fact]
    public void EveryLanguageHasModeAndProfileLabels()
    {
        foreach (var language in new[] { "en", "zh-Hant", "zh-Hans", "ja" })
        {
            var loc = new Localizer(Localizer.LoadEmbeddedTable(), language);
            foreach (var key in new[] { "mode.label", "mode.exportCsv", "mode.evidence.recorded", "mode.defaultNote", "profile.title", "profile.identity.unlinkNote", "profile.pairing.approve" })
                Assert.NotEqual(key, loc[key]);
        }
    }
}
