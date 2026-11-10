#include <gtest/gtest.h>

#include <algorithm>
#include <chrono>
#include <ctime>
#include <fstream>
#include <map>
#include <memory>
#include <set>

#include "agentspeed/history_store.hpp"
#include "agentspeed/modes.hpp"
#include "fake_home.hpp"
#include "fixture_support.hpp"
#include "history_sync.hpp"
#include "protocol.hpp"
#include "relay_process.hpp"
#include "zone_file.hpp"

// REMOTE 10: `agent-speed-relay serve` over spec/fixtures as a fake home, checked against parser-expected.json and
// history-expected.json, then the digest sync, live lines and the end of input.
using namespace agentspeed;
using namespace relay_test;
namespace fs = std::filesystem;

namespace {

constexpr std::int64_t kDayStart = 1792454400000;  // 2026-10-20T00:00:00Z
constexpr auto kLong = std::chrono::seconds(120);

// A received line kept with its parsed document.
struct Line {
    std::string text;
    json::Document doc;
    explicit Line(std::string t) : text(std::move(t)) { ok = doc.parse(text); }
    json::Value root() const { return doc.root(); }
    std::string type() const { return root()["t"].string().value_or(""); }
    bool ok = false;
};
using LinePtr = std::shared_ptr<Line>;

LinePtr next(RelayProcess& relay, std::chrono::milliseconds timeout) {
    const auto text = relay.readLine(timeout);
    if (!text) return nullptr;
    auto line = std::make_shared<Line>(*text);
    EXPECT_TRUE(line->ok) << *text;
    EXPECT_LE(text->size(), relay::kMaxOutputLine);
    return line;
}

std::string base64(const std::string& bytes) {
    static constexpr char kAlphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    std::size_t i = 0;
    for (; i + 2 < bytes.size(); i += 3) {
        const auto v = (static_cast<unsigned char>(bytes[i]) << 16) | (static_cast<unsigned char>(bytes[i + 1]) << 8) |
                       static_cast<unsigned char>(bytes[i + 2]);
        out += kAlphabet[(v >> 18) & 63];
        out += kAlphabet[(v >> 12) & 63];
        out += kAlphabet[(v >> 6) & 63];
        out += kAlphabet[v & 63];
    }
    if (i + 1 == bytes.size()) {
        const auto v = static_cast<unsigned char>(bytes[i]) << 16;
        out += kAlphabet[(v >> 18) & 63];
        out += kAlphabet[(v >> 12) & 63];
        out += "==";
    } else if (i + 2 == bytes.size()) {
        const auto v = (static_cast<unsigned char>(bytes[i]) << 16) | (static_cast<unsigned char>(bytes[i + 1]) << 8);
        out += kAlphabet[(v >> 18) & 63];
        out += kAlphabet[(v >> 12) & 63];
        out += kAlphabet[(v >> 6) & 63];
        out += '=';
    }
    return out;
}

std::optional<std::int64_t> inRange(const json::Value& v) {
    const auto ms = fixtures::optInt(v);
    return ms && relay::validTime(*ms) ? ms : std::nullopt;
}

template <typename E>
E enumOf(std::string_view name, std::initializer_list<E> all, std::string_view (*nameOf)(E)) {
    for (const auto e : all) {
        if (nameOf(e) == name) return e;
    }
    ADD_FAILURE() << "unknown name " << name;
    return *all.begin();
}

ExecutionModes modesOf(const json::Value& facts) {
    std::vector<ModeFact> out;
    for (const auto& f : facts.elements()) {
        ModeFact fact;
        fact.kind = enumOf(*f["kind"].string(), {ModeKind::ServiceTier, ModeKind::Accelerator, ModeKind::ModelVariant, ModeKind::Routing,
                                                   ModeKind::Workflow, ModeKind::Effort},
                           modeKindName);
        fact.value = *f["value"].string();
        fact.raw = *f["rawValue"].string();
        fact.evidence = enumOf(*f["evidence"].string(),
                               {ModeEvidence::Configured, ModeEvidence::Selected, ModeEvidence::Recorded, ModeEvidence::Served},
                               modeEvidenceName);
        fact.provider = fixtures::optString(f["provider"]);
        fact.scope = enumOf(*f["scope"].string(),
                            {ModeScope::Configuration, ModeScope::Session, ModeScope::Turn, ModeScope::Request, ModeScope::Response},
                            modeScopeName);
        out.push_back(fact);
    }
    return ExecutionModes(out);
}

// What the app has received: the latest state of every agent and response, the latest parent map.
struct Received {
    LinePtr hello;
    std::map<std::string, LinePtr> agents;
    std::map<std::string, LinePtr> responses;
    std::map<std::string, bool> firstCompletion;
    std::map<std::string, std::string> parents;
    std::vector<LinePtr> statuses, tools;
    int progress = 0;
    std::set<std::string> removed;

