using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests.Reporting;

/// <summary>METRICS 11.4: what the report ledger counts, growth of counted responses, attribution and exactly-once windows.</summary>
public sealed class ReportLedgerTests
{
    private const long Now = 1_800_000_000_000;
    private const long Today = Now - 3_600_000;

    private static ParsedResponse Record(string key, long? start, long end, long output, SourceKind source = SourceKind.ClaudeCode,
        string? model = "claude-opus-5-5", long input = 1000, long cached = 800, bool final = true, string? provider = null) =>
        new(key, source, "/agents/" + source, model, start, end, new TokenCounts(input, cached, 0, output, 0), final, provider);

    private static SpeedEngine Engine() => new(Today, new PriceBook());

    private static ReportLiveFacts Facts(SpeedEngine engine, long now) => engine.Compute(now, Today, observeLive: true).Report;

    [Fact]
    public void CountsOnlyLiveRecentFirstEmissions()
    {
        var engine = Engine();
        engine.Upsert(Record("seeded", Now - 9000, Now - 5000, 300), isCompletion: true, seeded: true, liveNowMs: Now);
        engine.Upsert(Record("no-clock", Now - 9000, Now - 5000, 300), isCompletion: true, seeded: false);
        engine.Upsert(Record("old", Now - 200_000, Now - 121_000, 50), isCompletion: true, seeded: false, liveNowMs: Now);
        engine.Upsert(Record("live", Now - 4000, Now - 1000, 120), isCompletion: true, seeded: false, liveNowMs: Now);
        engine.Upsert(Record("seeded", Now - 9000, Now - 500, 900), isCompletion: false, seeded: false, liveNowMs: Now);
        engine.Upsert(Record("old", Now - 200_000, Now - 500, 900), isCompletion: false, seeded: false, liveNowMs: Now);
        var total = Facts(engine, Now).Counters.Total;
        Assert.Equal(1, total.Requests);
        Assert.Equal(120, total.Output);
        Assert.Equal(1000, total.Input);
        Assert.Equal(800, total.CachedInput);
        Assert.Equal(120, total.MeasuredOutput);
        Assert.Equal(3000, total.MeasuredMs);
        Assert.True(total.CostMicroUsd > 0);
        Assert.Equal(0, total.UnpricedRequests);
    }

    /// <summary>A streaming Claude message adds its growth where it is seen; becoming measured adds output and time then.</summary>
    [Fact]
    public void GrowthIsCountedOnceWhereSeen()
    {
        var engine = Engine();
        engine.Upsert(Record("m", Now - 3000, Now - 2000, 8, final: false), isCompletion: true, seeded: false, liveNowMs: Now);
        var first = Facts(engine, Now).Counters;
        Assert.Equal(8, first.Total.Output);
        Assert.Equal(0, first.Total.MeasuredOutput);
        engine.Upsert(Record("m", Now - 3000, Now - 500, 400, final: false), isCompletion: false, seeded: false, liveNowMs: Now + 500);
        engine.Upsert(Record("m", Now - 3000, Now, 420), isCompletion: false, seeded: false, liveNowMs: Now + 500);
        var second = Facts(engine, Now + 500).Counters;
        var window = second.Since(first).Total;
        Assert.Equal(0, window.Requests);
        Assert.Equal(412, window.Output);
        Assert.Equal(420, window.MeasuredOutput);
        Assert.Equal(3000, window.MeasuredMs);
        engine.Upsert(Record("m", Now - 3000, Now, 300), isCompletion: false, seeded: false, liveNowMs: Now + 1000);
        var third = Facts(engine, Now + 1000).Counters;
        Assert.True(third.Since(second).Total.IsZero, "shrinking usage adds nothing");
        Assert.Same(second, third);
    }

