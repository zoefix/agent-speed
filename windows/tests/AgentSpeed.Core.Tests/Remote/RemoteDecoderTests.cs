using System.Globalization;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Remote;
using AgentSpeed.Core.Work;
using L = AgentSpeed.Core.Tests.Remote.RelayLines;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>Relay lines (REMOTE 5.2, 5.4) and every validation rule of REMOTE 5.6.</summary>
public sealed class RemoteDecoderTests
{
    private const string HistoryHead = """{"t":"historyDay","day":"2026-11-03","source":"codex","digest":"0123456789abcdef","aggregate":""";

    private const string SmallAggregate =
        """{"requests":1,"input":1,"cachedInput":0,"cacheWriteInput":0,"output":1,"reasoning":0,"measuredOutput":0"""
        + ""","measuredMs":0,"unfinalized":0,"agents":1,"costMicroUsd":null,"unpricedRequests":null,"models":{},"unpriced":[],"modes":[]}""";

    private static string Prefix => L.Prefix;

    private static RelayMessage Message(string line) => L.Message(line);

    private static RemoteLineErrorKind? Invalid(string line) => L.Invalid(line)?.Kind;

    // Each message type

    [Fact]
    public void Hello()
    {
        Assert.Equal(new RelayMessage.Hello(new RelayHello(1, "1.7.0", "x86_64", L.BaseMs)), Message(L.Hello()));
        Assert.Equal(new RelayMessage.Hello(new RelayHello(2, "", "", 0)), Message("""{"t":"hello","protocol":2}"""));
        Assert.Equal(new RelayMessage.Hello(new RelayHello(2, "", "", 0)), Message("""{"t":"hello","protocol":2,"version":7,"now":99}"""));
        Assert.Equal("protocol", L.Invalid("""{"t":"hello","version":"1"}""")?.Field);
        Assert.Equal(RemoteLineErrorKind.Missing, Invalid("""{"t":"hello","protocol":1,"arch":"x86_64","now":1780000000000}"""));
    }

    [Fact]
    public void AgentDecodesEveryField()
    {
        var agent = Assert.IsType<RelayMessage.Agent>(Message(L.Agent())).Value;
        Assert.Equal(L.HostId, agent.HostId);
        Assert.Equal(Prefix + "/home/u/.codex/sessions/a.jsonl", agent.Id);
        Assert.Contains("//home", agent.Id, StringComparison.Ordinal);
        Assert.Equal("/home/u/.codex/sessions/a.jsonl", agent.RelayId);
        Assert.Equal(SourceKind.Codex, agent.Source);
        Assert.Equal("proj", agent.Project);
        Assert.Equal("/home/u/proj", agent.CwdPath);
        Assert.Equal("gpt-5.5", agent.Model);
        Assert.False(agent.IsSubagent);
        Assert.Equal(AgentPhase.Generating, agent.Phase);
        Assert.Equal(L.BaseMs, agent.InFlightStartMs);
        Assert.Equal(L.BaseMs + 500, agent.LastLineMs);
        var fact = agent.Modes.Facts[0];
        Assert.Equal((ExecutionModeKind.ServiceTier, "fast", "priority", "openai", ExecutionModeEvidence.Recorded, ExecutionModeScope.Request),
            (fact.Kind, fact.Value, fact.RawValue, fact.Provider, fact.Evidence, fact.Scope));
        Assert.True(agent.SessionModes.IsEmpty);
        Assert.True(agent.ConfiguredModes.IsEmpty);
        var action = Assert.Single(agent.Actions);
        Assert.Equal("shell", action.Tool);
        Assert.Equal(0x0123456789abcdefUL, action.Digest);
        Assert.Equal(L.BaseMs, action.AtMs);
        Assert.Equal(L.BaseMs + 400, action.ResultMs);
        Assert.False(action.Failed);
        Assert.Equal("gpt-5.5", action.Model);
        Assert.True(action.Seeded);

        var engine = RemoteIngest.EngineAgent(agent, Prefix + "/p.jsonl");
        Assert.Equal(agent.Id, engine.Id);
        Assert.Equal("proj", engine.Project);
        Assert.Equal(Prefix + "/p.jsonl", engine.ParentId);
        Assert.Equal(agent.Actions, engine.Work);
        Assert.Equal(agent.Modes, engine.Modes);
        Assert.Equal(L.HostId, engine.Host);
    }

    [Fact]
    public void AgentWithNullsAndProjectFallback()
    {
        var line = L.Agent(a =>
        {
            a["project"] = null;
            a["cwd"] = null;
            a["model"] = null;
            a["inFlightStartMs"] = null;
            a.Remove("lastLineMs");
            a["phase"] = "idle";
            a["actions"] = new List<object?> { L.Action(resultMs: null) };
        });
        var agent = Assert.IsType<RelayMessage.Agent>(Message(line)).Value;
        Assert.Null(agent.Project);
        Assert.Null(agent.CwdPath);
        Assert.Null(agent.Model);
        Assert.Null(agent.InFlightStartMs);
        Assert.Null(agent.LastLineMs);
        Assert.Equal(AgentPhase.Idle, agent.Phase);
        Assert.Null(agent.Actions[0].ResultMs);
        Assert.Equal("sessions", RemoteIngest.EngineAgent(agent, null).Project);
        Assert.Equal(AgentPhase.Tools, Assert.IsType<RelayMessage.Agent>(Message(L.Agent(a => a["phase"] = "tools"))).Value.Phase);
    }

