#include <gtest/gtest.h>

#include "agentspeed/remote_protocol.hpp"
#include "remote_test_support.hpp"

namespace {

using namespace agentspeed;
using namespace agentspeed::remote;
using remote_test::kNow;

const LineDecoder kDecoder(remote_test::kHost);
const std::string kPrefix = "ssh://" + remote_test::kHost + "/";

RelayMessage decoded(const std::string& line) {
    auto outcome = kDecoder.decode(line);
    if (const auto* error = std::get_if<LineError>(&outcome)) ADD_FAILURE() << "invalid: " << error->field << " in " << line;
    if (std::holds_alternative<LineDecoder::Ignored>(outcome)) ADD_FAILURE() << "ignored: " << line;
    auto* message = std::get_if<RelayMessage>(&outcome);
    return message ? *message : RelayMessage{Ready{}};
}

bool invalid(const std::string& line) { return std::holds_alternative<LineError>(kDecoder.decode(line)); }

std::string replace(std::string text, const std::string& from, const std::string& to) {
    const auto at = text.find(from);
    EXPECT_NE(at, std::string::npos) << from;
    if (at != std::string::npos) text.replace(at, from.size(), to);
    return text;
}

const std::string kAggregate =
    R"({"requests":3,"input":100,"cachedInput":10,"cacheWriteInput":0,"output":50,"reasoning":5,"measuredOutput":40,"measuredMs":2000,)"
    R"("unfinalized":0,"agents":1,"costMicroUsd":1234,"unpricedRequests":0,"models":{"gpt-5":[3,50,40,2000,1234,0]},)"
    R"("unpriced":[["mystery",null,false,false,2]],"modes":[["gpt-5","effort","high","recorded",null,3,50,40,2000]]})";

std::string record() {
    std::string bins = "[";
    for (int k = 0; k < 64; ++k) bins += (k ? "," : "") + std::to_string(k == 10 ? 2 : 0);
    bins += "]";
    return R"({"n":2,"outputSum":50,"durationMsSum":2000,"latencyBins":)" + bins +
           R"(,"reasonN":1,"reasonOutput":5,"reasonDurationMsSum":100,"cacheN":2,"cacheInput":100,"cacheCached":10,"toolN":1,"toolLnSum":0.6931471805599453,"toolFailed":0,"activeMs":3000})";
}

TEST(RemoteDecoder, Hello) {
    const auto hello = std::get<Hello>(decoded(remote_test::helloLine()));
    EXPECT_EQ(hello.protocol, 1);
    EXPECT_EQ(hello.version, "1.6.1");
    EXPECT_EQ(hello.arch, "x86_64");
    EXPECT_EQ(hello.nowMs, kNow);
    const auto other = std::get<Hello>(decoded(R"({"t":"hello","protocol":2})"));
    EXPECT_EQ(other.protocol, 2);
    EXPECT_TRUE(other.version.empty());
}

TEST(RemoteDecoder, AgentDecodesEveryField) {
    const std::string action = R"({"tool":"Bash","digest":"0123456789abcdef","callMs":)" + std::to_string(kNow - 5000) + R"(,"resultMs":)" +
                               std::to_string(kNow - 4000) + R"(,"failed":false,"model":"gpt-5","seeded":true})";
    auto line = remote_test::agentLine("/home/dev/.codex/sessions/a.jsonl", "generating", kNow - 1000, action);
    line = replace(line, R"("modes":[])", R"("modes":[{"kind":"effort","value":"high","rawValue":"High","evidence":"recorded","provider":null,"scope":"turn"}])");
    const auto agent = std::get<RemoteAgent>(decoded(line));
    EXPECT_EQ(agent.hostId, remote_test::kHost);
    EXPECT_EQ(agent.id, kPrefix + "/home/dev/.codex/sessions/a.jsonl");
    EXPECT_EQ(agent.id.find("//home"), kPrefix.size() - 1);
    EXPECT_EQ(agent.relayId, "/home/dev/.codex/sessions/a.jsonl");
    EXPECT_EQ(agent.source, SourceKind::Codex);
    EXPECT_EQ(agent.project, "proj");
    EXPECT_EQ(agent.cwd, "/home/dev/proj");
    EXPECT_EQ(agent.model, "gpt-5");
    EXPECT_EQ(agent.phase, Phase::Generating);
    EXPECT_EQ(agent.lastLineMs, kNow - 1000);
    ASSERT_EQ(agent.modes.facts().size(), 1u);
    EXPECT_EQ(agent.modes.facts()[0].value, "high");
    EXPECT_EQ(agent.modes.facts()[0].raw, "High");
    EXPECT_EQ(agent.modes.facts()[0].scope, ModeScope::Turn);
    ASSERT_EQ(agent.actions.size(), 1u);
    EXPECT_EQ(agent.actions[0].tool, "Bash");
    EXPECT_EQ(agent.actions[0].digest, "0123456789abcdef");
    EXPECT_EQ(agent.actions[0].resultMs, kNow - 4000);
    EXPECT_EQ(agent.actions[0].failed, false);
    EXPECT_TRUE(agent.actions[0].seeded);
}

TEST(RemoteDecoder, AgentWithNullsAndAbsentNullables) {
    const auto agent = std::get<RemoteAgent>(decoded(
        R"({"t":"agent","a":{"id":"x","source":"claudeCode","subagent":true,"phase":"tools","modes":[],"sessionModes":[],"configuredModes":[],"actions":[],"model":""}})"));
    EXPECT_EQ(agent.project, std::nullopt);
    EXPECT_EQ(agent.cwd, std::nullopt);
    EXPECT_EQ(agent.model, std::nullopt);
    EXPECT_EQ(agent.lastLineMs, std::nullopt);
    EXPECT_TRUE(agent.subagent);
    EXPECT_EQ(agent.phase, Phase::Tools);
}

TEST(RemoteDecoder, ResponseIsARecordWithThePrefixedAgent) {
    auto line = remote_test::responseLine("k1", "/a.jsonl", kNow, 500);
    line = replace(line, R"("cacheWriteInput":0,"cacheWrite1hInput":0)", R"("cacheWriteInput":30,"cacheWrite1hInput":99)");
    line = replace(line, R"("provider":null)", R"("provider":"openai")");
    const auto response = std::get<RemoteResponse>(decoded(line));
    EXPECT_EQ(response.hostId, remote_test::kHost);
    EXPECT_EQ(response.record.key, "k1");
    EXPECT_EQ(response.record.agentId, kPrefix + "/a.jsonl");
    EXPECT_EQ(response.record.endMs, kNow);
    EXPECT_EQ(response.record.startMs, kNow - 2000);
    EXPECT_EQ(response.record.tokens.output, 500);
    EXPECT_EQ(response.record.tokens.cacheWriteInput, 30);
    EXPECT_EQ(response.record.tokens.cacheWrite1hInput, 30);
    EXPECT_EQ(response.record.provider, "openai");
    EXPECT_TRUE(response.completion);
    EXPECT_EQ(response.project, "proj");
}

TEST(RemoteDecoder, OtherMessages) {
    EXPECT_DOUBLE_EQ(std::get<Progress>(decoded(R"({"t":"progress","value":1.5})")).value, 1.0);
    EXPECT_DOUBLE_EQ(std::get<Progress>(decoded(R"({"t":"progress","value":-1})")).value, 0.0);
    EXPECT_EQ(std::get<AgentRemoved>(decoded(R"({"t":"agentRemoved","id":"/x"})")).id, kPrefix + "/x");
    const auto parents = std::get<Parents>(decoded(R"({"t":"parents","links":{"/child":"/parent"}})"));
    EXPECT_EQ(parents.links.at(kPrefix + "/child"), kPrefix + "/parent");
    EXPECT_TRUE(std::holds_alternative<Ready>(decoded(R"({"t":"ready"})")));
    const auto clock = std::get<ClockReply>(decoded(remote_test::clockLine(7, kNow)));
    EXPECT_EQ(clock.id, 7);
    const auto tool = std::get<RemoteTool>(decoded(R"({"t":"tool","agent":"/a","source":"codex","callMs":)" + std::to_string(kNow - 10) +
                                                   R"(,"resultMs":)" + std::to_string(kNow) + R"(,"failed":null,"model":null,"seeded":false})"));
    EXPECT_EQ(tool.agentId, kPrefix + "/a");
    EXPECT_EQ(tool.action.resultMs, kNow);
    EXPECT_EQ(std::get<HistoryDone>(decoded(R"({"t":"historyDone","days":3,"analyzed":9})")).days, 3);
    const auto error = std::get<RelayError>(decoded(R"({"t":"error","code":"historyFailed","detail":"disk full"})"));
    EXPECT_EQ(error.code, RelayError::Code::HistoryFailed);
    EXPECT_FALSE(error.fatal());
    EXPECT_TRUE(std::get<RelayError>(decoded(R"({"t":"error","code":"later","detail":""})")).fatal());
}

TEST(RemoteDecoder, StatusSkipsUnknownSources) {
    const auto report = std::get<SourceReport>(decoded(
        R"({"t":"status","sources":[{"source":"codex","available":true,"sessions":3,"unsupported":false,"configuredModes":[]},)"
        R"({"source":"newTool","available":true,"sessions":1,"unsupported":false,"configuredModes":[]}],"malformed":2,"unreadable":1,"partialHistory":true})"));
    ASSERT_EQ(report.sources.size(), 1u);
    EXPECT_EQ(report.sources[0].sessions, 3);
    EXPECT_EQ(report.malformed, 2);
    EXPECT_TRUE(report.partialHistory);
}

TEST(RemoteDecoder, HistoryDayKeepsTheExactAggregateText) {
    const auto spaced = replace(kAggregate, R"("requests":3,)", R"("requests" : 3 ,)");
    const auto day = std::get<HistoryDay>(decoded(R"({"t":"historyDay","day":"2026-05-27","source":"codex","aggregate":)" + spaced +
                                                  R"(,"digest":"0123456789abcdef"})"));
    EXPECT_EQ(day.aggregateJson, spaced);
    EXPECT_EQ(day.day, "2026-05-27");
    EXPECT_EQ(day.digestKey(), "2026-05-27/codex");
    EXPECT_EQ(day.aggregate.requests, 3);
    EXPECT_EQ(day.aggregate.costMicroUsd, 1234);
    EXPECT_EQ(day.aggregate.models.at("gpt-5").measuredMs, 2000);
    EXPECT_EQ(day.aggregate.unpriced.size(), 1u);
    EXPECT_EQ(day.aggregate.modes.size(), 1u);
    EXPECT_TRUE(decodeAggregateText(kAggregate));
    EXPECT_FALSE(decodeAggregateText(R"({"requests":-1})"));
}

TEST(RemoteDecoder, HistoryPerfAndDigest) {
    const auto perf = std::get<HistoryPerf>(decoded(R"({"t":"historyPerf","day":"2026-05-27","source":"codex","model":"gpt-5","record":)" + record() + "}"));
    EXPECT_EQ(perf.recordJson, record());
    EXPECT_EQ(perf.record.n, 2);
    EXPECT_EQ(perf.record.latencyBins[10], 2);
    EXPECT_NEAR(perf.record.toolLnSum, 0.693147, 1e-6);
    EXPECT_TRUE(decodePerfRecordText(record()));
    // The relay's digest: SHA-256 of the aggregate text and the records in model order, first 16 hex digits.
    const auto digest = historyDigest(kAggregate, {{"z", "B"}, {"a", "A"}});
    EXPECT_EQ(digest, historyDigest(kAggregate + "AB", {}));
    EXPECT_EQ(digest.size(), 16u);
}

TEST(RemoteDecoder, UnknownTypesAndFieldsAreIgnored) {
    EXPECT_TRUE(std::holds_alternative<LineDecoder::Ignored>(kDecoder.decode(R"({"t":"future","x":1})")));
    const auto hello = remote_test::helloLine();
    EXPECT_TRUE(std::holds_alternative<RelayMessage>(kDecoder.decode(replace(hello, R"("arch")", R"("extra":[1,{"a":null}],"arch")"))));
}

TEST(RemoteDecoder, StructuralViolations) {
    EXPECT_EQ(std::get<LineError>(kDecoder.decode("not json")).kind, LineError::Kind::NotJson);
    EXPECT_EQ(std::get<LineError>(kDecoder.decode("[1,2]")).kind, LineError::Kind::NotObject);
    EXPECT_EQ(std::get<LineError>(kDecoder.decode(R"({"t":"ready"} trailing)")).kind, LineError::Kind::NotJson);
    EXPECT_EQ(std::get<LineError>(kDecoder.decode(std::string(limits::kRelayLineBytes + 1, ' '))).kind, LineError::Kind::Oversized);
    EXPECT_TRUE(invalid(R"({"x":1})"));
    EXPECT_TRUE(invalid(R"({"t":5})"));
    EXPECT_TRUE(invalid("{\"t\":\"ready\",\"x\":\"\xff\"}"));
}

TEST(RemoteDecoder, MissingAndMistypedFields) {
    EXPECT_TRUE(invalid(R"({"t":"clock","id":1})"));
    EXPECT_TRUE(invalid(R"({"t":"clock","id":"1","now":1780000000000})"));
    EXPECT_TRUE(invalid(R"({"t":"clock","id":1.5,"now":1780000000000})"));
    EXPECT_TRUE(invalid(replace(remote_test::agentLine("/a"), R"("subagent":false)", R"("subagent":0)")));
    EXPECT_TRUE(invalid(replace(remote_test::agentLine("/a"), R"("phase":"idle")", R"("phase":"sleeping")")));
    EXPECT_TRUE(invalid(replace(remote_test::agentLine("/a"), R"("source":"codex")", R"("source":"unknownTool")")));
    EXPECT_TRUE(invalid(replace(remote_test::responseLine("k", "/a", kNow), R"("final":true)", R"("final":null)")));
    EXPECT_TRUE(invalid(replace(remote_test::responseLine("k", "/a", kNow), R"("output":100)", R"("output":"100")")));
    EXPECT_TRUE(invalid(replace(remote_test::responseLine("k", "/a", kNow), R"("requestCount":1)", R"("requestCount":0)")));
    EXPECT_TRUE(invalid(replace(remote_test::responseLine("k", "/a", kNow), R"("key":"k")", R"("key":"")")));
    EXPECT_TRUE(invalid(R"({"t":"agentRemoved","id":""})"));
}

TEST(RemoteDecoder, StringBounds) {
    EXPECT_FALSE(invalid(remote_test::agentLine("/" + std::string(4095, 'a'))));
    EXPECT_TRUE(invalid(remote_test::agentLine("/" + std::string(4096, 'a'))));
    EXPECT_TRUE(invalid(remote_test::responseLine(std::string(513, 'k'), "/a", kNow)));
    EXPECT_TRUE(invalid(replace(remote_test::agentLine("/a"), R"("project":"proj")", "\"project\":\"" + std::string(257, 'p') + "\"")));
    EXPECT_TRUE(invalid(replace(remote_test::agentLine("/a"), R"("model":"gpt-5")", "\"model\":\"" + std::string(257, 'm') + "\"")));
    EXPECT_TRUE(invalid(R"({"t":"error","code":"internal","detail":")" + std::string(201, 'd') + "\"}"));
    EXPECT_FALSE(invalid(R"({"t":"error","code":"internal","detail":")" + std::string(200, 'd') + "\"}"));
}

