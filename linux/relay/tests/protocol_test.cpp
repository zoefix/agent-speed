#include <gtest/gtest.h>

#include <deque>

#include "agentspeed/json.hpp"
#include "json_out.hpp"
#include "protocol.hpp"

using namespace agentspeed;
using namespace agentspeed::relay;

namespace {

constexpr std::int64_t kNow = 1792490400000;

struct Parsed {
    std::string text;
    json::Document doc;
    explicit Parsed(std::string t) : text(std::move(t)) { ok = doc.parse(text); }
    json::Value root() const { return doc.root(); }
    bool ok = false;
};

AgentInfo agentInfo() {
    AgentInfo a;
    a.id = "/home/dev/.codex/sessions/rollout.jsonl";
    a.source = SourceKind::Codex;
    a.project = "rocket";
    a.model = "gpt-6.1-sol";
    a.phase = Phase::Generating;
    a.inFlightStartMs = kNow - 1000;
    a.lastLineMs = kNow;
    a.modes = ExecutionModes::of(native::effort("high", ModeEvidence::Recorded, ModeScope::Turn));
    return a;
}

ParsedResponse response() {
    ParsedResponse r;
    r.key = "resp-1";
    r.source = SourceKind::ClaudeCode;
    r.agentId = "/home/dev/.claude/projects/p/s.jsonl";
    r.model = "claude-opus-5-5";
    r.startMs = kNow - 4000;
    r.endMs = kNow;
    r.firstMs = kNow - 3000;
    r.tokens = {1000, 800, 100, 200, 50, 40};
    r.provider = "anthropic";
    r.fast = true;
    return r;
}

work::Action action(std::int64_t callMs) {
    work::Action a;
    a.callMs = callMs;
    a.resultMs = callMs + 500;
    a.tool = "exec_command";
    a.digest = "0123456789abcdef";
    a.failed = false;
    a.model = "gpt-6.1-sol";
    a.seeded = true;
    return a;
}

}  // namespace

TEST(RelayJson, ReplacesInvalidUtf8AndEscapesControls) {
    EXPECT_EQ(sanitizeUtf8("caf\xC3\xA9"), "caf\xC3\xA9");
    EXPECT_EQ(sanitizeUtf8("a\xFF" "b"), "a\xEF\xBF\xBD" "b");
    EXPECT_EQ(sanitizeUtf8("\xC0\xAF"), "\xEF\xBF\xBD\xEF\xBF\xBD");     // overlong
    EXPECT_EQ(sanitizeUtf8("\xED\xA0\x80"), "\xEF\xBF\xBD\xEF\xBF\xBD\xEF\xBF\xBD");  // surrogate
    EXPECT_EQ(sanitizeUtf8("\xF0\x9F\x98\x80"), "\xF0\x9F\x98\x80");
    EXPECT_EQ(sanitizeUtf8("\xE2\x82"), "\xEF\xBF\xBD\xEF\xBF\xBD");     // truncated
    JsonOut j;
    j.beginObject().key("s").string(std::string("a\"\\\n\x01\x7F\xFF", 7)).key("n").integer(-3).key("b").boolean(true);
    j.key("x").null().key("d").number(9.22029070282935).key("f").fraction(0.12345).key("arr").beginArray();
    j.integer(1).beginObject().endObject().beginArray().endArray().string("z").endArray().endObject();
    EXPECT_EQ(j.text(), R"({"s":"a\"\\\n\u0001\u007f)" "\xEF\xBF\xBD" R"(","n":-3,"b":true,"x":null,"d":9.22029070282935,"f":0.123,"arr":[1,{},[],"z"]})");
    Parsed parsed(j.text());
    EXPECT_TRUE(parsed.ok);
    JsonOut k;
    k.beginArray().number(0).number(1e300).fraction(1).fraction(0).fraction(2).endArray();
    EXPECT_EQ(k.text(), "[0,1e+300,1,0,1]");
}

