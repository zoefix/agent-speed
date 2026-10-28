using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

/// <summary>Per-source measured buckets and source-set peaks (METRICS 6.6, 6.8).</summary>
public sealed class SubsetPeakEngineTests
{
    private const long B = 1_000_000_000_000;
    private const long Day = B - 3_600_000;

    /// <summary>The first millisecond of a one-second-bucket chunk (<see cref="SecondSeries.ChunkSeconds"/>).</summary>
    private const long ChunkStart = 976_563L * SecondSeries.ChunkSeconds * 1000;

    private static readonly SourceSet Codex = SourceSet.Of(SourceKind.Codex);
    private static readonly SourceSet Claude = SourceSet.Of(SourceKind.ClaudeCode);
    private static readonly SourceSet Both = [SourceKind.Codex, SourceKind.ClaudeCode];

    [Fact]
    public void OverlappingSourcesMakeASetPeakAboveEachSourcesOwn()
    {
        var ledger = new TodayLedger(Day);
        ledger.Upsert(Response("c1", SourceKind.Codex, B, B + 3000, 300));
        ledger.Upsert(Response("k1", SourceKind.ClaudeCode, B + 1000, B + 4000, 600));

        Assert.Equal(100, ledger.MeasuredPeakOutput(Codex), 9);
        Assert.Equal(200, ledger.MeasuredPeakOutput(Claude), 9);
        Assert.Equal(800.0 / 3, ledger.MeasuredPeakOutput(Both), 9);
        Assert.Equal(ledger.Peak().Output, ledger.MeasuredPeakOutput(Both), 9);
        Assert.Equal(0, ledger.MeasuredPeakOutput(SourceSet.Of(SourceKind.Gemini)));
        Assert.Equal(0, ledger.MeasuredPeakOutput(SourceSet.Empty));
        Assert.Equal(Both, ledger.MeasuredSources);
    }

    [Fact]
    public void ASingleSourceSetMatchesTheAllSourcesPeakExactly()
    {
        var ledger = new TodayLedger(Day);
        ledger.Upsert(Response("a", SourceKind.Gemini, B + 123, B + 4567, 997));
        ledger.Upsert(Response("b", SourceKind.Gemini, B + 2000, B + 2300, 71));
        ledger.Upsert(Response("c", SourceKind.Gemini, B + 1_030_000, B + 1_031_700, 333));

        Assert.Equal(ledger.Peak().Output, ledger.MeasuredPeakOutput(SourceSet.Of(SourceKind.Gemini)));
        Assert.Equal(ledger.Peak().Output, ledger.MeasuredPeakOutput(SourceSet.Everything));
    }

    [Fact]
    public void ReplacingAResponseLowersThePeak()
    {
        var ledger = new TodayLedger(Day);
        ledger.Upsert(Response("c1", SourceKind.Codex, B, B + 3000, 300));
        ledger.Upsert(Response("k1", SourceKind.ClaudeCode, B, B + 3000, 900));
        Assert.Equal(400, ledger.MeasuredPeakOutput(Both), 9);
        Assert.Equal(300, ledger.MeasuredPeakOutput(Claude), 9);

        ledger.Upsert(Response("k1", SourceKind.ClaudeCode, B, B + 3000, 90));

        Assert.Equal(130, ledger.MeasuredPeakOutput(Both), 9);
        Assert.Equal(30, ledger.MeasuredPeakOutput(Claude), 9);
        Assert.Equal(100, ledger.MeasuredPeakOutput(Codex), 9);
        Assert.Equal(ledger.Peak().Output, ledger.MeasuredPeakOutput(Both), 9);

        ledger.Upsert(Response("k1", SourceKind.ClaudeCode, B, B + 3000, 90) with { EndMs = Day - 1, StartMs = Day - 3001 });

        Assert.Equal(0, ledger.MeasuredPeakOutput(Claude), 9);
        Assert.Equal(100, ledger.MeasuredPeakOutput(Both), 9);
    }

