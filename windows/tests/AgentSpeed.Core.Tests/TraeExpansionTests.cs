using System.Text.Json;
using AgentSpeed.Core.History;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Trae;

namespace AgentSpeed.Core.Tests;

public sealed class TraeExpansionTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private static readonly long Start = Ts.Ms("2026-10-20T00:00:00Z");
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void LateExactMetadataAddsEarlierKeysAndLogRotationNeverRemovesTheirUsage()
    {
        var root = CopyCase("multi-call", "first");
        var path = TraePaths.DatabasePath(root);
        var full = File.ReadAllBytes(path);
        File.WriteAllBytes(path, TraeSnapshot.Read(path));
        using (var writer = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate))
            writer.Execute("DELETE FROM server_history_info WHERE client_history_id='history-earlier'");
        var source = new TraeSource(root, new Deduper());
        Assert.Equal(3, source.Seed(Start).Responses.Count);
        var locations = new SourceLocations("", "", "", "", [], TraeStores: [root]);
        var enabled = new HashSet<SourceKind> { SourceKind.Trae };
        var historyPath = _temp.Combine("history.sqlite");
        using (var store = HistoryStore.Open(historyPath))
        {
            var history = new HistoryAnalyzer(store);
            history.Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None);
            Assert.Equal(3, Assert.Single(history.Days()).Totals.Requests);
        }
        File.WriteAllBytes(path, full);
        var added = Assert.Single(source.Refresh(true).Responses);
        Assert.Equal("trae:history:s-main:history-earlier", added.Response.Key);
        Assert.True(added.Completion);
        Assert.Equal(60, added.Response.Tokens.Output);
        Assert.False(source.Refresh(true).Changed);
        using (var store = HistoryStore.Open(historyPath))
        {
            var history = new HistoryAnalyzer(store);
            history.Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None);
            var day = Assert.Single(history.Days());
            Assert.Equal((4, 400, 300), (day.Totals.Requests, day.Totals.Output, day.MeasuredOutput));
        }
        Directory.Delete(Path.Combine(root, "logs"), recursive: true);
        var changed = source.Refresh(true).Responses;
        Assert.Equal(2, changed.Count);
        Assert.All(changed, response => { Assert.False(response.Completion); Assert.Null(response.Response.StartMs); });
        var copy = CopyCase("multi-no-logs", "second");
        using var reopened = HistoryStore.Open(historyPath);
        var restored = new HistoryAnalyzer(reopened);
        restored.Run(locations with { TraeStores = [root, copy] }, enabled, TimeZoneInfo.Utc, CancellationToken.None);
        var final = Assert.Single(restored.Days());
        Assert.Equal((4, 400, 0), (final.Totals.Requests, final.Totals.Output, final.MeasuredOutput));
    }

    [Fact]
    public void SharedLifecycleCasesUseExactLiveRootsWithoutAddingTokens()
    {
        using var expected = JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("trae-activity-expected.json")));
        foreach (var item in expected.RootElement.GetProperty("cases").EnumerateArray())
        {
            var path = FixtureLocator.PathOf(item.GetProperty("database").GetString()!);
            var now = item.GetProperty("nowMs").GetInt64();
            var source = new TraeSource(path, new Deduper()) { WallClockMs = () => now };
            var update = source.Seed(Start);
            var agents = update.Agents.GroupBy(agent => agent.SessionId).Select(group => group.Last()).OrderBy(agent => agent.SessionId).ToArray();
            var wanted = item.GetProperty("expected").EnumerateArray().ToArray();
            Assert.Equal(wanted.Length, agents.Length);
            Assert.Single(update.Responses); // The completed reference is counted; live Fetch never creates a response.
            for (var index = 0; index < agents.Length; index++)
            {
                Assert.Equal(wanted[index].GetProperty("sessionId").GetString(), agents[index].SessionId);
                Assert.True(wanted[index].GetProperty("phase").GetString() == agents[index].Phase.ToString().ToLowerInvariant(),
                    item.GetProperty("name").GetString() + " phase differs");
                var start = wanted[index].GetProperty("inFlightStartMs");
                Assert.Equal(start.ValueKind == JsonValueKind.Null ? null : start.GetInt64(), agents[index].InFlightStartMs);
                Assert.Equal(wanted[index].GetProperty("model").GetString(), agents[index].Model);
            }
        }
    }

    [Fact]
    public void OversizedLogBudgetKeepsEveryVerifiedUsageRecord()
    {
        var root = CopyCase("multi-call", "bounded");
        var logDirectory = Directory.GetDirectories(Path.Combine(root, "logs"), "Modular", SearchOption.AllDirectories).Single();
        using (var oversized = File.Create(Path.Combine(logDirectory, "ai_agent-oversized.alaudalog")))
            oversized.SetLength(TraeAlauda.MaxCompressed + 1L);
        var source = new TraeSource(root, new Deduper());
        var responses = source.Seed(Start).Responses;
        Assert.Equal(4, responses.Count);
        Assert.Equal(400, responses.Sum(response => response.Response.Tokens.Output));
        Assert.Contains(responses, response => response.Response.StartMs is not null);
        Assert.False(source.Refresh(true).Changed);
    }

    [Fact]
    public void UnchangedLogsExpireLiveActivityWithoutCountingAnotherResponse()
    {
        var root = CopyCase("generating", "stale", activity: true);
        var now = Ts.Ms("2026-10-20T12:00:22Z");
        var source = new TraeSource(root, new Deduper()) { WallClockMs = () => now };
        Assert.Equal(AgentPhase.Generating, Assert.Single(source.Seed(Start).Agents).Phase);
        now += 300_001;
        var expired = source.Refresh();
        Assert.Empty(expired.Responses);
        Assert.Equal(AgentPhase.Idle, Assert.Single(expired.Agents).Phase);
        Assert.False(source.Refresh().Changed);
    }

    [Fact]
    public void LiveLifecycleUsesTheExistingEstimatorAndCopiedStoresCountOnce()
    {
        var root = CopyCase("generating", "live", activity: true);
        var copy = CopyCase("generating", "copy", activity: true);
        var now = Ts.Ms("2026-10-20T12:00:22Z");
        var locations = new SourceLocations("", "", "", "", [], TraeStores: [root, copy]);
        var enabled = new HashSet<SourceKind> { SourceKind.Trae };
        using (var monitor = new MonitorCore(new MonitorConfiguration(enabled), locations, Start) { Clock = () => now })
        {
            monitor.Seed(now, Start, CancellationToken.None);
            var snapshot = monitor.BuildSnapshot(now, Start, MonitorState.Running, false).Snapshot;
            Assert.Equal(AgentPhase.Generating, Assert.Single(snapshot.Agents).Phase);
            Assert.Equal((1, 240), (snapshot.Today.Requests, snapshot.Today.Output));
            Assert.Equal(9, snapshot.Live.Output, 8); // 30 tok/s reference over 0.9 s of the 3 s window.
            Assert.Equal(1, snapshot.LiveEstimatedShare);
            Assert.Equal(1, monitor.SessionCount(SourceKind.Trae, Start));
        }
        var path = TraePaths.DatabasePath(root);
        File.WriteAllBytes(path, TraeSnapshot.Read(path));
        using (var writer = SqliteConnection.Open(path, SqliteOpenMode.ReadWriteCreate)) writer.Execute("DELETE FROM chat_turn WHERE turn_id='t-one'");
        using var withoutReference = new MonitorCore(new MonitorConfiguration(enabled), locations with { TraeStores = [root] }, Start) { Clock = () => now };
        withoutReference.Seed(now, Start, CancellationToken.None);
        var missing = withoutReference.BuildSnapshot(now, Start, MonitorState.Running, false).Snapshot;
        Assert.Equal(AgentPhase.Generating, Assert.Single(missing.Agents).Phase);
        Assert.Equal(0, missing.Today.Requests);
        Assert.Equal(0, missing.Live.Output);
        Assert.Equal(0, missing.LiveEstimatedShare);
    }

    [Fact]
    public void LiveTransitionsUpdatePhaseWithoutNewResponseCounts()
    {
        var root = CopyCase("generating", "flow", activity: true);
        var now = Ts.Ms("2026-10-20T12:00:22Z");
        var source = new TraeSource(root, new Deduper()) { WallClockMs = () => now };
        var seeded = source.Seed(Start);
        Assert.Single(seeded.Responses);
        Assert.Equal(AgentPhase.Generating, seeded.Agents.Last().Phase);
        foreach (var (name, phase, time) in new[] { ("tools", AgentPhase.Tools, 24000L), ("next-fetch", AgentPhase.Generating, 25000L), ("completed", AgentPhase.Idle, 26000L) })
        {
            CopyCase(name, "flow", activity: true, overwrite: true);
            now = Ts.Ms("2026-10-20T12:00:00Z") + time;
            var update = source.Refresh(true);
            Assert.Empty(update.Responses);
            Assert.Equal(phase, update.Agents.Last().Phase);
        }
        Assert.False(source.Refresh(true).Changed);
    }

    private string CopyCase(string name, string destination, bool activity = false, bool overwrite = false)
    {
        var source = FixtureLocator.PathOf("trae/" + (activity ? "activity-" : "timing-") + name);
        var path = Path.Combine(CanonicalDirectory(_temp.Path), destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(path, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
        return path;
    }

    private static string CanonicalDirectory(string directory)
    {
        var root = Path.GetPathRoot(directory)!;
        foreach (var part in directory[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            root = Path.Combine(root, part);
            if (new DirectoryInfo(root).ResolveLinkTarget(true) is { } resolved) root = resolved.FullName;
        }
        return root;
    }
}