TEST(RemoteDecoder, ControlCharactersInIdsKeysAndProjects) {
    EXPECT_TRUE(invalid(remote_test::agentLine("/a\\u0000b")));
    EXPECT_TRUE(invalid(remote_test::agentLine("/a\\nb")));
    EXPECT_TRUE(invalid(remote_test::responseLine("k\\u007f", "/a", kNow)));
    EXPECT_TRUE(invalid(replace(remote_test::agentLine("/a"), R"("project":"proj")", R"("project":"a\tb")")));
    EXPECT_TRUE(invalid(R"({"t":"parents","links":{"/c\u0001":"/p"}})"));
    EXPECT_FALSE(invalid(remote_test::agentLine("/home/dev/项目/a b.jsonl")));
}

TEST(RemoteDecoder, ModelAndProviderCharacters) {
    const auto line = remote_test::agentLine("/a");
    EXPECT_FALSE(invalid(replace(line, R"("model":"gpt-5")", R"x("model":"Gemini 3.1 Pro (High)")x")));
    for (const auto* bad : {"\\u0085", "\\u2028", "\\u2029", "\\u202e", "\\u2066", "\\u200f", "\\u061c", "\\u009f"}) {
        EXPECT_TRUE(invalid(replace(line, R"("model":"gpt-5")", "\"model\":\"gpt" + std::string(bad) + "\""))) << bad;
    }
    EXPECT_TRUE(invalid(replace(remote_test::responseLine("k", "/a", kNow), R"("provider":null)", "\"provider\":\"open\\u202eai\"")));
}