    [Fact]
    public void ResponseIsARecordWithThePrefixedAgent()
    {
        var response = Assert.IsType<RelayMessage.Response>(Message(L.Response(completion: false))).Value;
        Assert.Equal(L.HostId, response.HostId);
        Assert.False(response.IsCompletion);
        Assert.Equal("proj", response.Project);
        var record = response.Record;
        Assert.Equal("resp_1", record.Key);
        Assert.Equal(Prefix + "/home/u/.codex/sessions/a.jsonl", record.AgentId);
        Assert.Equal(SourceKind.Codex, record.Source);
        Assert.Equal("gpt-5.5", record.Model);
        Assert.Equal(L.BaseMs, record.StartMs);
        Assert.Equal(L.BaseMs + 2000, record.EndMs);
        Assert.Equal(L.BaseMs + 300, record.FirstMs);
        Assert.Equal(new TokenCounts(Input: 1200, CachedInput: 1000, Output: 340, Reasoning: 120), record.Tokens);
        Assert.True(record.Final);
        Assert.False(record.Fast);
        Assert.Equal("openai", record.Provider);
        Assert.Equal(1, record.RequestCount);
        Assert.Null(record.CostMicroUsd);
        Assert.Equal(L.HostId, record.Host);
        Assert.True(Assert.IsType<RelayMessage.Response>(Message(L.Response())).Value.IsCompletion);
        Assert.Equal("", Assert.IsType<RelayMessage.Response>(Message(L.Response(project: ""))).Value.Project);
    }

    [Fact]
    public void OtherMessages()
    {
        Assert.Equal(new RelayMessage.Progress(0.42), Message("""{"t":"progress","value":0.42}"""));
        Assert.Equal(new RelayMessage.Progress(1), Message("""{"t":"progress","value":1.2}"""));
        Assert.Equal(new RelayMessage.Progress(0), Message("""{"t":"progress","value":-3}"""));
        Assert.Equal(new RelayMessage.AgentRemoved(Prefix + "/a"), Message("""{"t":"agentRemoved","id":"/a"}"""));
        Assert.Equal(new RelayMessage.Parents(new Dictionary<string, string> { [Prefix + "/c"] = Prefix + "/p" }),
            Message("""{"t":"parents","links":{"/c":"/p"}}"""));
        Assert.Equal(new RelayMessage.Parents(new Dictionary<string, string>()), Message("""{"t":"parents","links":{}}"""));
        Assert.Equal(new RelayMessage.Ready(), Message("""{"t":"ready"}"""));
        Assert.Equal(new RelayMessage.Clock(7, 1_780_000_000_000), Message("""{"t":"clock","id":7,"now":1780000000000}"""));
        Assert.Equal(new RelayMessage.Clock(-7, 1_780_000_000_000), Message(L.Clock(-7, 1_780_000_000_000)));
        Assert.Equal(new RelayMessage.HistoryDone(3, 12), Message("""{"t":"historyDone","days":3,"analyzed":12}"""));
        Assert.Equal(new RelayMessage.HistoryProgress(0.5), Message("""{"t":"historyProgress","value":0.5}"""));
        var historyFailed = Message("""{"t":"error","code":"historyFailed","detail":"disk full"}""");
        Assert.Equal(new RelayMessage.Error(new RelayError(RelayErrorCode.HistoryFailed, "historyFailed", "disk full")), historyFailed);
        Assert.True(historyFailed.IsHistoryFailure);
        Assert.False(Assert.IsType<RelayMessage.Error>(historyFailed).Value.IsFatal);
        Assert.Equal(new RelayMessage.Error(new RelayError(RelayErrorCode.Other, "newCode", "")), Message("""{"t":"error","code":"newCode","detail":""}"""));
        Assert.Equal(RelayErrorCode.BadStart, Assert.IsType<RelayMessage.Error>(Message("""{"t":"error","code":"badStart","detail":"x"}""")).Value.Code);
        Assert.True(Assert.IsType<RelayMessage.Error>(Message("""{"t":"error","code":"internal","detail":"x"}""")).Value.IsFatal);
        const string tool = """{"t":"tool","agent":"/a","source":"claudeCode","callMs":1780000000000,"resultMs":1780000001000,"failed":null,"model":null,"seeded":false}""";
        Assert.Equal(new RelayMessage.Tool(new RemoteToolCompletion(L.HostId, Prefix + "/a", SourceKind.ClaudeCode,
            new WorkAction(1_780_000_000_000, "", 0, null) { ResultMs = 1_780_000_001_000, Model = null, Seeded = false })), Message(tool));
    }

    [Fact]
    public void StatusSkipsUnknownSources()
    {
        const string line = """
            {"t":"status","sources":[{"source":"codex","available":true,"sessions":3,"unsupported":false,"configuredModes":[]},
            {"source":"futureAgent","available":true,"sessions":1,"unsupported":false,"configuredModes":[]}],
            "malformed":2,"unreadable":1,"partialHistory":false}
            """;
        var report = Assert.IsType<RelayMessage.Status>(Message(line.ReplaceLineEndings(""))).Value;
        Assert.Equal(new[] { new RemoteSourceState(SourceKind.Codex, true, 3, false, ExecutionModes.Empty) }, report.Sources);
        Assert.Equal(2, report.Malformed);
        Assert.Equal(1, report.Unreadable);
        Assert.False(report.PartialHistory);
    }