    [Fact]
    public void TheDayChangeResetsEveryArray()
    {
        var engine = new SpeedEngine(Day);
        engine.UpdateAgent(Agent("a1", SourceKind.Codex));
        engine.Upsert(Response("old", SourceKind.Codex, B, B + 3000, 600), isCompletion: true, seeded: true);
        engine.Compute(B + 3000, Day, observeLive: true);
        Assert.Equal(200, engine.MeasuredPeakOutput(Codex), 9);

        const long next = B + 86_400_000;
        engine.Compute(next, next - 1000, observeLive: true);

        Assert.Equal(0, engine.MeasuredPeakOutput(Codex));
        Assert.Equal(0, engine.MeasuredPeakOutput(Both));
        Assert.True(engine.MeasuredSourcesToday.IsEmpty);

        engine.Upsert(Response("new", SourceKind.Codex, next, next + 3000, 30), isCompletion: true, seeded: false);
        engine.Compute(next + 3000, next - 1000, observeLive: true);
        Assert.Equal(10, engine.MeasuredPeakOutput(Codex), 9);
    }

    [Fact]
    public void ResponsesStillInMemoryAfterMidnightRebuildTheNewDay()
    {
        var engine = new SpeedEngine(Day);
        engine.UpdateAgent(Agent("a1", SourceKind.Codex));
        const long midnight = B + 2000;
        engine.Upsert(Response("across", SourceKind.Codex, B, B + 6000, 600), isCompletion: true, seeded: true);
        engine.Compute(B + 6000, Day, observeLive: false);
        Assert.Equal(100, engine.MeasuredPeakOutput(Codex), 9);

        engine.Compute(B + 6000, midnight, observeLive: false);

        Assert.Equal(engine.Compute(B + 6000, midnight, observeLive: false).PeakToday.Output, engine.MeasuredPeakOutput(Codex));
        Assert.Equal(Codex, engine.MeasuredSourcesToday);
    }

    [Fact]
    public void ASetIsRescannedOnlyWhereOneOfItsSourcesChanged()
    {
        var ledger = new TodayLedger(Day);
        ledger.Upsert(Response("c1", SourceKind.Codex, B, B + 3000, 300));
        ledger.Upsert(Response("k1", SourceKind.ClaudeCode, B + 7_200_000, B + 7_203_000, 300));

        ledger.MeasuredPeakOutput(Codex);
        ledger.MeasuredPeakOutput(Both);
        var scans = ledger.SourcePeakChunkScans;
        Assert.Equal(3, scans);

        for (var tick = 0; tick < 100; tick++)
        {
            ledger.MeasuredPeakOutput(Codex);
            ledger.MeasuredPeakOutput(Both);
        }

        Assert.Equal(scans, ledger.SourcePeakChunkScans);

        ledger.Upsert(Response("k2", SourceKind.ClaudeCode, B + 7_300_000, B + 7_301_000, 50));
        Assert.Equal(100, ledger.MeasuredPeakOutput(Codex), 9);
        Assert.Equal(scans, ledger.SourcePeakChunkScans);
        Assert.Equal(100, ledger.MeasuredPeakOutput(Both), 9);
        Assert.InRange(ledger.SourcePeakChunkScans - scans, 1, 2);

        scans = ledger.SourcePeakChunkScans;
        ledger.Upsert(Response("c2", SourceKind.Codex, B + 3000, B + 6000, 900));
        Assert.Equal(300, ledger.MeasuredPeakOutput(Codex), 9);
        Assert.Equal(300, ledger.MeasuredPeakOutput(Both), 9);
        Assert.Equal(2, ledger.SourcePeakChunkScans - scans);
    }

    [Fact]
    public void AResponseAcrossAChunkBoundaryRescansBothChunks()
    {
        var ledger = new TodayLedger(Day);
        ledger.Upsert(Response("old", SourceKind.Codex, ChunkStart - 10_000, ChunkStart - 7000, 300));
        Assert.Equal(100, ledger.MeasuredPeakOutput(Codex), 9);

        ledger.Upsert(Response("across", SourceKind.Codex, ChunkStart - 1000, ChunkStart + 3000, 4000));

        Assert.Equal(1000, ledger.MeasuredPeakOutput(Codex), 9);
    }

