using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

/// <summary>Incremental reads of one conversation database while the language server writes it (METRICS 4.8, 5.4).</summary>
public sealed class AntigravityLiveTests : IDisposable
{
    private const string Id = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";

    private readonly TempDirectory _temp = new();
    private readonly long _t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;

    public void Dispose() => _temp.Dispose();

    private string Conversations => _temp.Combine("antigravity-cli", "conversations");

    private string DbPath => AgWriter.ConversationPath(Conversations, Id);

    [Fact]
    public void IncrementalReadsFollowThePhaseAndEndEqualToACompleteRead()
    {
        using var writer = new AgWriter(DbPath).Trajectory(AgBlobs.Trajectory(_t, "file:///w/nova"));
        var reader = new Reader(DbPath, new Deduper());

        writer.Step(0, AgWriter.UserInput, AgWriter.Done, AgBlobs.Step(created: _t + 100));
        reader.Read();
        Assert.Equal((AgentPhase.Generating, (long?)(_t + 100), "nova"), (reader.Parser.Phase, reader.Parser.InFlightStartMs, reader.Parser.Project));

        writer.Step(1, AgWriter.Planner, AgWriter.Generating, AgBlobs.Step(created: _t + 200, usage: AgBlobs.Usage(1000, 10)));
        reader.Read();
        Assert.Equal((AgentPhase.Generating, (long?)(_t + 200)), (reader.Parser.Phase, reader.Parser.InFlightStartMs));
        Assert.Empty(reader.Sink.Emissions);

        writer.UpdateStep(1, AgWriter.Done, AgBlobs.Step(_t + 200, _t + 2200, usage: AgBlobs.Usage(1000, 100, 200, 30, 70, "r1")))
            .Step(2, AgWriter.RunCommand, AgWriter.Running, AgBlobs.Step(created: _t + 2300));
        reader.Read();
        Assert.Equal(AgentPhase.Tools, reader.Parser.Phase);
        var first = Assert.Single(reader.Sink.Emissions);
        Assert.True(first.IsCompletion);
        Assert.Equal(("r1", (long?)(_t + 200), _t + 2200, (string?)null), (first.Response.Key, first.Response.StartMs, first.Response.EndMs, first.Response.Model));
        Assert.Equal(new TokenCounts(1200, 200, 0, 100, 30), first.Response.Tokens);

        writer.UpdateStep(2, AgWriter.Done, AgBlobs.Step(created: _t + 2300, completed: _t + 3000));
        reader.Read();
        Assert.Equal((AgentPhase.Generating, (long?)(_t + 3000)), (reader.Parser.Phase, reader.Parser.InFlightStartMs));

        writer.Step(3, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(_t + 3010, _t + 4010, usage: AgBlobs.Usage(1200, 50, 1000, 0, 50, "r2")))
            .Generation(0, AgBlobs.Generation("gemini-3.1-pro", 1, 3))
            .Executor(0, AgBlobs.Executor(reason: 4, lastStep: 3, model: "planner"));
        reader.Read();

        Assert.Equal((AgentPhase.Idle, (string?)"gemini-3.1-pro", (long?)(_t + 4010)), (reader.Parser.Phase, reader.Parser.Model, reader.Parser.LastLineMs));
        Assert.Equal([("r1", true), ("r2", true), ("r1", false)], reader.Sink.Emissions.Select(e => (e.Response.Key, e.IsCompletion)));
        Assert.All(reader.Sink.Latest, r => Assert.Equal("gemini-3.1-pro", r.Model));

        var complete = new Reader(DbPath, new Deduper());
        complete.Read();
        Assert.Equal(complete.Sink.Latest, reader.Sink.Latest);
        Assert.Equal(
            (complete.Parser.Phase, complete.Parser.Model, complete.Parser.Project, complete.Parser.LastLineMs),
            (reader.Parser.Phase, reader.Parser.Model, reader.Parser.Project, reader.Parser.LastLineMs));
    }