    void take(const LinePtr& line) {
        const auto t = line->type();
        const auto root = line->root();
        if (t == "hello") {
            hello = line;
        } else if (t == "progress") {
            ++progress;
        } else if (t == "agent") {
            const auto id = *root["a"]["id"].string();
            agents[id] = line;
            removed.erase(id);
        } else if (t == "agentRemoved") {
            agents.erase(*root["id"].string());
            removed.insert(*root["id"].string());
        } else if (t == "parents") {
            parents.clear();
            for (const auto& [child, parent] : root["links"].members()) parents[child] = *parent.string();
        } else if (t == "response") {
            const auto key = *root["r"]["key"].string();
            if (!responses.contains(key)) firstCompletion[key] = root["completion"].isTrue();
            responses[key] = line;
        } else if (t == "status") {
            statuses.push_back(line);
        } else if (t == "tool") {
            tools.push_back(line);
        }
    }
};

struct Expected {
    std::string id;
    json::Value want;
};

// Every agent parser-expected.json describes, by the id the relay gives it in the fake home.
std::vector<Expected> expectedAgents(const json::Value& root, const FakeHome& home, std::set<std::string>& absent) {
    std::vector<Expected> out;
    for (const auto& [relative, want] : root["files"].members()) out.push_back({*home.toHome(relative), want});
    for (const auto& [id, want] : root["opencode"]["agents"].members()) out.push_back({id, want});
    for (const auto& [relative, want] : root["antigravity"]["databases"].members()) {
        if (want["unsupported"].isTrue()) {
            absent.insert(*home.toHome(relative));
        } else {
            out.push_back({*home.toHome(relative), want});
        }
    }
    for (const auto& [database, agents] : root["openclaw"]["databases"].members()) {
        if (agents["unsupported"].isTrue()) continue;
        for (const auto& [relative, want] : agents.members()) out.push_back({*home.toHome(relative), want});
    }
    for (const auto& [folder, set] : root["hermes"]["sets"].members()) {
        for (const auto& [session, want] : set["agents"].members()) out.push_back({*home.toHome(folder) + "#" + session, want});
    }
    return out;
}

void expectResponse(const json::Value& got, const json::Value& want) {
    EXPECT_EQ(fixtures::optInt(got["startMs"]), inRange(want["startMs"]));
    EXPECT_EQ(fixtures::optInt(got["endMs"]), fixtures::optInt(want["endMs"]));
    EXPECT_EQ(fixtures::optInt(got["firstMs"]), inRange(want["firstMs"]));
    for (const char* field : {"input", "cachedInput", "cacheWriteInput", "cacheWrite1hInput", "output", "reasoning"}) {
        EXPECT_EQ(fixtures::optInt(got[field]), fixtures::optInt(want[field])) << field;
    }
    EXPECT_EQ(fixtures::optString(got["model"]), fixtures::optString(want["model"]));
    EXPECT_EQ(fixtures::optString(got["provider"]), fixtures::optString(want["provider"]));
    EXPECT_EQ(got["fast"].isTrue(), want["fast"].isTrue());
    EXPECT_EQ(got["final"].isTrue(), want["final"].isTrue());
    if (want["requestCount"].valid()) {
        EXPECT_EQ(fixtures::optInt(got["requestCount"]), fixtures::optInt(want["requestCount"]));
    }
}

void expectAgent(const Expected& e, const Received& received, const FakeHome& home) {
    SCOPED_TRACE(home.toFixture(e.id));
    const auto& want = e.want;
    const auto agent = received.agents.find(e.id);
    ASSERT_NE(agent, received.agents.end());
    const auto a = agent->second->root()["a"];
    EXPECT_EQ(*a["source"].string(), *want["source"].string());
    EXPECT_EQ(fixtures::optString(a["project"]), fixtures::optString(want["project"]));
    EXPECT_EQ(fixtures::optString(a["model"]), fixtures::optString(want["model"]));
    EXPECT_EQ(a["subagent"].isTrue(), want["isSubagent"].isTrue());
    EXPECT_EQ(*a["phase"].string(), *want["phase"].string());
    EXPECT_EQ(fixtures::optInt(a["inFlightStartMs"]), inRange(want["inFlightStartMs"]));
    EXPECT_EQ(fixtures::optInt(a["lastLineMs"]), inRange(want["lastLineMs"]));
    const auto parentWant = want["parentFile"].valid() ? want["parentFile"] : want["parentId"];
    if (parentWant.valid()) {
        const auto parent = received.parents.find(e.id);
        const auto got = parent == received.parents.end() ? std::nullopt : std::optional<std::string>(home.toFixture(parent->second));
        EXPECT_EQ(got, fixtures::optString(parentWant)) << "parent";
    }
    fixtures::expectCurrent(modesOf(a["modes"]), modesOf(a["sessionModes"]), modesOf(a["configuredModes"]), want);
    if (want["actions"].valid()) {
        const auto got = a["actions"].elements();
        const auto expected = want["actions"].elements();
        ASSERT_EQ(got.size(), std::min<std::size_t>(expected.size(), 64));
        const auto offset = expected.size() - got.size();
        for (std::size_t k = 0; k < got.size(); ++k) {
            const auto& g = got[k];
            const auto& w = expected[offset + k];
            EXPECT_EQ(*g["tool"].string(), *w["tool"].string());
            EXPECT_EQ(*g["digest"].string(), *w["digest"].string());
            EXPECT_EQ(fixtures::optInt(g["callMs"]), fixtures::optInt(w["callMs"]));
            EXPECT_EQ(fixtures::optInt(g["resultMs"]), fixtures::optInt(w["resultMs"]));
            EXPECT_EQ(g["failed"].isNull() ? std::nullopt : std::optional<bool>(g["failed"].isTrue()),
                      w["failed"].isNull() ? std::nullopt : std::optional<bool>(w["failed"].isTrue()));
            EXPECT_EQ(fixtures::optString(g["model"]), fixtures::optString(w["model"]));
            EXPECT_TRUE(g["seeded"].isTrue());
        }
    }
    // The agent's responses: exactly the expected ones the app accepts (5.6: times 2000–2100).
    std::map<std::string, json::Value> wanted;
    for (const auto& r : want["responses"].elements()) {
        if (inRange(r["endMs"])) wanted[*r["key"].string()] = r;
    }
    std::set<std::string> got;
    for (const auto& [key, line] : received.responses) {
        if (line->root()["r"]["agent"].equals(e.id)) got.insert(key);
    }
    std::set<std::string> wantKeys;
    for (const auto& [key, r] : wanted) wantKeys.insert(key);
    EXPECT_EQ(got, wantKeys);
    for (const auto& [key, r] : wanted) {
        const auto line = received.responses.find(key);
        if (line == received.responses.end()) continue;
        SCOPED_TRACE(key);
        const auto root = line->second->root();
        expectResponse(root["r"], r);
        EXPECT_TRUE(received.firstCompletion.at(key));
        EXPECT_EQ(*root["project"].string(), fixtures::optString(want["project"]).value_or(""));
        if (want["responseModes"][key].valid()) fixtures::expectModes(modesOf(root["r"]["modes"]), want["responseModes"][key]);
    }
}

void expectDay(const json::Value& got, const json::Value& want) {
    for (const char* field : {"requests", "input", "cachedInput", "cacheWriteInput", "output", "reasoning", "measuredOutput", "measuredMs",
                              "unfinalized", "agents", "costMicroUsd", "unpricedRequests"}) {
        EXPECT_EQ(fixtures::optInt(got[field]), fixtures::optInt(want[field])) << field;
    }
    std::map<std::string, json::Value> models;
    for (const auto& [name, m] : got["models"].members()) models[name] = m;
    ASSERT_EQ(models.size(), want["models"].members().size());
    for (const auto& [name, w] : want["models"].members()) {
        SCOPED_TRACE("model " + name);
        ASSERT_TRUE(models.contains(name));
        const auto& m = models[name];
        EXPECT_EQ(fixtures::optInt(m.at(0)), fixtures::optInt(w["requests"]));
        EXPECT_EQ(fixtures::optInt(m.at(1)), fixtures::optInt(w["output"]));
        EXPECT_EQ(fixtures::optInt(m.at(2)), fixtures::optInt(w["measuredOutput"]));
        EXPECT_EQ(fixtures::optInt(m.at(3)), fixtures::optInt(w["measuredMs"]));
        EXPECT_EQ(fixtures::optInt(m.at(4)), fixtures::optInt(w["costMicroUsd"]));
        EXPECT_EQ(fixtures::optInt(m.at(5)), fixtures::optInt(w["unpricedRequests"]));
    }
    const auto unpriced = got["unpriced"].elements();
    const auto expected = want["unpriced"].elements();
    ASSERT_EQ(unpriced.size(), expected.size());
    for (std::size_t k = 0; k < unpriced.size(); ++k) {
        EXPECT_EQ(fixtures::optString(unpriced[k].at(0)), fixtures::optString(expected[k]["model"]));
        EXPECT_EQ(fixtures::optString(unpriced[k].at(1)), fixtures::optString(expected[k]["provider"]));
        EXPECT_EQ(unpriced[k].at(2).isTrue(), expected[k]["fast"].isTrue());
        EXPECT_EQ(fixtures::optInt(unpriced[k].at(4)), fixtures::optInt(expected[k]["requests"]));
    }
}

void expectRecord(const json::Value& got, const json::Value& want) {
    for (const char* field : {"n", "outputSum", "durationMsSum", "reasonN", "reasonOutput", "reasonDurationMsSum", "cacheN", "cacheInput",
                              "cacheCached", "toolN", "toolFailed", "activeMs"}) {
        EXPECT_EQ(fixtures::optInt(got[field]), fixtures::optInt(want[field])) << field;
    }
    ASSERT_EQ(got["latencyBins"].size(), 64u);
    for (std::uint32_t k = 0; k < 64; ++k) EXPECT_EQ(fixtures::optInt(got["latencyBins"].at(k)), fixtures::optInt(want["latencyBins"].at(k))) << k;
    EXPECT_NEAR(*got["toolLnSum"].number(), *want["toolLnSum"].number(), 1e-9);
}

// One history answer: the days and records sent, until `historyDone`.
struct HistoryAnswer {
    std::vector<LinePtr> days;
    std::map<std::string, std::vector<LinePtr>> perf;  // "<day>/<source>" → records in order
    LinePtr done;
};

HistoryAnswer readHistory(RelayProcess& relay) {
    HistoryAnswer answer;
    std::string current;
    while (auto line = next(relay, kLong)) {
        const auto t = line->type();
        if (t == "historyDay") {
            current = *line->root()["day"].string() + "/" + *line->root()["source"].string();
            answer.days.push_back(line);
        } else if (t == "historyPerf") {
            const auto key = *line->root()["day"].string() + "/" + *line->root()["source"].string();
            EXPECT_EQ(key, current) << "a record follows its day";
            answer.perf[key].push_back(line);
        } else if (t == "historyDone") {
            answer.done = line;
            break;
        } else if (t == "error") {
            ADD_FAILURE() << line->text;
            break;
        }
    }
    return answer;
}

std::string isoOf(std::int64_t ms) {
    const std::time_t seconds = static_cast<std::time_t>(ms / 1000);
    std::tm tm{};
    gmtime_r(&seconds, &tm);
    char buffer[40];
    std::strftime(buffer, sizeof buffer, "%Y-%m-%dT%H:%M:%S", &tm);
    char out[48];
    std::snprintf(out, sizeof out, "%s.%03dZ", buffer, static_cast<int>(ms % 1000));
    return out;
}

std::string allSourcesJson() {
    std::string out = "[";
    for (const auto kind : MonitorConfig::allSources()) {
        if (out.size() > 1) out += ",";
        out += "\"" + std::string(sourceId(kind)) + "\"";
    }
    return out + ",\"someFutureSource\"]";
}

}  // namespace