    [Fact]
    public void WindowsThatStartInThePreviousChunkSeeItsLastSeconds()
    {
        var ledger = new TodayLedger(Day);
        ledger.Upsert(Response("next", SourceKind.ClaudeCode, ChunkStart, ChunkStart + 1000, 300));
        Assert.Equal(100, ledger.MeasuredPeakOutput(Both), 9);

        ledger.Upsert(Response("before", SourceKind.Codex, ChunkStart - 2000, ChunkStart, 600));

        Assert.Equal(300, ledger.MeasuredPeakOutput(Both), 9);
        Assert.Equal(200, ledger.MeasuredPeakOutput(Codex), 9);
        Assert.Equal(ledger.Peak().Output, ledger.MeasuredPeakOutput(Both), 9);
    }

    [Fact]
    public void RandomUpsertsAndReplacementsMatchABruteForcePeakForEverySet()
    {
        var random = new Random(6_8);
        var ledger = new TodayLedger(Day);
        var latest = new Dictionary<string, ParsedResponse>();
        SourceKind[] kinds = [SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Gemini];
        long[] anchors = [Day, ChunkStart, ChunkStart + SecondSeries.ChunkSeconds * 1000L, B + 7_200_000];
        var sets = Enumerable.Range(0, 1 << kinds.Length)
            .Select(mask => SourceSet.From(kinds.Where((_, i) => (mask & (1 << i)) != 0)))
            .ToList();

        for (var step = 0; step < 900; step++)
        {
            var key = "r" + random.Next(40);
            var kind = latest.TryGetValue(key, out var previous) && random.Next(4) != 0
                ? previous.Source
                : kinds[random.Next(kinds.Length)];
            var start = anchors[random.Next(anchors.Length)] + random.Next(-15_000, 15_000);
            var duration = random.Next(25) switch
            {
                0 => 1_500_000,
                1 or 2 => 60_000 + random.Next(840_000),
                _ => 100 + random.Next(6000),
            };
            var response = Response(key, kind, start, start + duration, random.Next(5000), final: random.Next(10) != 0);
            ledger.Upsert(response);
            latest[key] = response;

            if (step % 3 != 0)
            {
                continue;
            }

            foreach (var set in sets)
            {
                var expected = ReferencePeak(latest.Values.Where(r => set.Contains(r.Source)), Day);
                EngineCaseTests.AssertClose(expected, ledger.MeasuredPeakOutput(set), $"step {step} {set}");
            }

            EngineCaseTests.AssertClose(ReferencePeak(latest.Values, Day), ledger.Peak().Output, $"step {step} all");
        }
    }

    [Fact]
    public void AskingForSetsLeavesEveryAllSourcesValueUnchanged()
    {
        var plain = EngineWithInFlightCodex();
        var asked = EngineWithInFlightCodex();
        SourceSet[] sets = [Codex, Both, SourceSet.Of(SourceKind.Gemini), SourceSet.Everything];
        for (var step = 0; step < 12; step++)
        {
            var now = B + step * 500L;
            if (step == 4)
            {
                var late = Response("r3", SourceKind.Codex, now - 1500, now, 900, agent: "codex");
                plain.Upsert(late, isCompletion: true, seeded: false);
                asked.Upsert(late, isCompletion: true, seeded: false);
            }

            var a = plain.Compute(now, Day, observeLive: true);
            foreach (var set in sets)
            {
                asked.MeasuredPeakOutput(set);
            }

            var b = asked.Compute(now, Day, observeLive: true);
            foreach (var set in sets)
            {
                asked.MeasuredPeakOutput(set);
            }

            Assert.Equal(a.Live, b.Live);
            Assert.Equal(a.EstimatedShare, b.EstimatedShare);
            Assert.Equal(a.SourceLive, b.SourceLive);
            Assert.Equal(a.SourceLiveEstimated, b.SourceLiveEstimated);
            Assert.Equal(a.PeakToday, b.PeakToday);
            Assert.Equal(a.PeakTodayAtMs, b.PeakTodayAtMs);
            Assert.Equal(a.AverageToday, b.AverageToday);
            Assert.Equal(a.Today, b.Today);
            Assert.Equal(a.SourceToday, b.SourceToday);
            Assert.Equal(a.History, b.History);
            Assert.Equal(a.Agents, b.Agents);
            Assert.Equal(a.Events, b.Events);
            Assert.Equal(a.LastResponse, b.LastResponse);
        }
    }