    [Fact]
    public void ModelIdsAreResolvedNeverRaw()
    {
        var engine = Engine();
        engine.Upsert(Record("a", Now - 3000, Now - 1000, 10, model: "Claude-Opus-5-5[1m]"), true, false, Now);
        engine.Upsert(Record("b", Now - 3000, Now - 1000, 20, SourceKind.OpenCode, "qwen3:32b", provider: "ollama"), true, false, Now);
        engine.Upsert(Record("c", Now - 3000, Now - 1000, 30, SourceKind.Omp, "secret-internal-model"), true, false, Now);
        engine.Upsert(Record("d", Now - 3000, Now - 1000, 5, SourceKind.Omp, "deepseek-v4.1-flash-free"), true, false, Now);
        engine.Upsert(Record("e", Now - 3000, Now - 1000, 5, SourceKind.Omp, null), true, false, Now);
        var counters = Facts(engine, Now).Counters;
        Assert.Equal(new HashSet<string> { "claude-opus-5-5", "local", "unknown" }, counters.ByModel.Keys.Select(k => k.ModelId).ToHashSet());
        var unknown = counters.ByModel[new ReportModelKey("unknown", SourceKind.Omp)];
        Assert.Equal(3, unknown.Requests);
        Assert.Equal(2, unknown.UnpricedRequests);
        Assert.All(counters.ByModel.Keys, k => Assert.True(ReportModelIds.IsValid(k.ModelId)));

        var table = PriceBook.Bundled;
        Assert.Equal("claude-opus-5-5", table.ReportModelId("claude-opus-5-5", null));
        Assert.Equal("local", table.ReportModelId("llama3", "ollama"));
        Assert.Equal("unknown", table.ReportModelId(null, null));
        Assert.Equal("unknown", table.ReportModelId("my-private-model-x", null));
    }

    [Fact]
    public void LatePriceAddsCostWithoutDecreasingUnpriced()
    {
        var ledger = new ReportLedger();
        var unpriced = Record("r", Now - 3000, Now - 1000, 100, model: "gpt-5.5");
        ledger.Count(unpriced, "gpt-5.5");
        Assert.Equal(1, ledger.Counters.Total.UnpricedRequests);
        Assert.Equal(0, ledger.Counters.Total.CostMicroUsd);
        ledger.Grow(unpriced with { CostMicroUsd = 4200 }, "gpt-5.5");
        var total = ledger.Counters.Total;
        Assert.Equal(1, total.UnpricedRequests);
        Assert.Equal(4200, total.CostMicroUsd);
        Assert.Equal(1, total.Requests);
        ledger.Grow(unpriced with { CostMicroUsd = 4200 }, "gpt-5.5");
        Assert.Equal(4200, ledger.Counters.Total.CostMicroUsd);
        ledger.Count(unpriced, "gpt-5.5");
        Assert.Equal(1, ledger.Counters.Total.Requests);

        ledger.Grow(Record("never-counted", Now - 3000, Now, 50), "gpt-5.5");
        Assert.Equal(100, ledger.Counters.Total.Output);
        ledger.Prune(Now);
        Assert.False(ledger.IsCounted("r"));
        Assert.Equal(0, ledger.CountedKeys);
    }

    [Fact]
    public void CountedKeysAreBounded()
    {
        var ledger = new ReportLedger();
        for (var i = 0; i < ReportLedger.MaxCountedKeys + 10; i++)
        {
            ledger.Count(Record("k" + i, Now - 3000, Now - 1000, 1), "gpt-5.5");
        }

        Assert.Equal(ReportLedger.MaxCountedKeys, ledger.CountedKeys);
        Assert.False(ledger.IsCounted("k0"));
        Assert.True(ledger.IsCounted("k" + (ReportLedger.MaxCountedKeys + 9)));
    }

    [Fact]
    public void AgentCountsUseTheBaseSet()
    {
        var engine = Engine();
        engine.UpdateAgent(new AgentInfo("g", SourceKind.Codex, "p", "gpt-5.5", false, AgentPhase.Generating, Now - 1000, Now - 1000));
        engine.UpdateAgent(new AgentInfo("t", SourceKind.Codex, "p", null, false, AgentPhase.Tools, null, Now - 2000));
        engine.UpdateAgent(new AgentInfo("stale", SourceKind.Codex, "p", null, false, AgentPhase.Tools, null, Now - 400_000));
        engine.UpdateAgent(new AgentInfo("gone", SourceKind.Gemini, "p", null, false, AgentPhase.Idle, null, Now - 700_000));
        engine.UpdateAgent(new AgentInfo("child", SourceKind.Codex, "p", null, true, AgentPhase.Idle, null, Now - 3000, "gone"));
        var facts = Facts(engine, Now);
        Assert.Equal(new ReportAgentCounts(1, 1, 4), facts.Agents[SourceKind.Codex]);
        Assert.False(facts.Agents.ContainsKey(SourceKind.Gemini), "an ancestor outside the base set is not counted");
        Assert.Equal(1, Assert.Single(facts.GeneratingModels).Value);
        Assert.Equal(new ReportModelKey("gpt-5.5", SourceKind.Codex), facts.GeneratingModels.Single().Key);
    }