    [Fact]
    public void AnOpenStepIsReReadUntilItEndsAndMalformedMetadataCountsOnce()
    {
        using var writer = new AgWriter(DbPath).Trajectory(AgBlobs.Trajectory(_t, "/w/orbit"));
        var reader = new Reader(DbPath, new Deduper());
        writer.Step(0, AgWriter.Planner, AgWriter.Generating, [0x0A, 0x05, 0x01]);
        reader.Read();
        reader.Read();
        writer.Step(1, AgWriter.UserInput, AgWriter.Done, AgBlobs.Step(created: _t + 50));
        reader.Read();
        Assert.Equal(1, reader.Parser.MalformedLines);

        writer.UpdateStep(0, AgWriter.Done, AgBlobs.Step(_t + 10, _t + 1010, usage: AgBlobs.Usage(500, 40, responseId: "late")));
        reader.Read();
        reader.Read();

        var response = Assert.Single(reader.Sink.Latest);
        Assert.Equal(("late", (long?)(_t + 10), _t + 1010), (response.Key, response.StartMs, response.EndMs));
        Assert.Equal(1, reader.Parser.MalformedLines);
        Assert.Single(reader.Sink.Emissions);
    }

    [Fact]
    public void FailedAndOpenPlannerRowsFollowTheTotalsOnlyAndInFlightRules()
    {
        using var writer = new AgWriter(DbPath).Trajectory(AgBlobs.Trajectory(_t, "/w/orbit"));
        writer.Step(0, AgWriter.Planner, AgWriter.Canceled, AgBlobs.Step(_t + 10, _t + 900, usage: AgBlobs.Usage(300, 12, responseId: "c")))
            .Step(1, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(created: _t + 1000, usage: AgBlobs.Usage(0, 0)))
            .Step(2, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(created: null, usage: AgBlobs.Usage(10, 1, responseId: "notime")))
            .Step(3, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(created: _t + 2000, completed: _t + 2600, usage: AgBlobs.Usage(10, 2)));
        var reader = new Reader(DbPath, new Deduper());
        reader.Read();

        Assert.Equal(
            [("c", null, _t + 900), ($"antigravity:{Id}:3", _t + 2000, _t + 2600)],
            reader.Sink.Latest.Select(r => (r.Key, r.StartMs, r.EndMs)).ToList());
        Assert.Equal(AgentPhase.Idle, reader.Parser.Phase);
    }

    [Fact]
    public void OversizedBlobsAreNeverReadAndForkCopiesAreIgnored()
    {
        using var writer = new AgWriter(DbPath).Trajectory(AgBlobs.Trajectory(_t, "/w/orbit"));
        var huge = new byte[AntigravityDatabase.MaxBlobBytes + 1];
        writer.Generation(0, AgBlobs.Generation("from-a-huge-row", 1), size: 300_000_000)
            .Executor(0, AgBlobs.Executor(0, 5, "planner-model"))
            .Step(0, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(_t - 60_000, _t - 59_000, usage: AgBlobs.Usage(1, 1, responseId: "copied")))
            .Step(1, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(_t + 10, _t + 1010, usage: AgBlobs.Usage(10, 5, responseId: "own")))
            .Step(2, AgWriter.Planner, AgWriter.Generating, huge);
        var reader = new Reader(DbPath, new Deduper());
        reader.Read();

        var response = Assert.Single(reader.Sink.Latest);
        Assert.Equal(("own", (string?)"planner-model"), (response.Key, response.Model));
        Assert.Equal(0, reader.Parser.MalformedLines);
        Assert.Equal(_t + 1010, reader.Parser.LastLineMs);
    }

