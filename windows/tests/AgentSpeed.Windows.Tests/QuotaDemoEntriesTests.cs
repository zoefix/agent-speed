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
        const string origin = "https://radar.example";
        var entries = QuotaDemo.Entries(QuotaDemo.State(Now, two: false), Now, origin);
        Assert.Equal([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.ClaudeCode, SourceKind.ClaudeDesktop], entries.Select(e => e.Source));
        Assert.All(entries, e =>
        {
            Assert.True(AccountKeys.IsKey(e.Account), e.Account);
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

        // One tag per account and service: Claude Code's two windows share one, another service sees others.
        Assert.Equal(3, entries.Select(e => e.Account).Distinct().Count());
        Assert.Equal(entries[1].Account, entries[2].Account);
        var elsewhere = QuotaDemo.Entries(QuotaDemo.State(Now, two: false), Now, "https://other.example");
        Assert.Empty(elsewhere.Select(e => e.Account).Intersect(entries.Select(e => e.Account)));
    }
}