    /// <summary>Σ deltas of built windows + Σ discarded = Σ increments; windows never share an increment.</summary>
    [Fact]
    public void WindowsAreExactlyOnce()
    {
        var engine = Engine();
        var window = new ReportWindow(ReportSample.Empty with { Facts = Facts(engine, Now) });
        var built = ReportUsage.Zero;
        var discarded = ReportUsage.Zero;
        for (var step = 1; step <= 40; step++)
        {
            var t = Now + step * 500L;
            engine.Upsert(Record("r" + step, t - 2000, t, step), true, false, t);
            var sample = ReportSample.Empty with { Facts = Facts(engine, t) };
            if (step % 7 == 0)
            {
                discarded += window.Content(sample).Usage.Total;
                window.Discard(sample, keepingTick: false);
            }
            else if (step % 3 == 0)
            {
                built += window.Take(sample).Usage.Total;
            }

            window.Observe(sample);
        }

        var final = ReportSample.Empty with { Facts = Facts(engine, Now + 30_000) };
        built += window.Take(final).Usage.Total;
        Assert.Equal(Enumerable.Range(1, 40).Sum(), built.Output + discarded.Output);
        Assert.Equal(40, built.Requests + discarded.Requests);
    }

    [Fact]
    public void GapKeepsTheTicksOwnIncrements()
    {
        var key = new ReportModelKey("gpt-5.5", SourceKind.Codex);
        var counters = new ReportCounters();
        counters.Add(new ReportUsage(1, 10), key);
        var window = new ReportWindow(Sample(counters.Copy()));
        counters.Add(new ReportUsage(1, 5), key);
        window.Observe(Sample(counters.Copy()));
        counters.Add(new ReportUsage(1, 7), key);
        var afterSleep = Sample(counters.Copy());
        Assert.True(window.HasTokenIncrement(afterSleep));
        window.Discard(afterSleep, keepingTick: true);
        Assert.Equal(7, window.Take(afterSleep).Usage.Total.Output);
        Assert.False(window.HasTokenIncrement(afterSleep));

        static ReportSample Sample(ReportCounters counters) => ReportSample.Empty with { Facts = ReportLiveFacts.Empty with { Counters = counters, Epoch = 5 } };
    }

    /// <summary>A new monitor session (a new engine) counts from zero: a window never subtracts another engine's counters.</summary>
    [Fact]
    public void ANewEngineEpochCountsFromZero()
    {
        var first = Engine();
        var second = Engine();
        Assert.NotEqual(first.ReportEpoch, second.ReportEpoch);
        first.Upsert(Record("a", Now - 3000, Now - 1000, 500), true, false, Now);
        var window = new ReportWindow(ReportSample.Empty with { Facts = Facts(first, Now) });
        first.Upsert(Record("b", Now - 2000, Now - 500, 100), true, false, Now);
        window.Observe(ReportSample.Empty with { Facts = Facts(first, Now) });
        second.Upsert(Record("c", Now - 2000, Now, 30), true, false, Now);
        var restarted = ReportSample.Empty with { Facts = Facts(second, Now + 500) };
        Assert.True(window.HasTokenIncrement(restarted));
        Assert.Equal(30, window.Content(restarted).Usage.Total.Output);
        window.Discard(restarted, keepingTick: true);
        Assert.Equal(30, window.Take(restarted).Usage.Total.Output);
    }

    /// <summary>The monitor counts what it reads while running, by its clock, and never what seeding read.</summary>
    [Fact]
    public void MonitorCoreCountsLiveReadsOnly()
    {
        using var temp = new TempDirectory();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var today = now - 3_600_000;
        var claude = temp.Combine("claude", "projects");
        Directory.CreateDirectory(temp.Combine("codex", "sessions"));
        Directory.CreateDirectory(claude);
        var path = temp.WriteFile("claude/projects/p/a.jsonl",
            ClaudeLines.UserText(now - 9000, "hi") + "\n" + ClaudeLines.Assistant(now - 7000, "msg_seed", 300) + "\n");
        using var core = new Monitoring.MonitorCore(MonitorConfiguration.Default, temp.Combine("codex", "sessions"), claude,
            temp.Combine("dsh", "sessions"), today) { Clock = () => now };
        core.Seed(now, today, CancellationToken.None);
        Assert.True(core.BuildSnapshot(now, today, MonitorState.Running, observeLive: true).Snapshot.Report.Counters.Total.IsZero);

        File.AppendAllText(path, ClaudeLines.UserText(now - 4000, "again") + "\n" + ClaudeLines.Assistant(now - 1000, "msg_live", 80) + "\n");
        Assert.True(core.ProcessPaths([path], now, today));
        var report = core.BuildSnapshot(now, today, MonitorState.Running, observeLive: true).Snapshot.Report;
        Assert.Equal(1, report.Counters.Total.Requests);
        Assert.Equal(80, report.Counters.Total.Output);
        Assert.Equal(SourceKind.ClaudeCode, Assert.Single(report.Counters.ByModel).Key.Source);
        Assert.Equal(1, report.Agents[SourceKind.ClaudeCode].Active);
    }