    [Fact]
    public void HistoryDayKeepsTheExactAggregateText()
    {
        const string aggregate =
            """{"requests":3,"input":1200,"cachedInput":0,"cacheWriteInput":0,"output":340,"reasoning":0"""
            + ""","measuredOutput":300,"measuredMs":2000,"unfinalized":0,"agents":1,"costMicroUsd":1234,"unpricedRequests":0"""
            + ""","models":{"gpt-5.5":[3,340,300,2000,1234,0]},"unpriced":[["mystery",null,false,false,1]]"""
            + ""","modes":[["gpt-5.5","serviceTier","fast","recorded","openai",1,100,90,500],["gpt-5.5","future","x","y",null,1,1,1,1]]}""";
        var line = """{"t":"historyDay","day":"2026-11-03","source":"codex","aggregate":""" + aggregate + ""","digest":"0123456789abcdef"}""";
        var day = Assert.IsType<RelayMessage.HistoryDay>(Message(line)).Value;
        Assert.Equal(aggregate, day.AggregateJson);
        Assert.Equal("2026-11-03/codex", day.DigestKey);
        Assert.Equal("0123456789abcdef", day.Digest);
        Assert.Equal(3, day.Aggregate.Requests);
        Assert.Equal(1234, day.Aggregate.CostMicroUsd);
        Assert.Equal(new RemoteDayAggregate.Model(3, 340, 300, 2000, 1234, 0), day.Aggregate.Models["gpt-5.5"]);
        Assert.Equal([new RemoteDayAggregate.Unpriced("mystery", null, false, false, 1)], day.Aggregate.UnpricedFacts);
        var mode = Assert.Single(day.Aggregate.Modes);
        Assert.Equal(ExecutionModeKind.ServiceTier, mode.Kind);
        Assert.Equal("openai", mode.Provider);

        var spaced = """{"t":"historyDay","day":"2026-11-03","source":"codex","aggregate": { "requests" : 1 ,"input":1,"cachedInput":0"""
            + ""","cacheWriteInput":0,"output":1,"reasoning":0,"measuredOutput":0,"measuredMs":0,"unfinalized":0,"agents":1,"costMicroUsd":null"""
            + ""","unpricedRequests":null,"models":{},"unpriced":[],"modes":[]} ,"digest":"0123456789abcdef"}""";
        Assert.StartsWith("""{ "requests" : 1 ,""", Assert.IsType<RelayMessage.HistoryDay>(Message(spaced)).Value.AggregateJson, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryPerfAndDigest()
    {
        var bins = "[" + string.Join(',', Enumerable.Range(0, 64).Select(i => i == 3 ? "2" : "0")) + "]";
        var record = """{"n":2,"outputSum":340,"durationMsSum":2000,"latencyBins":""" + bins
            + ""","reasonN":1,"reasonOutput":100,"reasonDurationMsSum":900,"cacheN":2,"cacheInput":2400,"cacheCached":2000"""
            + ""","toolN":3,"toolLnSum":17.25,"toolFailed":1,"activeMs":60000}""";
        var line = """{"t":"historyPerf","day":"2026-11-03","source":"codex","model":"gpt-5.5","record":""" + record + "}";
        var perf = Assert.IsType<RelayMessage.HistoryPerf>(Message(line)).Value;
        Assert.Equal(record, perf.RecordJson);
        Assert.Equal(2, perf.Record.N);
        Assert.Equal(2, perf.Record.LatencyBins[3]);
        Assert.Equal(17.25, perf.Record.ToolLnSum);
        Assert.Equal(60000, perf.Record.ActiveMs);
        Assert.Equal(new PerfRecordKey("2026-11-03", SourceKind.Codex, "gpt-5.5"), perf.Key);

        var digest = RemoteHistoryDigest.Digest("{}", [("b", "[2]"), ("a", "[1]")]);
        Assert.Equal(TestBytes.Sha256("{}[1][2]")[..16], digest);
        Assert.Equal(TestBytes.Sha256("{}")[..16], RemoteHistoryDigest.Digest("{}", []));
        Assert.Equal(TestBytes.Sha256("{}[B][a][é]")[..16], RemoteHistoryDigest.Digest("{}", [("é", "[é]"), ("a", "[a]"), ("B", "[B]")]));
        Assert.Equal("2026-11-03/claudeCode", RemoteHistoryDigest.Key("2026-11-03", SourceKind.ClaudeCode));
    }

    [Fact]
    public void UnknownTypesAndFieldsAreIgnored()
    {
        Assert.Equal(RelayDecodeKind.Ignored, L.Decode("""{"t":"futureThing","x":1}""").Kind);
        Assert.Equal(new RelayMessage.Ready(), Message("""{"t":"ready","extra":{"nested":[1,2]}}"""));
        Assert.IsType<RelayMessage.Agent>(Message(L.Agent(a => a["newField"] = "x")));
        Assert.IsType<RelayMessage.Response>(Message(L.Response(r => r["newField"] = new List<object?> { 1, null })));
        var action = L.Action();
        action["later"] = true;
        Assert.IsType<RelayMessage.Agent>(Message(L.Agent(a => a["actions"] = new List<object?> { action })));
    }

    // Validation (REMOTE 5.6)

    [Fact]
    public void StructuralViolations()
    {
        Assert.Equal(RemoteLineErrorKind.NotJson, Invalid("not json"));
        Assert.Equal(RemoteLineErrorKind.NotJson, Invalid(""));
        Assert.Equal(RemoteLineErrorKind.NotJson, Invalid("""{"t":"ready"} {"t":"ready"}"""));
        Assert.Equal(RemoteLineErrorKind.NotJson, Invalid("""{"t":"ready",}"""));
        Assert.Equal(RemoteLineErrorKind.NotObject, Invalid("[1,2]"));
        Assert.Equal(RemoteLineErrorKind.NotObject, Invalid("\"ready\""));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid("""{"t":1}"""));
        Assert.Equal(RemoteLineErrorKind.Missing, Invalid("""{"x":"ready"}"""));
        Assert.Equal(RemoteLineErrorKind.NotJson, Invalid("""{"t":"progress","value":NaN}"""));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid("""{"t":"progress","value":"0.5"}"""));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid("""{"t":"agent","a":[]}"""));
        Assert.Equal(RemoteLineErrorKind.Missing, Invalid("""{"t":"agent"}"""));
        var huge = "{\"t\":\"ready\",\"pad\":\"" + new string('x', RemoteLineFramer.MaxLineBytes) + "\"}";
        Assert.Equal(RemoteLineErrorKind.Oversized, Invalid(huge));
        var deep = """{"t":"ready","x":""" + string.Concat(Enumerable.Repeat("[", 100)) + string.Concat(Enumerable.Repeat("]", 100)) + "}";
        Assert.Equal(RemoteLineErrorKind.NotJson, Invalid(deep));
    }

    [Fact]
    public void MissingAndMistypedFields()
    {
        Assert.Equal(RemoteLineErrorKind.Missing, Invalid(L.Response(r => r.Remove("endMs"))));
        Assert.Equal(RemoteLineErrorKind.Missing, Invalid(L.Response(r => r.Remove("output"))));
        Assert.Equal(RemoteLineErrorKind.Missing, Invalid(L.Response(r => r.Remove("final"))));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid(L.Response(r => r["final"] = "yes")));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid(L.Replaced(L.Response(), "\"endMs\":1780000002000", "\"endMs\":1.780000002e12")));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid(L.Replaced(L.Response(), "\"endMs\":1780000002000", "\"endMs\":1780000002000.0")));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid(L.Response(r => r["output"] = "340")));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid(L.Response(r => r["endMs"] = null)));
        Assert.Equal(RemoteLineErrorKind.Missing, Invalid(L.Json(new() { ["t"] = "response", ["r"] = L.ResponseObject(), ["project"] = "p" })));
        Assert.Equal(RemoteLineErrorKind.Missing, Invalid(L.Agent(a => a.Remove("subagent"))));
        Assert.Equal(RemoteLineErrorKind.Missing, Invalid(L.Agent(a => a.Remove("modes"))));
        Assert.Equal(RemoteLineErrorKind.Missing, Invalid(L.Agent(a => a.Remove("actions"))));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid(L.Agent(a => a["subagent"] = 0)));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid(L.Agent(a => a["modes"] = "none")));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(L.Agent(a => a["phase"] = "thinking")));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(L.Agent(a => a["source"] = "unknownAgent")));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(L.Agent(a => a["id"] = "")));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(L.Response(r => r["key"] = "")));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(L.Response(r => r["agent"] = "")));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid("""{"t":"agentRemoved","id":""}"""));
    }

    [Fact]
    public void StringBounds()
    {
        Assert.Equal(RemoteLineErrorKind.TooLong, Invalid(L.Agent(a => a["id"] = "/" + new string('a', 4096))));
        Assert.IsType<RelayMessage.Agent>(Message(L.Agent(a => a["id"] = "/" + new string('a', 4095))));
        Assert.Equal(RemoteLineErrorKind.TooLong, Invalid(L.Agent(a => a["project"] = new string('p', 257))));
        Assert.IsType<RelayMessage.Agent>(Message(L.Agent(a => a["project"] = new string('p', 256))));
        Assert.Equal(RemoteLineErrorKind.TooLong, Invalid(L.Agent(a => a["project"] = new string('é', 129))));
        Assert.IsType<RelayMessage.Agent>(Message(L.Agent(a => a["project"] = new string('é', 128))));
        Assert.Equal(RemoteLineErrorKind.TooLong, Invalid(L.Agent(a => a["cwd"] = "/" + new string('c', 4096))));
        Assert.Equal(RemoteLineErrorKind.TooLong, Invalid(L.Agent(a => a["model"] = new string('m', 257))));
        Assert.Equal(RemoteLineErrorKind.TooLong, Invalid(L.Response(r => r["key"] = new string('k', 513))));
        Assert.IsType<RelayMessage.Response>(Message(L.Response(r => r["key"] = new string('k', 512))));
        Assert.Equal(RemoteLineErrorKind.TooLong, Invalid(L.Response(r => r["provider"] = new string('p', 65))));
        Assert.Equal(RemoteLineErrorKind.TooLong, Invalid(L.Response(r => r["model"] = new string('m', 257))));
        Assert.Equal(RemoteLineErrorKind.TooLong, Invalid(L.Response(project: new string('p', 257))));
        var longTool = L.Action();
        longTool["tool"] = new string('t', 129);
        Assert.Equal(RemoteLineErrorKind.TooLong, Invalid(L.Agent(a => a["actions"] = new List<object?> { longTool })));
        var detail = "{\"t\":\"error\",\"code\":\"internal\",\"detail\":\"" + new string('x', 201) + "\"}";
        Assert.Equal(RemoteLineErrorKind.TooLong, Invalid(detail));
        Assert.IsType<RelayMessage.Error>(Message("{\"t\":\"error\",\"code\":\"internal\",\"detail\":\"" + new string('é', 200) + "\"}"));
    }

    [Fact]
    public void ControlCharactersInIdsKeysAndProjects()
    {
        foreach (var bad in new[] { "a\0b", "a\nb", "a\u001Fb", "a\u007Fb" })
        {
            Assert.Equal(RemoteLineErrorKind.BadCharacter, Invalid(L.Agent(a => a["id"] = bad)));
            Assert.Equal(RemoteLineErrorKind.BadCharacter, Invalid(L.Agent(a => a["project"] = bad)));
            Assert.Equal(RemoteLineErrorKind.BadCharacter, Invalid(L.Agent(a => a["cwd"] = bad)));
            Assert.Equal(RemoteLineErrorKind.BadCharacter, Invalid(L.Response(r => r["key"] = bad)));
            Assert.Equal(RemoteLineErrorKind.BadCharacter, Invalid(L.Response(r => r["agent"] = bad)));
            Assert.Equal(RemoteLineErrorKind.BadCharacter, Invalid(L.Response(project: bad)));
        }

        Assert.Equal(RemoteLineErrorKind.BadCharacter, Invalid("""{"t":"agentRemoved","id":"a\u0000"}"""));
        Assert.Equal(RemoteLineErrorKind.BadCharacter, Invalid("""{"t":"parents","links":{"a\n":"/p"}}"""));
        Assert.Equal(RemoteLineErrorKind.BadCharacter, Invalid("""{"t":"parents","links":{"/c":"/p\u0007"}}"""));
        Assert.Equal(RemoteLineErrorKind.BadCharacter, Invalid("""{"t":"parents","links":{"":"/p"}}"""));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid("""{"t":"parents","links":{"/c":7}}"""));
        Assert.Equal(RemoteLineErrorKind.BadCharacter, Invalid("""{"t":"error","code":"internal","detail":"a\nb"}"""));
        Assert.Equal("專案 ü", Assert.IsType<RelayMessage.Agent>(Message(L.Agent(a => a["project"] = "專案 ü"))).Value.Project);
    }

    [Fact]
    public void ModelAndProviderCharacters()
    {
        foreach (var good in new[]
        {
            "claude-opus-5-5[1m]", "Gemini 3.1 Pro (High)", "custom:GPT-5.5-[OpenAI-BYOK]-0", "anthropic/claude-sonnet-4.5",
            "us.anthropic.claude-sonnet-4-5-20250929-v1:0", "claude-opus-5-5@default",
        })
        {
            Assert.Equal(good, Assert.IsType<RelayMessage.Response>(Message(L.Response(r => r["model"] = good))).Value.Record.Model);
        }

        foreach (var bad in new[] { "gpt\0", "gpt\u0085", "gpt\u202E", "gpt\u2066", "gpt\u2028", "gpt\u2029", "gpt\u200F", "gpt\u200E", "gpt\u061C", "gpt\u009F" })
        {
            Assert.True(RemoteLineErrorKind.BadCharacter == Invalid(L.Response(r => r["model"] = bad)), Escaped(bad));
            Assert.True(RemoteLineErrorKind.BadCharacter == Invalid(L.Response(r => r["provider"] = bad)), Escaped(bad));
            Assert.True(RemoteLineErrorKind.BadCharacter == Invalid(L.Agent(a => a["model"] = bad)), Escaped(bad));
            var action = L.Action();
            action["model"] = bad;
            Assert.True(RemoteLineErrorKind.BadCharacter == Invalid(L.Agent(a => a["actions"] = new List<object?> { action })), Escaped(bad));
        }

        var response = Assert.IsType<RelayMessage.Response>(Message(L.Response(r =>
        {
            r["provider"] = "";
            r["model"] = "";
        }))).Value;
        Assert.Null(response.Record.Provider);
        Assert.Null(response.Record.Model);
        Assert.Null(Assert.IsType<RelayMessage.Agent>(Message(L.Agent(a => a["model"] = ""))).Value.Model);
    }

    [Fact]
    public void IntegersBeyond2To53AreInvalid()
    {
        Assert.Equal(new RelayMessage.Clock(9_007_199_254_740_992, 1_780_000_000_000), Message("""{"t":"clock","id":9007199254740992,"now":1780000000000}"""));
        Assert.IsType<RelayMessage.Clock>(Message("""{"t":"clock","id":-9007199254740992,"now":1780000000000}"""));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid("""{"t":"clock","id":9007199254740993,"now":1780000000000}"""));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid("""{"t":"clock","id":-9007199254740993,"now":1780000000000}"""));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid("""{"t":"clock","id":123456789012345678901234567890,"now":1780000000000}"""));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid("""{"t":"clock","id":1e3,"now":1780000000000}"""));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid("""{"t":"historyDone","days":-1,"analyzed":0}"""));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid("""{"t":"status","sources":[],"malformed":-1,"unreadable":0,"partialHistory":false}"""));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(L.Response(r => r["requestCount"] = 0)));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(L.Response(r => r["requestCount"] = 100_001)));
        Assert.Equal(100_000, Assert.IsType<RelayMessage.Response>(Message(L.Response(r => r["requestCount"] = 100_000))).Value.Record.RequestCount);
    }

    [Fact]
    public void TokenValuesAreClamped()
    {
        var line = L.Response();
        line = L.Replaced(line, "\"output\":340", "\"output\":1e20");
        line = L.Replaced(line, "\"input\":1200", "\"input\":123456789012345678901234567890");
        line = L.Replaced(line, "\"reasoning\":120", "\"reasoning\":12.9");
        line = L.Replaced(line, "\"cachedInput\":1000", "\"cachedInput\":-7");
        line = L.Replaced(line, "\"cacheWriteInput\":0", "\"cacheWriteInput\":5");
        line = L.Replaced(line, "\"cacheWrite1hInput\":0", "\"cacheWrite1hInput\":1e999");
        var response = Assert.IsType<RelayMessage.Response>(Message(line)).Value;
        Assert.Equal(new TokenCounts(Input: 1_000_000_000_000, CachedInput: 0, CacheWriteInput: 5, Output: 1_000_000_000_000, Reasoning: 12,
            CacheWrite1hInput: 5), response.Record.Tokens);

        var negative = L.Replaced(L.Response(), "\"output\":340", "\"output\":-1e999");
        Assert.Equal(0, Assert.IsType<RelayMessage.Response>(Message(negative)).Value.Record.Tokens.Output);
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid(L.Response(r => r["input"] = null)));
    }

    [Fact]
    public void TimesOutsideTheYears2000To2100AreInvalid()
    {
        Assert.Equal(946_684_800_000, RemoteFields.EarliestMs);
        Assert.Equal(RemoteFields.LatestMs, new DateTimeOffset(2100, 12, 31, 23, 59, 59, 999, TimeSpan.Zero).ToUnixTimeMilliseconds());
        Assert.IsType<RelayMessage.Response>(Message(L.Response(r =>
        {
            r["endMs"] = RemoteFields.EarliestMs;
            r["startMs"] = null;
            r["firstMs"] = null;
        })));
        Assert.IsType<RelayMessage.Response>(Message(L.Response(r => r["endMs"] = RemoteFields.LatestMs)));
        Assert.Equal(RemoteLineErrorKind.BadTime, Invalid(L.Response(r => r["endMs"] = RemoteFields.EarliestMs - 1)));
        Assert.Equal(RemoteLineErrorKind.BadTime, Invalid(L.Response(r => r["endMs"] = RemoteFields.LatestMs + 1)));
        Assert.Equal(RemoteLineErrorKind.BadTime, Invalid(L.Response(r => r["startMs"] = 0)));
        Assert.Equal(RemoteLineErrorKind.BadTime, Invalid(L.Response(r => r["firstMs"] = -1)));
        Assert.Equal(RemoteLineErrorKind.BadTime, Invalid(L.Agent(a => a["lastLineMs"] = 1_000)));
        Assert.Equal(RemoteLineErrorKind.BadTime, Invalid(L.Agent(a => a["inFlightStartMs"] = RemoteFields.LatestMs + 1)));
        Assert.Equal(RemoteLineErrorKind.BadTime, Invalid(L.Agent(a => a["actions"] = new List<object?> { L.Action(callMs: 5) })));
        Assert.Equal(RemoteLineErrorKind.BadTime, Invalid(L.Agent(a => a["actions"] = new List<object?> { L.Action(resultMs: 5) })));
        Assert.Equal(RemoteLineErrorKind.BadTime, Invalid("""{"t":"hello","protocol":1,"version":"1","arch":"x86_64","now":99}"""));
        Assert.Equal(RemoteLineErrorKind.BadTime, Invalid("""{"t":"clock","id":1,"now":99}"""));
        Assert.Equal(RemoteLineErrorKind.BadTime,
            Invalid("""{"t":"tool","agent":"/a","source":"codex","callMs":1780000000000,"resultMs":4133980800000,"failed":null,"model":null,"seeded":false}"""));
    }

    [Fact]
    public void ModeFactsFollowExecutionModes()
    {
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(ModesLine(L.Fact(value: "fa st"))));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(ModesLine(L.Fact(raw: "-x"))));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(ModesLine(L.Fact(kind: "speed"))));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(ModesLine(L.Fact(kind: "turbo"))));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(ModesLine(L.Fact(evidence: "guessed"))));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(ModesLine(L.Fact(scope: "forever"))));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(ModesLine(L.Fact(provider: "https://x"))));
        Assert.Equal(RemoteLineErrorKind.TooLong, Invalid(ModesLine(L.Fact(provider: new string('p', 33)))));
        Assert.Equal(RemoteLineErrorKind.TooLong, Invalid(ModesLine(L.Fact(raw: new string('r', 65)))));
        Assert.Equal(RemoteLineErrorKind.TooMany,
            Invalid(L.Agent(a => a["modes"] = Enumerable.Range(0, 21).Select(_ => (object?)L.Fact()).ToList())));
        Assert.IsType<RelayMessage.Agent>(Message(L.Agent(a => a["modes"] = Enumerable.Range(0, 20).Select(_ => (object?)L.Fact()).ToList())));
        Assert.Equal(RemoteLineErrorKind.TooMany,
            Invalid(L.Agent(a => a["sessionModes"] = Enumerable.Range(0, 21).Select(_ => (object?)L.Fact()).ToList())));
        Assert.Equal(RemoteLineErrorKind.Missing,
            Invalid(L.Response(r => r["modes"] = new List<object?> { new Dictionary<string, object?> { ["kind"] = "effort" } })));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid(L.Response(r => r["modes"] = new List<object?> { "effort" })));

        var configured = L.Fact(kind: "effort", value: "high", raw: "high", evidence: "configured", provider: null, scope: "configuration");
        var agent = Assert.IsType<RelayMessage.Agent>(Message(L.Agent(a => a["configuredModes"] = new List<object?> { configured }))).Value;
        var fact = Assert.Single(agent.ConfiguredModes.Facts);
        Assert.Null(fact.Provider);
        Assert.Equal(ExecutionModeEvidence.Configured, fact.Evidence);
        Assert.Equal(ExecutionModeScope.Configuration, fact.Scope);
        Assert.Equal(ExecutionModeKind.Effort, fact.Kind);
    }

    [Fact]
    public void ActionLimitsAndDigests()
    {
        Assert.Equal(64, Assert.IsType<RelayMessage.Agent>(Message(L.Agent(a => a["actions"] = Actions(64)))).Value.Actions.Count);
        Assert.Equal(RemoteLineErrorKind.TooMany, Invalid(L.Agent(a => a["actions"] = Actions(65))));
        foreach (var digest in new[] { "0123456789ABCDEF", "0123", "0123456789abcdeg", "" })
        {
            var action = L.Action();
            action["digest"] = digest;
            Assert.True(RemoteLineErrorKind.OutOfRange == Invalid(L.Agent(a => a["actions"] = new List<object?> { action })), digest);
        }

        var tooLong = L.Action();
        tooLong["digest"] = "0123456789abcdef0";
        Assert.Equal(RemoteLineErrorKind.TooLong, Invalid(L.Agent(a => a["actions"] = new List<object?> { tooLong })));
    }

    [Fact]
    public void ParentLinksAreLimitedTo4096()
    {
        Assert.Equal(4096, Assert.IsType<RelayMessage.Parents>(Message(ParentsLine(4096))).Links.Count);
        Assert.Equal(RemoteLineErrorKind.TooMany, Invalid(ParentsLine(4097)));
    }

    [Fact]
    public void HistoryViolations()
    {
        Assert.IsType<RelayMessage.HistoryDay>(Message(HistoryHead + SmallAggregate + "}"));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(L.Replaced(HistoryHead, "2026-11-03", "2026-1-3") + SmallAggregate + "}"));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(L.Replaced(HistoryHead, "0123456789abcdef", "XYZ") + SmallAggregate + "}"));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(L.Replaced(HistoryHead, "\"codex\"", "\"future\"") + SmallAggregate + "}"));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(HistoryHead + L.Replaced(SmallAggregate, "\"models\":{}", "\"models\":{\"m\":[1,2,3]}") + "}"));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(HistoryHead + L.Replaced(SmallAggregate, "\"requests\":1", "\"requests\":-1") + "}"));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid(HistoryHead + L.Replaced(SmallAggregate, "\"models\":{}", "\"models\":[]") + "}"));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid(HistoryHead + L.Replaced(SmallAggregate, "\"unpriced\":[]", "\"unpriced\":[[\"m\",null,1,false,1]]") + "}"));
        Assert.Equal(RemoteLineErrorKind.Missing, Invalid(HistoryHead + L.Replaced(SmallAggregate, "\"agents\":1,", "") + "}"));
        Assert.Equal(RemoteLineErrorKind.WrongType, Invalid(HistoryHead + "[]}"));
        const string perf = """{"t":"historyPerf","day":"2026-11-03","source":"codex","model":"m","record":{"n":1,"outputSum":1"""
            + ""","durationMsSum":1,"latencyBins":[1,2],"reasonN":0,"reasonOutput":0,"reasonDurationMsSum":0,"cacheN":0"""
            + ""","cacheInput":0,"cacheCached":0,"toolN":0,"toolLnSum":0,"toolFailed":0,"activeMs":0}}""";
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(perf));
        var bins = "[" + string.Join(',', Enumerable.Repeat("0", 64)) + "]";
        Assert.IsType<RelayMessage.HistoryPerf>(Message(L.Replaced(perf, "[1,2]", bins)));
        Assert.Equal(RemoteLineErrorKind.TooMany, Invalid(L.Replaced(perf, "[1,2]", "[" + string.Join(',', Enumerable.Repeat("0", 65)) + "]")));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(L.Replaced(perf, "[1,2]", "[-1" + string.Concat(Enumerable.Repeat(",0", 63)) + "]")));
        Assert.Equal(RemoteLineErrorKind.OutOfRange, Invalid(L.Replaced(L.Replaced(perf, "[1,2]", bins), "\"toolLnSum\":0", "\"toolLnSum\":1e999")));
    }

    [Fact]
    public void HistoryDaysStayInsideTheYears2000To2100()
    {
        var line = HistoryHead + SmallAggregate + "}";
        foreach (var day in new[] { "2000-01-01", "2100-12-31", "2024-02-29", "2000-02-29" })
        {
            Assert.IsType<RelayMessage.HistoryDay>(Message(L.Replaced(line, "2026-11-03", day)));
        }

        foreach (var day in new[] { "1999-12-31", "2101-01-01", "2026-00-10", "2026-13-01", "2026-02-29", "2100-02-29", "2026-04-31", "2026-11-00", "2026-11-3x" })
        {
            Assert.True(RemoteLineErrorKind.OutOfRange == Invalid(L.Replaced(line, "2026-11-03", day)), day);
        }

        Assert.True(RemoteFields.IsDay("2026-11-03"));
        Assert.False(RemoteFields.IsDay("2026/11/03"));
        Assert.False(RemoteFields.IsDay("２０２６-11-03"));
    }

    [Fact]
    public void HistoryPerfModelsUseTheModelCharacterRules()
    {
        const string perf = """{"t":"historyPerf","day":"2026-11-03","source":"codex","model":"MODEL","record":{"n":1,"outputSum":1"""
            + ""","durationMsSum":1,"latencyBins":BINS,"reasonN":0,"reasonOutput":0,"reasonDurationMsSum":0,"cacheN":0"""
            + ""","cacheInput":0,"cacheCached":0,"toolN":0,"toolLnSum":0,"toolFailed":0,"activeMs":0}}""";
        var line = perf.Replace("BINS", "[" + string.Join(',', Enumerable.Repeat("0", 64)) + "]", StringComparison.Ordinal);
        Assert.Equal("", Assert.IsType<RelayMessage.HistoryPerf>(Message(line.Replace("MODEL", "", StringComparison.Ordinal))).Value.Model);
        Assert.Equal("Gemini 3.1 Pro (High)",
            Assert.IsType<RelayMessage.HistoryPerf>(Message(line.Replace("MODEL", "Gemini 3.1 Pro (High)", StringComparison.Ordinal))).Value.Model);
        Assert.Equal(RemoteLineErrorKind.BadCharacter, Invalid(line.Replace("MODEL", "a\\u202Eb", StringComparison.Ordinal)));
    }

    [Fact]
    public void DigestCoversTheTextsExactlyAsSent()
    {
        const string aggregate = """{ "requests":1,"input":1,"cachedInput":0,"cacheWriteInput":0,"output":1,"reasoning":0,"measuredOutput":0"""
            + ""","measuredMs":0,"unfinalized":0,"agents":1,"costMicroUsd":null,"unpricedRequests":null"""
            + ""","models":{"MODEL_ESCAPED":[1,1,0,0,null,null]},"unpriced":[],"modes":[] }""";
        var record = """{"n":1,"outputSum":1,"durationMsSum":1,"latencyBins":[""" + string.Join(',', Enumerable.Repeat("0", 64))
            + """],"reasonN":0,"reasonOutput":0,"reasonDurationMsSum":0,"cacheN":0,"cacheInput":0,"cacheCached":0"""
            + ""","toolN":2,"toolLnSum":1.50,"toolFailed":0,"activeMs":0}""";
        var escapedAggregate = aggregate.Replace("MODEL_ESCAPED", Backslash + "u6a21", StringComparison.Ordinal);
        var digest = RemoteHistoryDigest.Digest(escapedAggregate, [("模", record)]);
        var dayLine = """{"t":"historyDay","day":"2026-11-03","source":"codex","aggregate":""" + escapedAggregate + ""","digest":"""
            + "\"" + digest + "\"}";
        var perfLine = """{"t":"historyPerf","day":"2026-11-03","source":"codex","model":"""
            + "\"" + Backslash + "u6a21\",\"record\":" + record + "}";

        var day = Assert.IsType<RelayMessage.HistoryDay>(Message(dayLine)).Value;
        var perf = Assert.IsType<RelayMessage.HistoryPerf>(Message(perfLine)).Value;
        Assert.Equal(escapedAggregate, day.AggregateJson);
        Assert.Contains(Backslash + "u6a21", day.AggregateJson, StringComparison.Ordinal);
        Assert.Equal("模", Assert.Single(day.Aggregate.Models).Key);
        Assert.Equal("模", perf.Model);
        Assert.Equal(record, perf.RecordJson);
        Assert.Contains("\"toolLnSum\":1.50", perf.RecordJson, StringComparison.Ordinal);
        Assert.Equal(day.Digest, RemoteHistoryDigest.Digest(day.AggregateJson, [(perf.Model, perf.RecordJson)]));
        Assert.Equal(escapedAggregate, RelayLineDecoder.RawMember("aggregate", System.Text.Encoding.UTF8.GetBytes(dayLine)));
        Assert.Null(RelayLineDecoder.RawMember("missing", System.Text.Encoding.UTF8.GetBytes(dayLine)));
    }

    private const string Backslash = "\\";

    private static string ModesLine(Dictionary<string, object?> fact) => L.Agent(a => a["modes"] = new List<object?> { fact });

    private static List<object?> Actions(int count) => [.. Enumerable.Range(0, count).Select(_ => (object?)L.Action())];

    private static string ParentsLine(int count) =>
        """{"t":"parents","links":{""" + string.Join(',', Enumerable.Range(0, count).Select(i => string.Create(CultureInfo.InvariantCulture, $"\"/c{i}\":\"/p\""))) + "}}";

    private static string Escaped(string text) => string.Concat(text.Select(c => c < 0x80 && !char.IsControl(c) ? c.ToString() : $"\\u{(int)c:X4}"));
}
