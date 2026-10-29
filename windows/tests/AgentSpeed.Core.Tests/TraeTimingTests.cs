using System.Text.Json;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Trae;

namespace AgentSpeed.Core.Tests;

public sealed class TraeTimingTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private static readonly long Start = Ts.Ms("2026-10-20T00:00:00Z");
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void SharedNativeShapeCasesRequireExactUsageAndUnambiguousCompletedTransport()
    {
        using var expected = JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("trae-timing-expected.json")));
        foreach (var item in expected.RootElement.GetProperty("cases").EnumerateArray())
        {
            var path = FixtureLocator.PathOf(item.GetProperty("database").GetString()!);
            using var database = TraeDatabase.Open(path);
            var actual = database.Read().Select(row => row.Response(path)).ToArray();
            var wanted = item.GetProperty("responses").EnumerateArray().ToArray();
            Assert.Equal(wanted.Length, actual.Length);
            for (var index = 0; index < actual.Length; index++)
            {
                Assert.Equal(wanted[index].GetProperty("key").GetString(), actual[index].Key);
                var start = wanted[index].GetProperty("startMs");
                Assert.True((start.ValueKind == JsonValueKind.Null ? null : start.GetInt64()) == actual[index].StartMs,
                    item.GetProperty("name").GetString() + " timing differs");
                Assert.Equal(wanted[index].GetProperty("endMs").GetInt64(), actual[index].EndMs);
                Assert.Equal(wanted[index].GetProperty("output").GetInt64(), actual[index].Tokens.Output);
                Assert.Equal(wanted[index].GetProperty("input").GetInt64(), actual[index].Tokens.Input);
                Assert.Equal(wanted[index].GetProperty("cachedInput").GetInt64(), actual[index].Tokens.CachedInput);
                Assert.Equal(wanted[index].GetProperty("reasoning").GetInt64(), actual[index].Tokens.Reasoning);
            }
        }
    }

    [Fact]
    public void LogOnlyArrivalUpdatesLiveAndPersistedHistoryWithoutCountingAnotherRequest()
    {
        var (path, log) = CopyValid(includeLog: false);
        var source = new TraeSource(path, new Deduper());
        var first = source.Seed(Start);
        Assert.Equal(3, first.Responses.Count);
        Assert.All(first.Responses, response => Assert.Null(response.Response.StartMs));
        var locations = new SourceLocations("", "", "", "", [], TraeStores: [path]);
        var enabled = new HashSet<SourceKind> { SourceKind.Trae };
        var storePath = _temp.Combine("history.sqlite");
        using (var store = HistoryStore.Open(storePath))
        {
            var history = new HistoryAnalyzer(store);
            history.Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None);
            Assert.Null(Assert.Single(history.Days()).AverageSpeed);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.Copy(FixtureLocator.PathOf("trae/timing-valid/logs/20261020T120000/Modular/ai_agent-0-2026-10-20.alaudalog"), log);
        var upgraded = Assert.Single(source.Refresh(true).Responses);
        Assert.False(upgraded.Completion);
        Assert.Equal(8000, upgraded.Response.EndMs - upgraded.Response.StartMs);
        Assert.False(source.Refresh(true).Changed);
        using (var store = HistoryStore.Open(storePath))
        {
            var history = new HistoryAnalyzer(store);
            history.Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None);
            var day = Assert.Single(history.Days());
            Assert.Equal((3, 340, 240, 8000), (day.Totals.Requests, day.Totals.Output, day.MeasuredOutput, day.MeasuredMs));
            Assert.Equal(30, day.AverageSpeed);
        }
        File.Delete(log);
        var lost = Assert.Single(source.Refresh(true).Responses);
        Assert.False(lost.Completion);
        Assert.Null(lost.Response.StartMs);
        using var reopened = HistoryStore.Open(storePath);
        var restored = new HistoryAnalyzer(reopened);
        restored.Run(locations, enabled, TimeZoneInfo.Utc, CancellationToken.None);
        Assert.Null(Assert.Single(restored.Days()).AverageSpeed);
    }

    [Fact]
    public void CorruptOrLinkedLogsNeverInventTimingAndTheSourceStaysIdle()
    {
        var (path, log) = CopyValid(includeLog: true);
        var bytes = File.ReadAllBytes(log);
        bytes[8] ^= 1;
        File.WriteAllBytes(log, bytes);
        using (var db = TraeDatabase.Open(path)) Assert.All(db.Read(), row => Assert.Null(row.StartMs));
        File.Delete(log);
        if (!OperatingSystem.IsWindows()) // Windows symlink creation can require privileges absent on CI.
        {
            File.CreateSymbolicLink(log, FixtureLocator.PathOf("trae/timing-valid/logs/20261020T120000/Modular/ai_agent-0-2026-10-20.alaudalog"));
            using (var db = TraeDatabase.Open(path)) Assert.All(db.Read(), row => Assert.Null(row.StartMs));
            File.Delete(log);
        }
        File.Copy(FixtureLocator.PathOf("trae/timing-valid/logs/20261020T120000/Modular/ai_agent-0-2026-10-20.alaudalog"), log);
        var locations = new SourceLocations("", "", "", "", [], TraeStores: [path]);
        using var monitor = new MonitorCore(new MonitorConfiguration(new HashSet<SourceKind> { SourceKind.Trae }), locations, Start);
        monitor.Seed(Start + 50_000_000, Start, CancellationToken.None);
        var snapshot = monitor.BuildSnapshot(Start + 50_000_000, Start, MonitorState.Running, false).Snapshot;
        Assert.Equal(0, snapshot.Live.Output);
        Assert.Equal(30, snapshot.AverageToday);
        Assert.Contains(snapshot.Warnings, warning => warning is MonitorWarning.PartialHistory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OlderLogBudgetsPreserveRecentTiming(bool warmCache)
    {
        var (path, log) = CopyValid(includeLog: true);
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow);
        using var database = TraeDatabase.Open(path);
        var timing = new TraeTiming();
        if (warmCache) database.Read(timing: timing);
        var unrelated = Path.Combine(Path.GetDirectoryName(log)!, "ai_agent-unrelated.alaudalog");
        foreach (var size in new[] { TraeAlauda.MaxCompressed + 1L, TraeAlauda.MaxCompressed })
        {
            using (var handle = File.Create(unrelated)) handle.SetLength(size);
            // Skip individually oversized files even when newest; stop before older files that
            // cross the aggregate budget without throwing away the valid recent graph.
            File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddMinutes(size > TraeAlauda.MaxCompressed ? 1 : -1));
            for (var repeat = 0; repeat < 2; repeat++)
            {
                var rows = database.Read(timing: timing);
                Assert.Equal(3, rows.Count);
                Assert.Equal(340, rows.Sum(row => row.Tokens.Output));
                var measured = Assert.Single(rows, row => row.StartMs is not null);
                Assert.Equal(240, measured.Tokens.Output);
                Assert.Equal(8000, measured.TimingEndMs - measured.StartMs);
            }
        }
        File.Delete(log);
        var remaining = database.Read(timing: timing);
        Assert.Equal(3, remaining.Count);
        Assert.Equal(340, remaining.Sum(row => row.Tokens.Output));
        Assert.All(remaining, row => Assert.Null(row.StartMs));
    }

    private (string Database, string Log) CopyValid(bool includeLog)
    {
        var root = CanonicalDirectory(_temp.Path);
        var path = Path.Combine(root, "Trae", "ModularData", "ai-agent", "database.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(FixtureLocator.PathOf("trae/timing-valid/ModularData/ai-agent/database.db"), path);
        var log = Path.Combine(root, "Trae", "logs", "20261020T120000", "Modular", "ai_agent-0-2026-10-20.alaudalog");
        if (includeLog)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.Copy(FixtureLocator.PathOf("trae/timing-valid/logs/20261020T120000/Modular/ai_agent-0-2026-10-20.alaudalog"), log);
        }
        return (path, log);
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