TEST(RemoteDecoder, IntegersBeyond2To53AreInvalid) {
    EXPECT_FALSE(invalid(R"({"t":"historyDone","days":9007199254740992,"analyzed":0})"));
    EXPECT_TRUE(invalid(R"({"t":"historyDone","days":9007199254740993,"analyzed":0})"));
    EXPECT_TRUE(invalid(R"({"t":"historyDone","days":99999999999999999999999,"analyzed":0})"));
    EXPECT_TRUE(invalid(R"({"t":"historyDone","days":-1,"analyzed":0})"));
}

TEST(RemoteDecoder, TokenValuesAreClamped) {
    auto line = replace(remote_test::responseLine("k", "/a", kNow), R"("output":100)", R"("output":5e15)");
    line = replace(line, R"("input":1000)", R"("input":-7)");
    line = replace(line, R"("reasoning":10)", R"("reasoning":10.9)");
    const auto response = std::get<RemoteResponse>(decoded(line));
    EXPECT_EQ(response.record.tokens.output, TokenCounts::kMax);
    EXPECT_EQ(response.record.tokens.input, 0);
    EXPECT_EQ(response.record.tokens.reasoning, 10);
}

TEST(RemoteDecoder, TimesOutsideTheYears2000To2100AreInvalid) {
    EXPECT_TRUE(invalid(remote_test::responseLine("k", "/a", 946'684'799'999, 1, true, 946'684'700'000)));
    EXPECT_FALSE(invalid(remote_test::responseLine("k", "/a", 946'684'800'000 + 5000, 1, true, 946'684'800'000)));
    EXPECT_TRUE(invalid(remote_test::agentLine("/a", "idle", 4'133'980'800'000)));
    EXPECT_FALSE(invalid(remote_test::agentLine("/a", "idle", 4'133'980'799'999)));
    EXPECT_TRUE(invalid(remote_test::clockLine(1, 0)));
}