    [Fact]
    public void UsageAndCountersArithmetic()
    {
        Assert.Equal(new ReportUsage(1, 2), new ReportUsage(1, 2) + ReportUsage.Zero);
        Assert.Equal(ReportUsage.Zero, new ReportUsage(1, 2) - new ReportUsage(3, 4));
        Assert.True(new ReportUsage(Requests: 1).HasTokens);
        Assert.False(new ReportUsage(CostMicroUsd: 5, MeasuredMs: 3).HasTokens);
        var counters = new ReportCounters();
        var version = counters.Version;
        counters.Add(ReportUsage.Zero, new ReportModelKey("x", SourceKind.Codex));
        Assert.Equal(version, counters.Version);
        Assert.Empty(counters.ByModel);
        counters.Add(new ReportUsage(Output: 3), new ReportModelKey("x", SourceKind.Codex));
        Assert.NotEqual(version, counters.Version);
        var copy = counters.Copy();
        counters.Add(new ReportUsage(Output: 3), new ReportModelKey("x", SourceKind.Codex));
        Assert.Equal(3, copy.Total.Output);
        Assert.Equal(3, counters.Since(copy).Total.Output);
    }

    [Fact]
    public void ReportModelIdPattern()
    {
        Assert.True(ReportModelIds.IsValid("gpt-5.5"));
        Assert.True(ReportModelIds.IsValid("qwen3:32b"));
        Assert.False(ReportModelIds.IsValid("GPT-5"));
        Assert.False(ReportModelIds.IsValid("-x"));
        Assert.False(ReportModelIds.IsValid(new string('a', 65)));
        Assert.False(ReportModelIds.IsValid(string.Empty));
    }

    [Fact]
    public void TitleTrackerFollowsDesign1()
    {
        var tracker = new ReportTitleTracker();
        tracker.Update(900, 2000, 0);
        Assert.Equal(0, tracker.Level);
        tracker.Update(900, 2000, 0.8);
        Assert.Equal(10, tracker.Level);
        tracker.Update(650, 2000, 1.0);
        tracker.Update(650, 2000, 2.0);
        Assert.Equal(10, tracker.Level);
        tracker.Update(600, 2000, 2.0);
        tracker.Update(600, 2000, 3.0);
        Assert.Equal(9, tracker.Level);
        tracker.Update(0, 2000, 4.0);
        tracker.Update(0, 2000, 7.0);
        tracker.Update(0, 2000, 8.0);
        Assert.Equal(0, tracker.Level);
        tracker.Update(5000, 2000, 9);
        tracker.Update(5000, 2000, 10);
        Assert.Equal(ReportTitleTracker.MaxLevel, tracker.Level);
        tracker.Update(double.NaN, 2000, 11);
        Assert.InRange(Math.Abs(ReportTitleTracker.Heat(412.6, 2000) - 0.388), 0, 0.0005);
        Assert.Equal(1, ReportTitleTracker.Heat(5000, 2000));
        Assert.Equal(0, ReportTitleTracker.Heat(-1, 2000));
        Assert.Equal(0.5, ReportTitleTracker.EnterSpeed(1, 2000));
        Assert.Equal(2000, ReportTitleTracker.EnterSpeed(13, 2000));
    }

    [Fact]
    public void PriceVersionOfADay()
    {
        var book = new PriceBook();
        Assert.Equal(book.CurrentVersion, book.ReportPriceVersion(Now, TimeZoneInfo.Utc));
        Assert.Null(book.BoundVersion(DateTimeOffset.FromUnixTimeMilliseconds(Now).UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
