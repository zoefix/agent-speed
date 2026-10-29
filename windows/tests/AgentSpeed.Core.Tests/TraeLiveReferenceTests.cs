using System.Text.Json;
using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Trae;

namespace AgentSpeed.Core.Tests;

public sealed class TraeLiveReferenceTests
{
    private static readonly long Day = Ts.Ms("2026-10-20T00:00:00Z");

    [Fact]
    public void RequestReferencesSeedLiveSpeedWithoutAccountingAndExpireWhenRemoved()
    {
        var now = Day + 12 * 3_600_000;
        var engine = new SpeedEngine(Day);
        var old = new ParsedResponse("old", SourceKind.Trae, "a", "m", now - 3_700_000, now - 3_600_000, new TokenCounts(100, 0, 0, 100, 0));
        engine.Upsert(old, true, true);
        var agent = new AgentInfo("a", SourceKind.Trae, "p", "m", false, AgentPhase.Generating, now - 3000, now - 3000);
        engine.UpdateAgent(agent);
        Assert.Equal(0, engine.Compute(now, Day, false).Live.Output);
        var reference = new ParsedResponse("history-call", SourceKind.Trae, "a", "m", now - 12000, now - 4000, new TokenCounts(1200, 400, 0, 240, 40));
        engine.UpdateAgent(agent with { ReferenceResponses = [reference, reference] });
        var live = engine.Compute(now, Day, false);
        Assert.Equal(30, live.Live.Output);
        Assert.Equal(1, live.EstimatedShare);
        Assert.Equal((1, 100), (live.Today.Requests, live.Today.Output));
        Assert.Equal(100, live.LastResponse?.OutputTokens);
        Assert.Empty(live.Events);

        // Accounting later uses the same history key; the live reference must not add another request or weight.
        engine.Upsert(reference, true, true);
        var committed = engine.Compute(now, Day, false);
        Assert.Equal(30, committed.Live.Output);
        Assert.Equal((2, 340), (committed.Today.Requests, committed.Today.Output));
        engine.UpdateAgent(agent with { Phase = AgentPhase.Idle });
        Assert.Equal(0, engine.Compute(now, Day, false).Live.Output);
    }

    [Fact]
    public void InvalidOrStaleReferenceCannotSupplySpeed()
    {
        var now = Day + 12 * 3_600_000;
        var reference = new ParsedResponse("r", SourceKind.Trae, "a", "m", now - 12000, now - 4000, new TokenCounts(1200, 400, 0, 240, 40));
        foreach (var invalid in new[] { reference with { AgentId = "other" }, reference with { Source = SourceKind.Codex },
                     reference with { Final = false }, reference with { EndMs = now + 1 },
                     reference with { StartMs = now - 4_000_000, EndMs = now - 3_000_000 } })
        {
            var engine = new SpeedEngine(Day);
            engine.UpdateAgent(new AgentInfo("a", SourceKind.Trae, "p", "m", false, AgentPhase.Generating, now - 3000, now - 3000)
            { ReferenceResponses = [invalid] });
            Assert.Equal(0, engine.Compute(now, Day, false).Live.Output);
            Assert.Equal(0, engine.Compute(now, Day, false).Today.Requests);
        }
    }

    [Fact]
    public void ReferenceKeysAreWeightedOnceAndCommittedRecordsWin()
    {
        var now = Day + 12 * 3_600_000;
        var engine = new SpeedEngine(Day);
        var first = new ParsedResponse("first", SourceKind.Trae, "a", "m", now - 12000, now - 4000, new TokenCounts(1200, 0, 0, 240, 0));
        var second = new ParsedResponse("second", SourceKind.Trae, "a", "m", now - 14000, now - 13000, new TokenCounts(1200, 0, 0, 60, 0));
        engine.UpdateAgent(new AgentInfo("a", SourceKind.Trae, "p", "m", false, AgentPhase.Generating, now - 3000, now - 3000)
        { ReferenceResponses = [first, first, second] });
        Assert.Equal(300 / 9.0, engine.Compute(now, Day, false).Live.Output, 8);
        engine.Upsert(first with { Tokens = new TokenCounts(1200, 0, 0, 480, 0) }, true, true);
        var committed = engine.Compute(now, Day, false);
        Assert.Equal(540 / 9.0, committed.Live.Output, 8);
        Assert.Equal((1, 480), (committed.Today.Requests, committed.Today.Output));
    }