TEST(RemoteDecoder, ModeFactsFollowExecutionModes) {
    const auto line = remote_test::agentLine("/a");
    const auto with = [&](const std::string& fact) { return replace(line, R"("modes":[])", "\"modes\":[" + fact + "]"); };
    EXPECT_FALSE(invalid(with(R"({"kind":"serviceTier","value":"fast","rawValue":"priority","evidence":"served","provider":"openai","scope":"response"})")));
    EXPECT_TRUE(invalid(with(R"({"kind":"speed","value":"fast","rawValue":"fast","evidence":"served","provider":null,"scope":"response"})")));
    EXPECT_TRUE(invalid(with(R"({"kind":"effort","value":"high","rawValue":"hi gh","evidence":"recorded","provider":null,"scope":"turn"})")));
    EXPECT_TRUE(invalid(with(R"({"kind":"effort","value":"high","rawValue":"high","evidence":"guessed","provider":null,"scope":"turn"})")));
    EXPECT_FALSE(invalid(with(R"({"kind":"effort","value":"high","rawValue":"high","evidence":"recorded","scope":"turn"})")));
    std::string many;
    for (int i = 0; i < 21; ++i) many += std::string(i ? "," : "") + R"({"kind":"effort","value":"high","rawValue":"high","evidence":"recorded","provider":null,"scope":"turn"})";
    EXPECT_TRUE(invalid(with(many)));
}

