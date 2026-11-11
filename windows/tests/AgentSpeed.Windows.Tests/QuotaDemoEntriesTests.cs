using AgentSpeed.Core;
using AgentSpeed.Core.Quota.Estimate;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Quota;

namespace AgentSpeed.Windows.Tests;

/// <summary>The capture's demo estimates as report entries (REPORTING-EXTENSIONS 11.2, 11.4): what the report preview shows.</summary>
public class QuotaDemoEntriesTests
{
    private const long Now = 1_800_000_000_000;

    [Fact]
    public void EveryDemoEstimateIsAnEntryThatKeepsTheReceiversRules()
    {
        var entries = QuotaDemo.Entries(QuotaDemo.State(Now, two: false), Now);
        Assert.Equal([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.ClaudeCode, SourceKind.ClaudeDesktop], entries.Select(e => e.Source));
        Assert.All(entries, e =>
        {
            Assert.Equal(e.Estimate.Confidence == EstimateConfidence.None, e.Estimate is { CapMicroUsd: null, LowMicroUsd: null, HighMicroUsd: null });
            Assert.True(e.ObservedUsedMicroUsd <= e.UsedMicroUsd);
            Assert.Contains(e.Source, e.Sources);
            Assert.True(e.Mix.Sum(m => m.Share) <= 1.003);
            if (e.Estimate.CapMicroUsd is { } cap)
            {
                Assert.InRange(cap, e.Estimate.LowMicroUsd!.Value, e.Estimate.HighMicroUsd!.Value);
            }
        });

        var json = ReportQuotas.Json(entries).ToCompactString();
        Assert.Contains("\"window\":{\"kind\":\"fiveHour\",\"name\":null,\"minutes\":300}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"account\":", json, StringComparison.Ordinal);
    }
}