    [Fact]
    public void CompletingAnActiveTurnClearsReferencesAndCountsEachCallOnce()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("trae");
        // Native log discovery rejects symlink ancestors; resolve the macOS /var alias in the test folder.
        var canonical = Path.GetPathRoot(root)!;
        foreach (var part in root[canonical.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            canonical = Path.Combine(canonical, part);
            if (Directory.Exists(canonical) && new DirectoryInfo(canonical).ResolveLinkTarget(true) is { } resolved) canonical = resolved.FullName;
        }
        root = canonical;
        void Copy(string name)
        {
            var fixture = FixtureLocator.PathOf("trae/live-reference-" + name);
            foreach (var file in Directory.GetFiles(fixture, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(root, Path.GetRelativePath(fixture, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
        Copy("next-request");
        var now = Ts.Ms("2026-10-20T12:00:15Z");
        var source = new TraeSource(root, new Deduper()) { WallClockMs = () => now };
        var engine = new SpeedEngine(Day);
        void Apply(TraeUpdate update)
        {
            foreach (var id in update.Removed) engine.RemoveAgent(id);
            foreach (var agent in update.Agents)
                engine.UpdateAgent(new AgentInfo(agent.Id, SourceKind.Trae, agent.Project, agent.Model, false, agent.Phase, agent.InFlightStartMs, agent.LastMs)
                { ReferenceResponses = agent.ReferenceResponses });
            foreach (var (response, completion) in update.Responses) engine.Upsert(response, completion, true);
        }
        Apply(source.Seed(Day));
        var live = engine.Compute(now, Day, false);
        Assert.Equal((0, 0, 30), (live.Today.Requests, live.Today.Output, live.Live.Output));
        Copy("completed");
        now = Ts.Ms("2026-10-20T12:00:22Z");
        var completion = source.Refresh(true);
        Assert.Empty(Assert.Single(completion.Agents).ReferenceResponses);
        Apply(completion);
        var done = engine.Compute(now, Day, false);
        Assert.Equal((2, 840, 0), (done.Today.Requests, done.Today.Output, done.Live.Output));
        Assert.False(source.Refresh(true).Changed);
    }

    [Fact]
    public void SharedActiveTurnCasesUseReferencesWithoutInventingAccounting()
    {
        using var expected = JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("trae-live-reference-expected.json")));
        foreach (var item in expected.RootElement.GetProperty("cases").EnumerateArray())
        {
            var root = FixtureLocator.PathOf(item.GetProperty("root").GetString()!);
            var now = item.GetProperty("nowMs").GetInt64();
            var source = new TraeSource(root, new Deduper()) { WallClockMs = () => now };
            var update = source.Seed(Day);
            Assert.Equal(item.GetProperty("expectedReferenceCount").GetInt32(), update.Agents.Sum(agent => agent.ReferenceResponses.Count));
            Assert.Equal(item.GetProperty("phase").GetString(), update.Agents.SingleOrDefault()?.Phase.ToString().ToLowerInvariant() ?? "idle");
            Assert.False(source.Refresh(true).Changed);
            var locations = new SourceLocations("", "", "", "", [], TraeStores: [root]);
            using var monitor = new MonitorCore(new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Trae }), locations, Day) { Clock = () => now };
            monitor.Seed(now, Day, CancellationToken.None);
            var snapshot = monitor.BuildSnapshot(now, Day, MonitorState.Running, false).Snapshot;
            Assert.True(Math.Abs(item.GetProperty("expectedLiveOutput").GetDouble() - snapshot.Live.Output) < 0.000001,
                item.GetProperty("name").GetString() + " live speed differs");
            Assert.Equal(item.GetProperty("expectedRequests").GetInt64(), snapshot.Today.Requests);
            Assert.Equal(item.GetProperty("expectedOutput").GetInt64(), snapshot.Today.Output);
        }
    }
}