TEST(RemoteDecoder, ActionLimitsAndDigests) {
    const auto action = [](const std::string& digest) {
        return R"({"tool":"Bash","digest":")" + digest + R"(","callMs":)" + std::to_string(kNow) + R"(,"resultMs":null,"failed":null,"model":null,"seeded":false})";
    };
    EXPECT_FALSE(invalid(remote_test::agentLine("/a", "idle", kNow, action("0123456789abcdef"))));
    EXPECT_TRUE(invalid(remote_test::agentLine("/a", "idle", kNow, action("0123456789ABCDEF"))));
    EXPECT_TRUE(invalid(remote_test::agentLine("/a", "idle", kNow, action("0123"))));
    std::string many;
    for (int i = 0; i < 65; ++i) many += (i ? "," : "") + action("0123456789abcdef");
    EXPECT_TRUE(invalid(remote_test::agentLine("/a", "idle", kNow, many)));
}

TEST(RemoteDecoder, HistoryViolations) {
    const auto day = [](const std::string& aggregate, const std::string& digest = "0123456789abcdef") {
        return R"({"t":"historyDay","day":"2026-05-27","source":"codex","aggregate":)" + aggregate + R"(,"digest":")" + digest + "\"}";
    };
    EXPECT_FALSE(invalid(day(kAggregate)));
    EXPECT_TRUE(invalid(day(kAggregate, "0123")));
    EXPECT_TRUE(invalid(day(replace(kAggregate, R"([3,50,40,2000,1234,0])", R"([3,50,40,2000,1234])"))));
    EXPECT_TRUE(invalid(day(replace(kAggregate, R"("output":50,)", ""))));
    EXPECT_TRUE(invalid(day(replace(kAggregate, R"("gpt-5":[)", "\"gpt\\u2028\":["))));
    EXPECT_FALSE(invalid(day(replace(kAggregate, R"(["gpt-5","effort",)", R"(["gpt-5","futureKind",)"))));
    EXPECT_TRUE(invalid(R"({"t":"historyPerf","day":"2026-05-27","source":"codex","model":"m","record":)" +
                        replace(record(), R"("latencyBins":[0,)", R"("latencyBins":[)") + "}"));
    EXPECT_TRUE(invalid(R"({"t":"historyPerf","day":"2026-05-27","source":"codex","model":"m","record":)" +
                        replace(record(), R"("toolLnSum":0.6931471805599453)", R"("toolLnSum":"x")") + "}"));
}

