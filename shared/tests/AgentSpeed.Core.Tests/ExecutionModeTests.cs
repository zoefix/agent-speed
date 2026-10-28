using System.Text;
using System.Text.Json;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Engine;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Omp;
using AgentSpeed.Core.OpenCode;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.ZCode;

namespace AgentSpeed.Core.Tests;

public sealed class ExecutionModeTests
{
    public static IEnumerable<object[]> NormalizationCases() => Cases("normalization");
    public static IEnumerable<object[]> AttributionCases() => Cases("attribution");
    private static IEnumerable<object[]> Cases(string group)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("execution-mode-cases.json")));
        return document.RootElement.GetProperty(group).EnumerateArray().Select(c => new object[] { c.GetProperty("id").GetString()!, c.Clone() }).ToArray();
    }

    [Theory]
    [MemberData(nameof(NormalizationCases))]
    public void SharedNormalization(string id, JsonElement data)
    {
        var raw = String(data, "rawValue");
        var modes = String(data, "adapter") switch
        {
            "claudeSpeed" => ExecutionModeNative.ClaudeSpeed(raw),
            "kimiModel" => ExecutionModeNative.KimiModel(raw),
            "routing" => ExecutionModeNative.Routing(raw),
            "workflow" => ExecutionModeNative.Workflow(raw),
            _ => ExecutionModeNative.OpenAiTier(raw, Enum.Parse<ExecutionModeEvidence>(String(data, "evidence")!, true),
                Enum.Parse<ExecutionModeScope>(String(data, "scope")!, true), String(data, "provider")),
        };
        var expected = data.GetProperty("expected");
        if (expected.ValueKind == JsonValueKind.Null) Assert.Empty(modes.Facts);
        else Assert.Equal(Fact(expected), Assert.Single(modes.Facts));
        Assert.False(string.IsNullOrEmpty(id));
    }

    [Theory]
    [MemberData(nameof(AttributionCases))]
    public void SharedAttribution(string id, JsonElement data)
    {
        var modes = new ExecutionModes(data.GetProperty("facts").EnumerateArray().Select(Fact));
        var value = modes.Attribute(Enum.Parse<ExecutionModeKind>(String(data, "kind")!, true));
        var expected = data.GetProperty("expected");
        Assert.Equal((String(expected, "mode"), String(expected, "evidence"), String(expected, "provider")),
            (value.Mode, value.Evidence, value.Provider));
        Assert.False(string.IsNullOrEmpty(id));
    }

    [Fact]
    public void UnresolvedNativeValuesNeverProduceKnownEvidenceInTheUnknownBucket()
    {
        foreach (var raw in new[] { "unknown", "none", "auto", "inherit", "inherited" })
        foreach (var evidence in Enum.GetValues<ExecutionModeEvidence>())
        {
            var mode = ExecutionModeNative.OpenAiTier(raw, evidence).Attribute(ExecutionModeKind.ServiceTier);
            Assert.Equal(("unknown", "unknown", (string?)null), (mode.Mode, mode.Evidence, mode.Provider));
        }
    }

    [Fact]
    public void FactLimitRetainsStrongEvidenceAndCannotExposeAMutableArray()
    {
        var facts = Enumerable.Range(0, 40).Select(i => new ExecutionModeFact(ExecutionModeKind.ServiceTier, "FAST", "priority", ExecutionModeEvidence.Configured, "p" + i));
        var modes = new ExecutionModes(facts.Concat(ExecutionModeNative.ClaudeSpeed("standard").Facts));
        Assert.Equal(20, modes.Facts.Count);
        Assert.Contains(modes.Facts, f => f.Evidence == ExecutionModeEvidence.Served);
        Assert.DoesNotContain(modes.Facts, f => f.Value == "FAST");
        Assert.False(modes.Facts is ExecutionModeFact[]);
    }

    [Theory]
    [InlineData("openai", true)]
    [InlineData("azure", true)]
    [InlineData("azure-openai", false)]
    [InlineData("openai-codex", false)]
    [InlineData("anthropic", false)]
    public void OpenCodeNativeResponseUsesTheVerifiedProviderIds(string provider, bool supported)
    {
        var json = """{"time":{"created":1000,"streamed":2000,"completed":2100},"model":{"id":"gpt-5","providerID":"PROVIDER"},"native":{"serviceTier":"priority"},"tokens":{"input":1,"output":20}}""".Replace("PROVIDER", provider, StringComparison.Ordinal);
        var data = OpenCodeDataReader.Read(Encoding.UTF8.GetBytes(json));
        var row = new OpenCodeRow("r", "s", OpenCodeRowKind.Assistant, 1, 1000, 2100, data);
        var response = Assert.IsType<ParsedResponse>(OpenCodeRules.Response(row, new OpenCodeSessionInfo("s", null, null, 0, 2100)));
        Assert.Equal(provider, response.Provider);
        if (supported)
        {
            var fact = Assert.Single(response.Modes.Facts);
            Assert.Equal(("fast", "priority", "openai", ExecutionModeEvidence.Served), (fact.Value, fact.RawValue, fact.Provider, fact.Evidence));
        }
        else Assert.Empty(response.Modes.Facts);
    }

    [Fact]
    public void TodayReplacesLocalModeEvidenceWithoutRecountingAndOldSnapshotsStayImmutable()
    {
        var ledger = new TodayLedger(0);
        var response = new ParsedResponse("r", SourceKind.ClaudeCode, "a", "m", 1000, 3000, new TokenCounts(Output: 40));
        ledger.Upsert(response);
        var before = ledger.Modes;
        ledger.Upsert(response with { Modes = ExecutionModeNative.ClaudeSpeed("fast") });
        var tier = Assert.Single(ledger.Modes, m => m.Kind == ExecutionModeKind.ServiceTier);
        Assert.Equal(("fast", "served", 1L, 40L, 40L, 2000L), (tier.Mode, tier.Evidence, tier.Requests, tier.Output, tier.MeasuredOutput, tier.MeasuredMs));
        Assert.All(before, m => Assert.Equal("unknown", m.Mode));
        Assert.Equal((1L, 40L, 20.0), (ledger.Totals.Requests, ledger.Totals.Output, ledger.AverageOutputSpeed));
        ledger.Reset(4000);
        Assert.Empty(ledger.Modes);
    }

    [Fact]
    public void ClaudeLateEvidenceUpdatesOneResponseAndNextInputClearsItsMode()
    {
        var parser = new ClaudeParser("/fixture/a.jsonl", new Deduper());
        var sink = new RecordingSink();
        Feed.Lines(parser, sink, ClaudeLines.UserText(ClaudeLines.T0, "go"), ClaudeLines.Assistant(ClaudeLines.T0 + 2000, "r", 30));
        var before = Assert.Single(sink.Latest);
        Assert.Equal("unknown", before.Modes.Attribute(ExecutionModeKind.ServiceTier).Mode);
        Feed.Lines(parser, sink, ClaudeLines.Assistant(ClaudeLines.T0 + 2000, "r", 30).Replace("\"usage\":{", "\"usage\":{\"speed\":\"standard\",", StringComparison.Ordinal));
        var after = Assert.Single(sink.Latest);
        Assert.Equal((before.Key, before.Requests, before.StartMs, before.EndMs, before.Tokens),
            (after.Key, after.Requests, after.StartMs, after.EndMs, after.Tokens));
        Assert.Equal("standard", after.Modes.Attribute(ExecutionModeKind.ServiceTier).Mode);
        Assert.Equal("served", after.Modes.Attribute(ExecutionModeKind.ServiceTier).Evidence);
        Feed.Lines(parser, sink, ClaudeLines.UserText(ClaudeLines.T0 + 3000, "next"));
        Assert.Empty(parser.Modes.Facts);
    }

    [Fact]
    public void OmpBranchSelectionAndRecordedEchoRemainDistinct()
    {
        var parser = new OmpParser("/fixture/session.jsonl", new Deduper(), "/fixture");
        var sink = new RecordingSink();
        Feed.Lines(parser, sink,
            """{"type":"session","id":"session","timestamp":"2026-10-19T23:59:00Z","cwd":"/fixture"}""",
            """{"type":"service_tier_change","id":"a","timestamp":"2026-10-20T00:00:00Z","serviceTier":{"openai":"priority"}}""",
            """{"type":"message","id":"b","parentId":"a","timestamp":"2026-10-20T00:00:01Z","message":{"role":"assistant","id":"r1","provider":"openai","api":"openai-responses","model":"gpt","serviceTier":"default","usage":{"input":1,"output":20},"stopReason":"stop"}}""");
        var modes = Assert.Single(sink.Latest).Modes;
        Assert.Contains(modes.Facts, f => f.Value == "fast" && f.Evidence == ExecutionModeEvidence.Selected);
        Assert.Contains(modes.Facts, f => f.Value == "standard" && f.Evidence == ExecutionModeEvidence.Recorded);
        Assert.DoesNotContain(modes.Facts, f => f.Evidence == ExecutionModeEvidence.Served);
        Assert.Equal("standard", modes.Attribute(ExecutionModeKind.ServiceTier).Mode);
        Feed.Lines(parser, sink,
            """{"type":"service_tier_change","id":"clear","parentId":"a","timestamp":"2026-10-20T00:00:02Z","serviceTier":null}""",
            """{"type":"message","id":"c","parentId":"clear","timestamp":"2026-10-20T00:00:03Z","message":{"role":"assistant","id":"r2","provider":"openai","api":"openai-responses","model":"gpt","usage":{"input":1,"output":10},"stopReason":"stop"}}""");
        Assert.Empty(sink.Latest[1].Modes.Facts);
        Feed.Lines(parser, sink,
            """{"type":"message","id":"d","parentId":"a","timestamp":"2026-10-20T00:00:04Z","message":{"role":"assistant","id":"r3","provider":"openai","api":"openai-responses","model":"gpt","usage":{"input":1,"output":10},"stopReason":"stop"}}""");
        Assert.Equal("fast", sink.Latest[2].Modes.Attribute(ExecutionModeKind.ServiceTier).Mode);
        Feed.Lines(parser, sink, """{"type":"model_usage","id":"side","parentId":"a","timestamp":"2026-10-20T00:00:05Z","provider":"openai","api":"openai-responses","model":"gpt","serviceTier":"priority","usage":{"input":1,"output":20}}""");
        Assert.Contains(sink.Latest[^1].Modes.Facts, f => f.Value == "fast" && f.Evidence == ExecutionModeEvidence.Recorded);
    }

    [Fact]
    public void ZCodeRetriesClassifyEachAttemptAndMockIsNeverRecordedAsAccelerated()
    {
        var metadata = """{"metadata":{"highspeed":{"schemaVersion":1,"provider":"account:zai","model":"glm-5","cardId":"hsc_real"}}}""";
        var accelerated = ZCodeModes.Read(Encoding.UTF8.GetBytes(metadata), "account:zai-highspeed-card", "glm-5");
        var fallback = ZCodeModes.Read(Encoding.UTF8.GetBytes(metadata), "account:zai", "glm-5");
        Assert.Equal(("highspeed", "recorded"), (accelerated.Attribute(ExecutionModeKind.Accelerator).Mode, accelerated.Attribute(ExecutionModeKind.Accelerator).Evidence));
        Assert.Equal("standard", fallback.Attribute(ExecutionModeKind.Accelerator).Mode);
        Assert.DoesNotContain(accelerated.Facts, f => f.Evidence == ExecutionModeEvidence.Served);
        var mock = ZCodeModes.Read(Encoding.UTF8.GetBytes(metadata.Replace("hsc_real", "hsc_mock_1", StringComparison.Ordinal)), "account:zai-highspeed-card", "glm-5");
        Assert.All(mock.Facts, f => Assert.Equal(ExecutionModeEvidence.Selected, f.Evidence));
        Assert.Empty(ZCodeModes.Read("{\"metadata\":{\"highspeed\":{\"schemaVersion\":\"1\"}}}"u8, null, null).Facts);
        Assert.Empty(ZCodeModes.Read("[]"u8, null, null).Facts);
    }

    [Fact]
    public void CodexReadsOnlyExplicitRootDefaultsAndRefreshesChangedConfiguration()
    {
        using var temp = new TempDirectory();
        var config = temp.WriteFile("config.toml", "service_tier = 'priority' # selected default\n[profiles.other]\nservice_tier = 'ultrafast'\n");
        Directory.CreateDirectory(temp.Combine("sessions"));
        var modes = CodexConfiguredModes.Read(temp.Combine("sessions") + Path.DirectorySeparatorChar);
        var fact = Assert.Single(modes.Facts);
        Assert.Equal(("fast", ExecutionModeEvidence.Configured, ExecutionModeScope.Configuration), (fact.Value, fact.Evidence, fact.Scope));
        Assert.Equal("unknown", modes.Attribute(ExecutionModeKind.ServiceTier).Mode);
        File.WriteAllText(config, "[profiles.other]\nservice_tier = 'ultrafast'\n");
        Assert.Empty(CodexConfiguredModes.Read(temp.Path).Facts);
        File.WriteAllText(config, "service_tier = 'fast\"\n");
        Assert.Empty(CodexConfiguredModes.Read(temp.Path).Facts);
    }

    [Fact]
    public void StaleModeCacheFromAnOlderWriterFallsBackToUnknownWithoutChangingModelTotals()
    {
        var aggregate = new DayAggregate { Requests = 1, Output = 20 };
        aggregate.Models["m"] = new(1, 20, 0, 0);
        aggregate.Modes[new("m", ExecutionModeKind.ServiceTier, "fast", "served", "anthropic")] = new(1, 30, 30, 2000);
        var day = aggregate.ToUsage(new("2026-10-24", SourceKind.ClaudeCode), null);
        Assert.Equal((1L, 20L, 0L, 0L), (day.Totals.Requests, day.Totals.Output, day.MeasuredOutput, day.MeasuredMs));
        Assert.All(day.Modes, row => Assert.Equal(("unknown", "unknown", 1L, 20L, 0L, 0L),
            (row.Mode, row.Evidence, row.Requests, row.Output, row.MeasuredOutput, row.MeasuredMs)));
    }

    [Fact]
    public void LegacyHistoryWithoutAnyModelBreakdownStillConservesUnknownModes()
    {
        var aggregate = new DayAggregate { Requests = 3, Output = 90, MeasuredOutput = 60, MeasuredMs = 3000 };
        var day = aggregate.ToUsage(new("2026-10-24", SourceKind.Codex), null);
        Assert.Empty(day.Models);
        Assert.Equal(5, day.Modes.Count);
        Assert.All(day.Modes, row => Assert.Equal((string.Empty, "unknown", "unknown", 3L, 90L, 60L, 3000L),
            (row.Model, row.Mode, row.Evidence, row.Requests, row.Output, row.MeasuredOutput, row.MeasuredMs)));
    }

    [Theory]
    [InlineData("instructions = \"\"\"\nservice_tier = \"priority\"\n\"\"\"\n")]
    [InlineData("instructions = '''\nservice_tier = \"priority\"\n'''\n")]
    [InlineData("service_tier = \"priority\"\ninstructions = \"\"\"unsafe\n")]
    [InlineData("service_tier = [\"priority\"]\n")]
    [InlineData("service_tier = \"priority\"\nservice_tier = \"default\"\n")]
    public void CodexNeverTreatsMultilineStringContentOrNonliteralValuesAsDefaults(string text)
    {
        using var temp = new TempDirectory();
        temp.WriteFile("config.toml", text);
        Assert.Empty(CodexConfiguredModes.Read(temp.Path).Facts);
    }

    [Fact]
    public void HistoryAddsModeTableWithoutBumpingSchemaAndRetainsMissingFileTotals()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("claude/projects/p/a.jsonl", ClaudeLines.UserText(ClaudeLines.T0, "go") + "\n"
            + ClaudeLines.Assistant(ClaudeLines.T0 + 2000, "r", 30).Replace("\"usage\":{", "\"usage\":{\"speed\":\"fast\",", StringComparison.Ordinal) + "\n");
        var locations = new SourceLocations("", temp.Combine("claude", "projects"), "", "", []);
        var db = temp.Combine("history.sqlite");
        using (var store = HistoryStore.Open(db))
        {
            var analyzer = new HistoryAnalyzer(store);
            analyzer.Run(locations, new HashSet<SourceKind> { SourceKind.ClaudeCode }, TimeZoneInfo.Utc, CancellationToken.None);
            var day = Assert.Single(analyzer.Days());
            Assert.Equal("fast", Assert.Single(day.Modes, m => m.Kind == ExecutionModeKind.ServiceTier).Mode);
            Assert.All(day.Modes.GroupBy(m => m.Kind), g => Assert.Equal((day.Totals.Requests, day.Totals.Output, day.MeasuredOutput, day.MeasuredMs),
                (g.Sum(m => m.Requests), g.Sum(m => m.Output), g.Sum(m => m.MeasuredOutput), g.Sum(m => m.MeasuredMs))));
        }
        using (var read = SqliteConnection.Open(db, SqliteOpenMode.ReadWriteCreate))
        {
            Assert.Equal(3, read.ScalarInt64("PRAGMA user_version"));
            read.Execute("DROP TABLE contribution_modes; UPDATE files SET version=3;");
        }
        File.Delete(path);
        using var reopened = HistoryStore.Open(db);
        var oldDay = Assert.Single(new HistoryAnalyzer(reopened).Days());
        Assert.Equal(30, oldDay.Totals.Output);
        Assert.Equal(5, oldDay.Modes.Count);
        Assert.All(oldDay.Modes, m => Assert.Equal(("unknown", "unknown", 1L, 30L, 2000L), (m.Mode, m.Evidence, m.Requests, m.Output, m.MeasuredMs)));
    }

    private static ExecutionModeFact Fact(JsonElement item) => new(Enum.Parse<ExecutionModeKind>(String(item, "kind")!, true),
        String(item, "value")!, String(item, "rawValue")!, Enum.Parse<ExecutionModeEvidence>(String(item, "evidence")!, true),
        String(item, "provider"), Enum.Parse<ExecutionModeScope>(String(item, "scope")!, true));
    private static string? String(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
