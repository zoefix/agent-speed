using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Engine;
using AgentSpeed.Core.History;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.OpenBitFun;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

public sealed class OpenBitFunTests
{
    [Fact]
    public void SharedTracesUseAttemptTimingAndDeduplicateCopies()
    {
        using var expected = JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("openbitfun-expected.json")));
        var deduper = new Deduper();
        foreach (var file in expected.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = FixtureLocator.PathOf(file.GetProperty("path").GetString()!);
            var parser = new OpenBitFunParser(path, deduper);
            var tailer = new OpenBitFunTailer(path);
            var batch = parser.NewBatch();
            var sink = new RecordingSink();
            tailer.ReadAppended(batch);
            batch.ApplyTo(sink);
            HarnessFixtureTests.AssertResponses(file.GetProperty("responses"), sink.Latest);
            Assert.Equal(file.GetProperty("project").GetString(), parser.Project);
            Assert.Equal(file.GetProperty("parent").GetString(), parser.ParentSessionId);
            Assert.Equal(file.GetProperty("isSubagent").GetBoolean(), parser.IsSubagent);
            Assert.Equal(file.GetProperty("phase").GetString(), parser.Phase.ToString().ToLowerInvariant());
            Assert.Equal(sink.Latest.Count, sink.Emissions.Count(e => e.IsCompletion));
            batch.Clear();
            Assert.Equal(0, tailer.ReadAppended(batch).BytesRead);
            Assert.Equal(0, batch.Count);
        }
    }

    [Fact]
    public void LiveAndHistoryReadSessionDocumentsAndReuseExistingEngineMath()
    {
        using var temp = new TempDirectory();
        var root = FixtureLocator.PathOf("openbitfun/projects");
        var paths = new SourceLocations("", "", "", "", []) { OpenBitFunRoots = [root] };
        var enabled = new HashSet<SourceKind> { SourceKind.OpenBitFun };
        const long day = 1_792_454_400_000, now = 1_792_497_613_000;
        using (var core = new MonitorCore(new MonitorConfiguration(enabled), paths, day))
        {
            core.Seed(now, day, CancellationToken.None);
            var (snapshot, _) = core.BuildSnapshot(now, day, MonitorState.Running, observeLive: false);
            Assert.Equal(4, snapshot.Today.Requests);
            Assert.Equal(240, snapshot.Today.Output);
            Assert.Contains(snapshot.Agents, a => a.Source == SourceKind.OpenBitFun && a.IsSubagent && a.ParentId is not null);
        }
        using var store = HistoryStore.Open(temp.Combine("history.sqlite"));
        var history = new HistoryAnalyzer(store);
        Assert.Equal(0, history.Run(paths, enabled, TimeZoneInfo.Utc, CancellationToken.None).FilesUnreadable);
        Assert.Equal(240, history.Days().Sum(day => day.Totals.Output));
        Assert.Equal(0, history.Run(paths, enabled, TimeZoneInfo.Utc, CancellationToken.None).FilesPlanned);
    }

    [Fact]
    public void AReplacedTraceIsReadEvenWhenMetadataDoesNotChange()
    {
        using var temp = new TempDirectory();
        var metadata = temp.Combine("projects/demo/sessions/main/metadata.json");
        var trace = temp.Combine("projects/demo/request-traces/main/request-000005.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        Directory.CreateDirectory(Path.GetDirectoryName(trace)!);
        File.Copy(FixtureLocator.PathOf("openbitfun/projects/demo/sessions/main/metadata.json"), metadata);
        File.Copy(FixtureLocator.PathOf("openbitfun/projects/demo/request-traces/main/request-000005.json"), trace);
        var parser = new OpenBitFunParser(metadata, new Deduper());
        var tailer = new OpenBitFunTailer(metadata);
        var batch = parser.NewBatch();
        var sink = new RecordingSink();
        tailer.ReadAppended(batch);
        batch.ApplyTo(sink);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Empty(sink.Latest);
        var before = OpenBitFunFiles.Stat(metadata);
        File.Copy(FixtureLocator.PathOf("openbitfun/completed-request.json"), trace, overwrite: true);
        Assert.NotEqual(before, OpenBitFunFiles.Stat(metadata));
        Assert.Equal(Path.GetFullPath(metadata), OpenBitFunFiles.MetadataOf(trace));
        batch.Clear();
        tailer.ReadAppended(batch);
        batch.ApplyTo(sink);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Equal(80, Assert.Single(sink.Latest).Tokens.Output);
    }

    [Fact]
    public async Task SessionUsesOneSourceAsDailyUsageRecordsAppearAndDisappear()
    {
        using var expected = JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("openbitfun-expected.json")));
        var fixture = expected.RootElement.GetProperty("ledger");
        var path = FixtureLocator.PathOf(fixture.GetProperty("metadata").GetString()!);
        using var temp = new TempDirectory();
        var daily = temp.Combine("2026-10-20.json");
        var ledger = new OpenBitFunUsageLedger(Path.GetDirectoryName(daily)!);
        var tailer = new OpenBitFunTailer(path, ledger);
        var parser = new OpenBitFunParser(path, new Deduper());
        var sink = new RecordingSink();

        const long now = 1_792_497_606_000;
        var engine = new SpeedEngine(0);
        void ReadAndCheck(long requests, long output, long reportedRequests, long reportedOutput)
        {
            var batch = parser.NewBatch();
            var from = sink.Emissions.Count;
            tailer.ReadAppended(batch);
            batch.ApplyTo(sink);
            foreach (var (record, completion) in sink.Emissions.Skip(from))
                engine.Upsert(record, completion, seeded: false, liveNowMs: now);
            var frame = engine.Compute(now, 0, observeLive: false);
            Assert.Equal((requests, output), (frame.Today.Requests, frame.Today.Output));
            Assert.Equal((reportedRequests, reportedOutput), (frame.Report.Counters.Total.Requests, frame.Report.Counters.Total.Output));
        }

        ReadAndCheck(1, 25, 1, 25);
        var document = JsonNode.Parse(File.ReadAllBytes(FixtureLocator.PathOf(Path.Combine(
            fixture.GetProperty("root").GetString()!, "2026-10-20.json"))))!;
        var rows = document["records"]!.AsArray();
        File.WriteAllText(daily, document.ToJsonString());
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        ReadAndCheck(2, 200, 2, 200);
        var own = ledger.ForSession("session");
        var sibling = ledger.ForSession("other");
        rows.Last()!["output_tokens"] = 901;
        File.WriteAllText(daily, document.ToJsonString());
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        Assert.Same(own, ledger.ForSession("session"));
        Assert.NotSame(sibling, ledger.ForSession("other"));
        Assert.Equal(0, tailer.ReadAppended(parser.NewBatch()).BytesRead);

        rows.RemoveAt(1); // The completed round now has only one of its two attempts.
        File.WriteAllText(daily, document.ToJsonString());
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        ReadAndCheck(0, 0, 2, 200);
        var revised = OpenBitFunFiles.Stat(path, ledger.ForSession("session"));
        var reopened = new OpenBitFunUsageLedger(Path.GetDirectoryName(daily)!);
        Assert.NotEqual(OpenBitFunFiles.Stat(path, own), revised);
        Assert.Equal(revised, OpenBitFunFiles.Stat(path, reopened.ForSession("session")));
        var cold = new OpenBitFunParser(path, new Deduper());
        var coldBatch = cold.NewBatch();
        new OpenBitFunTailer(path, ledger).ReadAppended(coldBatch);
        var coldSink = new RecordingSink();
        coldBatch.ApplyTo(coldSink);
        Assert.Empty(coldSink.Latest);

        File.Delete(daily);
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        ReadAndCheck(1, 25, 2, 200);
    }

    [Theory]
    [InlineData("completed", true)]
    [InlineData("completed", false)]
    [InlineData("cancelled", true)]
    [InlineData("cancelled", false)]
    [InlineData("error", true)]
    [InlineData("error", false)]
    public void EndedTurnStopsEstimatingEvenWhenARoundRemainsStreaming(string status, bool useLedger)
    {
        const long start = 1_792_497_600_000, end = start + 6000;
        using var expected = JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("openbitfun-expected.json")));
        var fixture = expected.RootElement.GetProperty("ledger");
        var metadata = FixtureLocator.PathOf(fixture.GetProperty("metadata").GetString()!);
        var folder = new FileInfo(metadata).Directory!;
        var ledger = new OpenBitFunUsageLedger(FixtureLocator.PathOf(fixture.GetProperty("root").GetString()!));
        var parser = new OpenBitFunParser(metadata, new Deduper());
        var batch = (OpenBitFunBatch)parser.NewBatch();
        var sink = new RecordingSink();
        var turn = JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder.FullName, "turns", "turn-000001.json")))!;
        if (useLedger) batch.Usage = ledger.ForSession(folder.Name);
        batch.OnLine(File.ReadAllBytes(metadata));
        if (!useLedger)
            foreach (var trace in OpenBitFunFiles.Traces(metadata)) batch.OnLine(File.ReadAllBytes(trace.FullName));
        batch.OnLine(JsonSerializer.SerializeToUtf8Bytes(turn));
        batch.ApplyTo(sink);
        Assert.Equal(AgentPhase.Generating, parser.Phase);
        Assert.Equal(start + 5000, parser.InFlightStartMs);

        var engine = new SpeedEngine(0);
        foreach (var emission in sink.Emissions)
            engine.Upsert(emission.Response, emission.IsCompletion, seeded: false);
        engine.UpdateAgent(new AgentInfo(parser.AgentId, parser.Source, null, parser.Model, false,
            parser.Phase, parser.InFlightStartMs, parser.LastLineMs));
        Assert.True(engine.Compute(end + 60_000, 0, observeLive: false).Live.Output > 0);

        batch.Clear();
        turn["status"] = status;
        turn["endTime"] = end;
        batch.OnLine(JsonSerializer.SerializeToUtf8Bytes(turn));
        batch.ApplyTo(sink);
        Assert.Equal(AgentPhase.Idle, parser.Phase);
        Assert.Null(parser.InFlightStartMs);
        // The turn end closes activity without inventing timing for the unfinished round.
        if (useLedger) HarnessFixtureTests.AssertResponses(fixture.GetProperty("responses"), sink.Latest);
        else Assert.Equal(25, Assert.Single(sink.Latest).Tokens.Output);
        Assert.Single(sink.Emissions);

        engine.UpdateAgent(new AgentInfo(parser.AgentId, parser.Source, null, parser.Model, false,
            parser.Phase, parser.InFlightStartMs, parser.LastLineMs));
        var frame = engine.Compute(end + 60_000, 0, observeLive: false);
        Assert.Equal(0, frame.Live.Output);
        Assert.Equal(0, frame.EstimatedShare);
        Assert.Equal(AgentPhase.Idle, Assert.Single(frame.Agents).Phase);
    }
}