TEST(RemoteDecoder, HistoryDaysStayInsideTheYears2000To2100) {
    EXPECT_TRUE(isDay("2000-01-01"));
    EXPECT_TRUE(isDay("2100-12-31"));
    EXPECT_TRUE(isDay("2024-02-29"));
    EXPECT_FALSE(isDay("2023-02-29"));
    EXPECT_FALSE(isDay("2100-02-29"));
    EXPECT_FALSE(isDay("1999-12-31"));
    EXPECT_FALSE(isDay("2101-01-01"));
    EXPECT_FALSE(isDay("2026-13-01"));
    EXPECT_FALSE(isDay("2026-1-01"));
    EXPECT_TRUE(invalid(R"({"t":"historyDay","day":"1999-12-31","source":"codex","aggregate":)" + kAggregate + R"(,"digest":"0123456789abcdef"})"));
}

TEST(RemoteDecoder, FramerCapsLinesAt1MiB) {
    LineFramer framer;
    int oversized = 0;
    auto lines = framer.consume("{\"t\":\"ready\"}\r\n{\"t\":", oversized);
    ASSERT_EQ(lines.size(), 1u);
    EXPECT_EQ(lines[0], "{\"t\":\"ready\"}");
    lines = framer.consume("\"ready\"}\n\n", oversized);
    ASSERT_EQ(lines.size(), 1u);
    EXPECT_EQ(lines[0], "{\"t\":\"ready\"}");
    const std::string big(limits::kRelayLineBytes + 10, 'x');
    lines = framer.consume(big.substr(0, 600'000), oversized);
    lines = framer.consume(big.substr(600'000) + "\nok\n", oversized);
    EXPECT_EQ(oversized, 1);
    ASSERT_EQ(lines.size(), 1u);
    EXPECT_EQ(lines[0], "ok");
}

}  // namespace
