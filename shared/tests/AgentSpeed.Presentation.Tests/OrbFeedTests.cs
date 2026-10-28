using AgentSpeed.Core;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Orb;

namespace AgentSpeed.Presentation.Tests;

public class OrbFeedTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);

    private static SourceStatus Source(SourceKind kind, double live) =>
        new(kind, true, "root", true, 1, new Rate(live, live * 10), new TokenTotals(Requests: 3, Output: 1000), 1);

    private static SpeedSnapshot Snapshot(double codex, double claude, long seconds, double? codexPeak = null) =>
        SpeedSnapshot.Empty with
        {
            Timestamp = Day.AddSeconds(seconds),
            State = MonitorState.Running,
            Live = new Rate(codex + claude, (codex + claude) * 10),
            PeakToday = new Rate(Math.Max(codex + claude, 0), 0),
            Sources = [Source(SourceKind.Codex, codex), Source(SourceKind.ClaudeCode, claude)],
            SubsetPeaks = codexPeak is { } peak ? new Dictionary<SourceSet, double> { [SourceSet.Of(SourceKind.Codex)] = peak } : new Dictionary<SourceSet, double>(),
            DayStart = Day,
        };

    [Fact]
    public void AllSourcesOrbShowsTheAllSourcesValue()
    {
        var feed = new OrbFeed("a");
        feed.Configure(null, SpeedMetric.Output, 400, 0);
        feed.Update(Snapshot(100, 50, 10), 10);
        Assert.Equal(150, feed.Value);
        Assert.False(feed.IsUnavailable);
    }

    [Fact]
    public void SubsetOrbSumsOnlyItsSourcesInTheSelectedMetric()
    {
        var feed = new OrbFeed("a");
        feed.Configure(SourceSet.Of(SourceKind.Codex), SpeedMetric.Total, 200_000, 0);
        feed.Update(Snapshot(100, 50, 10), 10);
        Assert.Equal(1000, feed.Value);
    }

    [Fact]
    public void TiersAndTitlesUseTheOrbsOwnRedline()
    {
        var low = new OrbFeed("low");
        low.Configure(null, SpeedMetric.Output, 400, 0);
        var high = new OrbFeed("high");
        high.Configure(null, SpeedMetric.Output, 2000, 0);
        foreach (var feed in new[] { low, high })
        {
            feed.Update(Snapshot(320, 0, 10), 10);
            feed.Tick(11);
        }

        Assert.Equal(SpeedTier.Redline, low.Tier);
        Assert.True(high.Tier < SpeedTier.Redline);
        Assert.NotEqual(low.Title, high.Title);
    }

    [Fact]
    public void SubsetNewPeakFiresAndIsSilencedAfterASetChange()
    {
        var feed = new OrbFeed("a");
        feed.Configure(SourceSet.Of(SourceKind.Codex), SpeedMetric.Output, 400, 0);
        Assert.False(feed.Update(Snapshot(10, 0, 1, codexPeak: 10), 1));
        Assert.True(feed.Update(Snapshot(80, 0, 2, codexPeak: 10), 2));

        feed.Configure(SourceSet.Of(SourceKind.ClaudeCode).With(SourceKind.Codex), SpeedMetric.Output, 400, Day.AddSeconds(3).ToUnixTimeMilliseconds());
        Assert.False(feed.Update(Snapshot(10, 0, 4, codexPeak: 10), 4));
        Assert.False(feed.Update(Snapshot(300, 300, 30), 30));
    }

    [Fact]
    public void AllSourcesOrbLeavesNewPeakToTheEngineEvent()
    {
        var feed = new OrbFeed("a");
        feed.Configure(null, SpeedMetric.Output, 400, 0);
        Assert.False(feed.Update(Snapshot(500, 0, 1), 1));
        Assert.True(feed.Accepts(new SpeedEvent.NewPeak(500)));
        var subset = new OrbFeed("b");
        subset.Configure(SourceSet.Of(SourceKind.Codex), SpeedMetric.Output, 400, 0);
        Assert.False(subset.Accepts(new SpeedEvent.NewPeak(500)));
    }

    [Fact]
    public void CompletionsGoToOrbsThatTrackTheirSource()
    {
        var response = new ResponseSummary(SourceKind.ClaudeCode, "p", null, 100, 100, TimeSpan.FromSeconds(1), 100, Day);
        var codex = new OrbFeed("a");
        codex.Configure(SourceSet.Of(SourceKind.Codex), SpeedMetric.Output, 400, 0);
        var claude = new OrbFeed("b");
        claude.Configure(SourceSet.Of(SourceKind.ClaudeCode), SpeedMetric.Output, 400, 0);
        Assert.False(codex.Accepts(new SpeedEvent.ResponseCompleted(response)));
        Assert.True(claude.Accepts(new SpeedEvent.ResponseCompleted(response)));
    }
}