TEST(RelayEndToEnd, ServesTheFixturesAsTheDesktopReadsThem) {
    const auto temp = makeTempDir("agentspeed-relay");
    const FakeHome home(temp / "home");
    const auto folder = temp / "relay";
    fs::create_directories(folder);

    fixtures::JsonFile parsers(fixtures::fixtureDir() / "parser-expected.json");
    fixtures::JsonFile history(fixtures::fixtureDir() / "history-expected.json");
    ASSERT_TRUE(parsers.ok && history.ok);
    // history-expected.json's prices: the newest table is the app's, the older one is bound to its day in the store.
    const auto tables = history.root()["prices"]["tables"].elements();
    ASSERT_EQ(tables.size(), 2u);
    const auto older = fixtures::readText(fixtures::fixtureDir() / *tables[0].string());
    // One line on the wire: the table's JSON without its formatting.
    const fixtures::JsonFile currentTable(fixtures::fixtureDir() / *tables[1].string());
    ASSERT_TRUE(currentTable.ok);
    const auto current = fixtures::compactJson(currentTable.root());
    {
        auto store = history::Store::open((folder / "history.sqlite").string());
        std::map<std::string, int> bindings;
        for (const auto& [day, version] : history.root()["prices"]["bindings"].members()) bindings[day] = static_cast<int>(*fixtures::optInt(version));
        store->savePriceBindings(bindings, [&](int version) { return version == 1 ? older : std::string(); });
    }
    const auto utc = fixtures::readText("/usr/share/zoneinfo/UTC");
    ASSERT_FALSE(utc.empty()) << "tzdata is needed for the zone of the start line";

    RelayProcess relay(AGENTSPEED_RELAY_BINARY, {"serve", "--folder", folder.string()},
                       {"HOME=" + home.path(), "TZ=" + *parsers.root()["hermes"]["timeZone"].string(), "LC_ALL=C"});
    const auto start = std::string(R"({"t":"start","protocol":1,"todayStartMs":)") + std::to_string(kDayStart) + R"(,"sources":)" +
                       allSourcesJson() + R"(,"zone":{"id":"UTC","tzif":")" + base64(utc) + R"("},"prices":)" + current + R"(,"later":1})";
    ASSERT_TRUE(relay.send(start));

    // Seeding: hello first, then the backlog, then ready and the first status.
    Received received;
    const auto hello = next(relay, kLong);
    ASSERT_TRUE(hello);
    ASSERT_EQ(hello->type(), "hello");
    EXPECT_EQ(fixtures::optInt(hello->root()["protocol"]), 1);
    EXPECT_TRUE(hello->root()["version"].isString());
    EXPECT_TRUE(hello->root()["arch"].equals("x86_64") || hello->root()["arch"].equals("aarch64"));
    bool ready = false;
    while (auto line = next(relay, kLong)) {
        if (line->type() == "ready") {
            ready = true;
            break;
        }
        EXPECT_NE(line->type(), "status") << "status comes after ready";
        received.take(line);
    }
    ASSERT_TRUE(ready);
    const auto status = next(relay, std::chrono::seconds(10));
    ASSERT_TRUE(status);
    ASSERT_EQ(status->type(), "status");
    const auto sources = status->root()["sources"].elements();
    EXPECT_EQ(sources.size(), MonitorConfig::allSources().size());
    for (const auto& s : sources) {
        const auto id = *s["source"].string();
        // The harness, Devin and Qoder IDE fixtures are not laid out in the fake home.
        const bool fixtureSource = id != "zcode" && id != "kimi" && id != "grokBuild" && id != "devin" && id != "qoder";
        EXPECT_EQ(s["available"].isTrue(), fixtureSource) << id;
        if (id == "antigravity") {
            EXPECT_TRUE(s["unsupported"].isTrue());
        }
    }
    EXPECT_GT(*fixtures::optInt(status->root()["malformed"]), 0);

    std::set<std::string> absent;
    const auto expected = expectedAgents(parsers.root(), home, absent);
    EXPECT_GT(expected.size(), 70u);
    std::set<std::string> expectedKeys;
    for (const auto& e : expected) {
        expectAgent(e, received, home);
        for (const auto& r : e.want["responses"].elements()) {
            if (inRange(r["endMs"])) expectedKeys.insert(*r["key"].string());
        }
    }
    for (const auto& id : absent) EXPECT_FALSE(received.agents.contains(id)) << home.toFixture(id);
    std::set<std::string> sentKeys;
    for (const auto& [key, line] : received.responses) sentKeys.insert(key);
    EXPECT_EQ(sentKeys, expectedKeys);
    EXPECT_GT(received.progress, 0);

    // A ping is answered with the host's clock.
    ASSERT_TRUE(relay.send(R"({"t":"ping","id":42})"));
    LinePtr clock;
    while (auto line = next(relay, std::chrono::seconds(10))) {
        if (line->type() == "clock") {
            clock = line;
            break;
        }
        received.take(line);
    }
    ASSERT_TRUE(clock);
    EXPECT_EQ(fixtures::optInt(clock->root()["id"]), 42);
    EXPECT_TRUE(relay::validTime(*fixtures::optInt(clock->root()["now"])));

    // History (8.2): every day goes out for an empty request, exactly as history-expected.json says.
    ASSERT_TRUE(relay.send(R"({"t":"history","digests":{}})"));
    const auto answer = readHistory(relay);
    ASSERT_TRUE(answer.done);
    EXPECT_EQ(fixtures::optInt(answer.done->root()["days"]), static_cast<std::int64_t>(answer.days.size()));
    EXPECT_GT(*fixtures::optInt(answer.done->root()["analyzed"]), 0);
    std::map<std::string, LinePtr> days;
    for (const auto& d : answer.days) days[*d->root()["day"].string() + "/" + *d->root()["source"].string()] = d;
    const auto rows = history.root()["days"].elements();
    EXPECT_EQ(days.size(), rows.size());
    for (const auto& row : rows) {
        const auto key = *row["date"].string() + "/" + *row["source"].string();
        SCOPED_TRACE(key);
        ASSERT_TRUE(days.contains(key));
        expectDay(days[key]->root()["aggregate"], row);
    }
    std::map<std::string, json::Value> records;
    for (const auto& [key, lines] : answer.perf) {
        for (const auto& line : lines) records[key + "/" + *line->root()["model"].string()] = line->root()["record"];
    }
    const auto perfRows = history.root()["perf"].elements();
    EXPECT_EQ(records.size(), perfRows.size());
    for (const auto& row : perfRows) {
        const auto key = *row["day"].string() + "/" + *row["source"].string() + "/" + *row["model"].string();
        SCOPED_TRACE(key);
        ASSERT_TRUE(records.contains(key));
        expectRecord(records[key], row);
    }
    // The digest covers the aggregate's exact text and its records' texts.
    std::string digests = "{";
    for (const auto& [key, line] : days) {
        std::vector<std::pair<std::string, std::string>> perf;
        if (const auto it = answer.perf.find(key); it != answer.perf.end()) {
            for (const auto& p : it->second) perf.emplace_back(*p->root()["model"].string(), std::string(p->root()["record"].raw()));
        }
        EXPECT_TRUE(std::is_sorted(perf.begin(), perf.end(), [](const auto& a, const auto& b) { return a.first < b.first; })) << key;
        const auto digest = *line->root()["digest"].string();
        EXPECT_EQ(digest, relay::digestOf(line->root()["aggregate"].raw(), perf)) << key;
        if (digests.size() > 1) digests += ",";
        digests += "\"" + key + "\":\"" + digest + "\"";
    }
    digests += "}";

    // The digests it returned: nothing to send.
    ASSERT_TRUE(relay.send(R"({"t":"history","digests":)" + digests + "}"));
    const auto again = readHistory(relay);
    ASSERT_TRUE(again.done);
    EXPECT_TRUE(again.days.empty());
    EXPECT_EQ(fixtures::optInt(again.done->root()["days"]), 0);

    // Live (5.5): a session written now is a live response with its completion, its agent and, with the watch on,
    // its tool call.
    ASSERT_TRUE(relay.send(R"({"t":"watch","on":true})"));
    const auto now = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
    const auto live = home.path() + "/.codex/sessions/2026/10/20/rollout-2026-10-20T23-00-00-live.jsonl";
    {
        std::ofstream out(live);
        out << R"({"timestamp":")" << isoOf(now - 5000)
            << R"(","type":"session_meta","payload":{"id":"thread-live","timestamp":")" << isoOf(now - 5000)
            << R"(","cwd":"/srv/work/live-project","originator":"codex_cli_rs","cli_version":"0.170.0","source":"cli","model_provider":"openai"}})" << "\n";
        out << R"({"timestamp":")" << isoOf(now - 4900) << R"(","type":"turn_context","payload":{"cwd":"/srv/work/live-project","model":"gpt-6.1-sol"}})" << "\n";
        out << R"({"timestamp":")" << isoOf(now - 4000)
            << R"(","type":"response_item","payload":{"type":"function_call","name":"exec_command","arguments":"{\"cmd\":\"make\"}","call_id":"call-live"}})" << "\n";
        out << R"({"timestamp":")" << isoOf(now - 3990)
            << R"(","type":"token_usage_record","payload":{"thread_id":"thread-live","response_id":"resp-live-1","usage":{"input_tokens":900,"cached_input_tokens":100,"output_tokens":120,"reasoning_output_tokens":10,"total_tokens":1020}}})" << "\n";
        out << R"({"timestamp":")" << isoOf(now - 2000)
            << R"(","type":"response_item","payload":{"type":"function_call_output","call_id":"call-live","output":"{\"exit_code\":0}"}})" << "\n";
    }
    LinePtr liveResponse, liveTool;
    bool liveAgent = false;
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(30);
    while ((!liveResponse || !liveTool || !liveAgent) && std::chrono::steady_clock::now() < deadline) {
        auto line = next(relay, std::chrono::seconds(30));
        if (!line) break;
        received.take(line);
        if (line->type() == "response" && line->root()["r"]["key"].equals("resp-live-1")) liveResponse = line;
        if (line->type() == "tool" && line->root()["agent"].equals(live)) liveTool = line;
        if (line->type() == "agent" && line->root()["a"]["id"].equals(live)) liveAgent = true;
    }
    ASSERT_TRUE(liveResponse);
    EXPECT_TRUE(liveResponse->root()["completion"].isTrue());
    EXPECT_TRUE(liveResponse->root()["project"].equals("live-project"));
    EXPECT_EQ(fixtures::optInt(liveResponse->root()["r"]["output"]), 120);
    ASSERT_TRUE(liveTool);
    EXPECT_FALSE(liveTool->root()["seeded"].isTrue());
    EXPECT_TRUE(liveTool->root()["failed"].isBool() && !liveTool->root()["failed"].isTrue());
    ASSERT_TRUE(liveAgent);
    EXPECT_TRUE(received.agents.at(live)->root()["a"]["cwd"].equals("/srv/work/live-project"));

    // Only the relay's own files were written in its folder (REMOTE 9).
    for (const auto& name : fixtures::listFiles(folder)) {
        EXPECT_TRUE(name == "zone.tzif" || name.rfind("history.sqlite", 0) == 0) << name;
    }
    EXPECT_EQ(fixtures::readText(folder / "zone.tzif"), utc);

    // The end of input ends the relay with status 0.
    relay.closeInput();
    EXPECT_EQ(relay.wait(std::chrono::seconds(15)), 0);
    fs::remove_all(temp);
}

TEST(RelayEndToEnd, PrintsItsVersionAndRefusesAnythingElse) {
    const auto [code, out] = runToEnd(AGENTSPEED_RELAY_BINARY, {"--version"});
    EXPECT_EQ(code, 0);
    EXPECT_EQ(out.rfind("agent-speed-relay ", 0), 0u) << out;
    EXPECT_NE(out.find(" protocol 1\n"), std::string::npos) << out;
    EXPECT_EQ(runToEnd(AGENTSPEED_RELAY_BINARY, {}).first, 2);
    EXPECT_EQ(runToEnd(AGENTSPEED_RELAY_BINARY, {"serve"}).first, 2);
    EXPECT_EQ(runToEnd(AGENTSPEED_RELAY_BINARY, {"serve", "--folder", ""}).first, 2);
    EXPECT_EQ(runToEnd(AGENTSPEED_RELAY_BINARY, {"--help"}).first, 2);
}