    [Fact]
    public void AClosedConversationIsReadImmutablyWithoutCreatingFilesUntilItContinues()
    {
        using (var writer = new AgWriter(DbPath))
        {
            writer.Trajectory(AgBlobs.Trajectory(_t, "/w/orbit"))
                .Step(0, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(_t + 10, _t + 1010, usage: AgBlobs.Usage(10, 5, responseId: "a")));
        }

        Assert.False(File.Exists(DbPath + "-wal"));
        var before = Files();
        var reader = new Reader(DbPath, new Deduper());
        reader.Read();
        reader.Read();
        Assert.True(reader.Tailer.IsImmutable);
        Assert.Equal(before, Files());

        using (var writer = new AgWriter(DbPath, schema: false))
        {
            writer.Step(1, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(_t + 2000, _t + 2500, usage: AgBlobs.Usage(10, 7, responseId: "b")));
            Assert.True(File.Exists(DbPath + "-wal"), string.Join(",", Files()));
            reader.Read();
            Assert.False(reader.Tailer.IsImmutable);
        }

        Assert.Equal(["a", "b"], reader.Sink.Latest.Select(r => r.Key));
    }

    [Fact]
    public void ANewIdentityResetsTheConversationAndADatabaseThatStartsAtItsEndIsNotOpened()
    {
        using (var writer = new AgWriter(DbPath))
        {
            writer.Trajectory(AgBlobs.Trajectory(_t, "/w/orbit"))
                .Step(0, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(_t + 10, _t + 1010, usage: AgBlobs.Usage(10, 5, responseId: "a")));
        }

        var stamp = AntigravityStamp.Of(DbPath)!.Value;
        var reader = new Reader(DbPath, new Deduper());
        reader.Tailer.StartAtEnd(stamp.Length, stamp.LastWriteUtc);
        Assert.Equal(0, reader.Read().BytesRead);
        Assert.False(reader.Tailer.HasConnection);
        Assert.True(reader.Tailer.NeedsCompleteRead);

        using (var writer = new AgWriter(DbPath, schema: false))
        {
            writer.Step(1, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(_t + 2000, _t + 2500, usage: AgBlobs.Usage(10, 7, responseId: "b")));
        }

        Assert.True(reader.Read().BytesRead > 0);
        Assert.Equal(["a", "b"], reader.Sink.Latest.Select(r => r.Key));

        File.Delete(DbPath);
        Thread.Sleep(20);
        using (var writer = new AgWriter(DbPath))
        {
            writer.Trajectory(AgBlobs.Trajectory(_t, "/w/other"))
                .Step(0, AgWriter.Planner, AgWriter.Done, AgBlobs.Step(_t + 10, _t + 3010, usage: AgBlobs.Usage(10, 9, responseId: "c")));
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            var result = reader.Read();
            Assert.True(result.WasReset);
            Assert.Equal("other", reader.Parser.Project);
            Assert.Equal("c", reader.Sink.Latest[^1].Key);
        }
    }

    [Fact]
    public void ADatabaseWithoutAStepsTableIsUnsupported()
    {
        using var writer = new AgWriter(DbPath, schema: false).Sql("CREATE TABLE trajectory_steps(idx INTEGER, payload BLOB)");
        var reader = new Reader(DbPath, new Deduper());
        reader.Read();

        Assert.True((reader.Parser.Unsupported, reader.Tailer.Unsupported) == (true, true));
        Assert.Null(reader.Parser.LastLineMs);
    }

    private List<string> Files() => [.. Directory.EnumerateFiles(Conversations).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    /// <summary>A tailer and parser applied the way the monitor does (reset first when the database was replaced).</summary>
    private sealed class Reader(string path, IDeduper deduper)
    {
        public AntigravityParser Parser { get; } = new(path, deduper);

        public AntigravityTailer Tailer { get; } = new(path);

        public RecordingSink Sink { get; } = new();

        public IO.TailReadResult Read()
        {
            var batch = Parser.NewBatch();
            var result = Tailer.ReadAppended(batch);
            if (result.WasReset)
            {
                Parser.Reset();
            }

            batch.ApplyTo(Sink);
            return result;
        }
    }
}