    [Fact]
    public void TheEstimatedPartOfEachSourcesLiveRateIsReported()
    {
        var engine = EngineWithInFlightCodex();

        var frame = engine.Compute(B, Day, observeLive: false);

        Assert.Equal(100, frame.SourceLiveEstimated[(int)SourceKind.Codex].Output, 9);
        Assert.Equal(frame.SourceLive[(int)SourceKind.Codex], frame.SourceLiveEstimated[(int)SourceKind.Codex]);
        Assert.Equal(Rate.Zero, frame.SourceLiveEstimated[(int)SourceKind.ClaudeCode]);
        Assert.Equal(100, frame.SourceLive[(int)SourceKind.ClaudeCode].Output, 9);
        Assert.Equal(0.5, frame.EstimatedShare, 12);
        Assert.Equal(100, engine.MeasuredPeakOutput(Both), 4);
    }

    /// <summary>Codex generating with a 100 tok/s reference; Claude Code measured at 100 tok/s in the window.</summary>
    internal static SpeedEngine EngineWithInFlightCodex()
    {
        var engine = new SpeedEngine(Day);
        engine.UpdateAgent(new AgentInfo("codex", SourceKind.Codex, "p", "m-codex", false, AgentPhase.Generating, B - 4000, B - 100));
        engine.UpdateAgent(Agent("claude", SourceKind.ClaudeCode) with { Model = "m-claude" });
        engine.Upsert(Response("r1", SourceKind.Codex, B - 10_000, B - 4000, 600, agent: "codex", model: "m-codex"),
            isCompletion: true, seeded: true);
        engine.Upsert(Response("r2", SourceKind.ClaudeCode, B - 3000, B, 300, agent: "claude", model: "m-claude"),
            isCompletion: true, seeded: true);
        return engine;
    }

    /// <summary>The reference implementation's measured peak (spec/tools/reference.py <c>today_stats</c>).</summary>
    internal static double ReferencePeak(IEnumerable<ParsedResponse> responses, long todayStartMs)
    {
        var buckets = new Dictionary<long, double>();
        foreach (var response in responses)
        {
            if (response.EndMs < todayStartMs || !response.Final || !SpeedMath.TryGetSpeedInterval(response, out var interval))
            {
                continue;
            }

            var (start, end) = (interval.StartMs, interval.EndMs);
            for (var k = SpeedMath.FloorDiv(start, 1000) * 1000; k < end; k += 1000)
            {
                if (k >= todayStartMs)
                {
                    buckets[k] = buckets.GetValueOrDefault(k) + response.Tokens.Output * (double)SpeedMath.Overlap(k, k + 1000, start, end) / (end - start);
                }
            }
        }

        var peak = 0.0;
        foreach (var k in buckets.Keys)
        {
            peak = Math.Max(peak, (buckets[k] + buckets.GetValueOrDefault(k - 1000) + buckets.GetValueOrDefault(k - 2000)) / 3);
        }

        return peak;
    }

    private static AgentInfo Agent(string id, SourceKind source) =>
        new(id, source, "p", "m1", false, AgentPhase.Idle, null, B);

    private static ParsedResponse Response(
        string key, SourceKind source, long start, long end, long output, bool final = true, string agent = "a1", string model = "m1") =>
        new(key, source, agent, model, start, end, new TokenCounts(Output: output), final);
}