TEST(RelayCommands, DecodesEveryCommandDefensively) {
    const auto start = decodeCommand(
        R"({"t":"start","protocol":1,"todayStartMs":1792454400000,"sources":["codex","claudeCode","newSource"],)"
        R"("zone":{"id":"Europe/Paris","tzif":"VFppZg=="},"prices":{"version":3,"models":[]},"extra":[1,2]})");
    ASSERT_TRUE(start);
    const auto& s = std::get<StartCommand>(*start);
    EXPECT_EQ(s.protocol, 1);
    EXPECT_EQ(s.todayStartMs, 1792454400000);
    EXPECT_EQ(s.sources, (std::set<SourceKind>{SourceKind::Codex, SourceKind::ClaudeCode}));
    EXPECT_EQ(s.zoneId, "Europe/Paris");
    EXPECT_EQ(s.zoneTzif, "VFppZg==");
    EXPECT_EQ(s.prices, R"({"version":3,"models":[]})");

    EXPECT_EQ(std::get<PingCommand>(*decodeCommand(R"({"t":"ping","id":7})")).id, 7);
    EXPECT_EQ(std::get<PingCommand>(*decodeCommand(R"({"id":-9007199254740992,"t":"ping"})")).id, -9007199254740992);
    EXPECT_EQ(std::get<TodayCommand>(*decodeCommand(R"({"t":"today","todayStartMs":1792540800000})")).todayStartMs, 1792540800000);
    EXPECT_EQ(std::get<PricesCommand>(*decodeCommand(R"({"t":"prices","table":{"a":1}})")).table, R"({"a":1})");
    EXPECT_EQ(std::get<ZoneCommand>(*decodeCommand(R"({"t":"zone","id":"UTC","tzif":""})")).id, "UTC");
    const auto history = decodeCommand(R"({"t":"history","digests":{"2026-10-20/codex":"0011223344556677","2026-10-21/codex":5}})");
    ASSERT_TRUE(history);
    EXPECT_EQ(std::get<HistoryCommand>(*history).digests, (std::map<std::string, std::string>{{"2026-10-20/codex", "0011223344556677"}}));
    EXPECT_TRUE(std::get<WatchCommand>(*decodeCommand(R"({"t":"watch","on":true})")).on);

    for (const char* bad : {
             "", "[]", "null", "not json", R"({"t":1})", R"({"t":"unknown"})", R"({"id":1})", R"({"t":"ping"})", R"({"t":"ping","id":"1"})",
             R"({"t":"ping","id":1.5})", R"({"t":"ping","id":9007199254740993})", R"({"t":"ping","id":1e3})",
             R"({"t":"today","todayStartMs":null})", R"({"t":"prices","table":[]})", R"({"t":"zone","id":"UTC"})",
             R"({"t":"history","digests":[]})", R"({"t":"watch","on":1})", R"({"t":"ping","id":1}{"t":"ping","id":2})",
             R"({"t":"start","protocol":1,"todayStartMs":1,"sources":["codex"],"zone":{"id":"UTC","tzif":""}})",
             R"({"t":"start","protocol":1,"todayStartMs":1,"sources":[1],"zone":{"id":"UTC","tzif":""},"prices":{}})",
             R"({"t":"start","protocol":1,"todayStartMs":1,"sources":["codex"],"zone":"UTC","prices":{}})",
         }) {
        EXPECT_FALSE(decodeCommand(bad)) << bad;
    }
    std::string deep = R"({"t":"watch","on":true,"x":)";
    for (int i = 0; i < 100; ++i) deep += "[";
    for (int i = 0; i < 100; ++i) deep += "]";
    EXPECT_FALSE(decodeCommand(deep + "}"));
    EXPECT_FALSE(decodeCommand(std::string(R"({"t":"watch","on":true,"pad":")") + std::string(kMaxInputLine, 'x') + "\"}"));
}

TEST(RelayLines, EncodesAnAgentWithinTheAppsBounds) {
    auto info = agentInfo();
    info.configuredModes = ExecutionModes::of(ModeFact::create(ModeKind::ServiceTier, "priority", ModeEvidence::Configured, "openai",
                                                               ModeScope::Configuration));
    std::deque<work::Action> actions;
    for (int i = 0; i < 70; ++i) actions.push_back(action(kNow - 100'000 + i * 1000));
    actions.push_back(action(10));  // a time the app would refuse
    actions.back().digest = "0123456789abcdef";
    Parsed line(agentLine({&info, std::string("/srv/work/rocket"), &actions}));
    ASSERT_TRUE(line.ok) << line.text;
    EXPECT_TRUE(line.root()["t"].equals("agent"));
    const auto a = line.root()["a"];
    EXPECT_TRUE(a["id"].equals(info.id));
    EXPECT_TRUE(a["source"].equals("codex"));
    EXPECT_TRUE(a["project"].equals("rocket"));
    EXPECT_TRUE(a["cwd"].equals("/srv/work/rocket"));
    EXPECT_TRUE(a["phase"].equals("generating"));
    EXPECT_EQ(a["inFlightStartMs"].truncated(), kNow - 1000);
    EXPECT_EQ(a["modes"].size(), 1u);
    EXPECT_TRUE(a["modes"].at(0)["kind"].equals("effort"));
    EXPECT_TRUE(a["modes"].at(0)["evidence"].equals("recorded"));
    EXPECT_TRUE(a["modes"].at(0)["scope"].equals("turn"));
    EXPECT_TRUE(a["modes"].at(0)["provider"].isNull());
    EXPECT_TRUE(a["configuredModes"].at(0)["value"].equals("fast"));
    EXPECT_TRUE(a["configuredModes"].at(0)["rawValue"].equals("priority"));
    EXPECT_TRUE(a["configuredModes"].at(0)["provider"].equals("openai"));
    EXPECT_EQ(a["sessionModes"].size(), 0u);
    // The last 64 actions the app accepts, in order.
    ASSERT_EQ(a["actions"].size(), 64u);
    EXPECT_EQ(a["actions"].at(0)["callMs"].truncated(), kNow - 100'000 + 6 * 1000);
    EXPECT_EQ(a["actions"].at(63)["callMs"].truncated(), kNow - 100'000 + 69 * 1000);
    EXPECT_TRUE(a["actions"].at(0)["seeded"].isTrue());
    EXPECT_TRUE(a["actions"].at(0)["failed"].isBool());

    auto odd = agentInfo();
    odd.project = std::string(300, 'p');
    odd.model = std::string("gpt\n");
    odd.lastLineMs = 253402387139999;
    Parsed bounded(agentLine({&odd, std::string(5000, 'c'), nullptr}));
    ASSERT_TRUE(bounded.ok);
    EXPECT_TRUE(bounded.root()["a"]["project"].isNull());
    EXPECT_TRUE(bounded.root()["a"]["model"].isNull());
    EXPECT_TRUE(bounded.root()["a"]["cwd"].isNull());
    EXPECT_TRUE(bounded.root()["a"]["lastLineMs"].isNull());
    EXPECT_EQ(bounded.root()["a"]["actions"].size(), 0u);

    auto bad = agentInfo();
    bad.id = "/tmp/line\nbreak.jsonl";
    EXPECT_EQ(agentLine({&bad, std::nullopt, nullptr}), "");
    bad.id = std::string(4097, 'x');
    EXPECT_EQ(agentLine({&bad, std::nullopt, nullptr}), "");

    Parsed removed(agentRemovedLine(info.id));
    EXPECT_TRUE(removed.root()["t"].equals("agentRemoved"));
    EXPECT_TRUE(removed.root()["id"].equals(info.id));
}

TEST(RelayLines, EncodesResponsesToolsAndParents) {
    Parsed line(responseLine(response(), true, std::string("orbit")));
    ASSERT_TRUE(line.ok) << line.text;
    const auto r = line.root()["r"];
    EXPECT_TRUE(r["key"].equals("resp-1"));
    EXPECT_TRUE(r["source"].equals("claudeCode"));
    EXPECT_TRUE(r["agent"].equals("/home/dev/.claude/projects/p/s.jsonl"));
    EXPECT_EQ(r["startMs"].truncated(), kNow - 4000);
    EXPECT_EQ(r["endMs"].truncated(), kNow);
    EXPECT_EQ(r["firstMs"].truncated(), kNow - 3000);
    EXPECT_EQ(r["input"].truncated(), 1000);
    EXPECT_EQ(r["cachedInput"].truncated(), 800);
    EXPECT_EQ(r["cacheWriteInput"].truncated(), 100);
    EXPECT_EQ(r["output"].truncated(), 200);
    EXPECT_EQ(r["reasoning"].truncated(), 50);
    EXPECT_EQ(r["cacheWrite1hInput"].truncated(), 40);
    EXPECT_EQ(r["requestCount"].truncated(), 1);
    EXPECT_TRUE(r["final"].isTrue());
    EXPECT_TRUE(r["fast"].isTrue());
    EXPECT_TRUE(r["provider"].equals("anthropic"));
    EXPECT_EQ(r["modes"].size(), 0u);
    EXPECT_FALSE(r["cost"].valid());
    EXPECT_TRUE(line.root()["completion"].isTrue());
    EXPECT_TRUE(line.root()["project"].equals("orbit"));

    auto other = response();
    other.startMs = std::nullopt;
    other.firstMs = 5;
    other.requestCount = 250'000;
    other.provider = std::string(65, 'p');
    Parsed second(responseLine(other, false, std::nullopt));
    ASSERT_TRUE(second.ok);
    EXPECT_TRUE(second.root()["r"]["startMs"].isNull());
    EXPECT_TRUE(second.root()["r"]["firstMs"].isNull());
    EXPECT_EQ(second.root()["r"]["requestCount"].truncated(), 100'000);
    EXPECT_TRUE(second.root()["r"]["provider"].isNull());
    EXPECT_TRUE(second.root()["project"].equals(""));
    EXPECT_FALSE(second.root()["completion"].isTrue());

    auto old = response();
    old.endMs = -62167219199000;
    EXPECT_EQ(responseLine(old, true, std::nullopt), "");
    auto longKey = response();
    longKey.key = std::string(513, 'k');
    EXPECT_EQ(responseLine(longKey, true, std::nullopt), "");

    Parsed tool(toolLine("/a/b.jsonl", SourceKind::Codex, action(kNow)));
    ASSERT_TRUE(tool.ok);
    EXPECT_TRUE(tool.root()["t"].equals("tool"));
    EXPECT_TRUE(tool.root()["agent"].equals("/a/b.jsonl"));
    EXPECT_TRUE(tool.root()["source"].equals("codex"));
    EXPECT_EQ(tool.root()["callMs"].truncated(), kNow);
    EXPECT_EQ(tool.root()["resultMs"].truncated(), kNow + 500);
    EXPECT_FALSE(tool.root()["failed"].isTrue());
    EXPECT_TRUE(tool.root()["model"].equals("gpt-6.1-sol"));
    EXPECT_TRUE(tool.root()["seeded"].isTrue());
    auto unknown = action(kNow);
    unknown.failed = std::nullopt;
    unknown.model = std::nullopt;
    Parsed toolUnknown(toolLine("/a/b.jsonl", SourceKind::Codex, unknown));
    EXPECT_TRUE(toolUnknown.root()["failed"].isNull());
    EXPECT_TRUE(toolUnknown.root()["model"].isNull());

    Parsed parents(parentsLine({{"/a/child.jsonl", "/a/parent.jsonl"}, {"/bad\x01", "/a/parent.jsonl"}}));
    ASSERT_TRUE(parents.ok);
    EXPECT_EQ(parents.root()["links"].members().size(), 1u);
    EXPECT_TRUE(parents.root()["links"]["/a/child.jsonl"].equals("/a/parent.jsonl"));
}

TEST(RelayLines, EncodesTheSmallLines) {
    Parsed hello(helloLine("1.6.1", "x86_64", kNow));
    EXPECT_EQ(hello.text, R"({"t":"hello","protocol":1,"version":"1.6.1","arch":"x86_64","now":1792490400000})");
    EXPECT_EQ(progressLine(0.5), R"({"t":"progress","value":0.5})");
    EXPECT_EQ(readyLine(), R"({"t":"ready"})");
    EXPECT_EQ(clockLine(3, kNow), R"({"t":"clock","id":3,"now":1792490400000})");
    EXPECT_EQ(historyDoneLine(2, 40), R"({"t":"historyDone","days":2,"analyzed":40})");
    EXPECT_EQ(historyProgressLine(1), R"({"t":"historyProgress","value":1})");
    EXPECT_EQ(historyDayLine("2026-10-20", SourceKind::Codex, R"({"requests":1})", "0011223344556677"),
              R"({"t":"historyDay","day":"2026-10-20","source":"codex","aggregate":{"requests":1},"digest":"0011223344556677"})");
    EXPECT_EQ(historyPerfLine("2026-10-20", SourceKind::Codex, "gpt-5.5", R"({"n":1})"),
              R"({"t":"historyPerf","day":"2026-10-20","source":"codex","model":"gpt-5.5","record":{"n":1}})");
    const auto error = errorLine("badStart", std::string(300, 'x'));
    Parsed parsed(error);
    EXPECT_EQ(parsed.root()["detail"].string()->size(), 200u);

    StatusView status;
    status.sources.push_back({SourceKind::Codex, true, 3, ExecutionModes()});
    status.sources.push_back({SourceKind::OpenCode, false, 0, ExecutionModes()});
    status.warnings.malformedLines = 4;
    status.warnings.unreadableFiles = 1;
    status.warnings.partialHistory = true;
    status.warnings.unsupported = {SourceKind::OpenCode};
    EXPECT_EQ(statusLine(status),
              R"({"t":"status","sources":[{"source":"codex","available":true,"sessions":3,"unsupported":false,"configuredModes":[]},)"
              R"({"source":"opencode","available":false,"sessions":0,"unsupported":true,"configuredModes":[]}],)"
              R"("malformed":4,"unreadable":1,"partialHistory":true})");
}
