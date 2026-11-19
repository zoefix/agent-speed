#include <gtest/gtest.h>

#include <algorithm>
#include <chrono>
#include <limits>
#include <map>

#include "agentspeed/codebuddy.hpp"
#include "agentspeed/discovery.hpp"
#include "agentspeed/engine.hpp"
#include "agentspeed/fs_util.hpp"
#include "agentspeed/history_analyzer.hpp"
#include "agentspeed/location_entries.hpp"
#include "agentspeed/mode_context.hpp"
#include "agentspeed/monitor.hpp"
#include "agentspeed/traffic.hpp"
#include "agentspeed/workbuddy_parser.hpp"
#include "devin_test_support.hpp"
#include "fixture_support.hpp"

// CodeBuddy Code (METRICS 4.20): the WorkBuddy format in the standalone CLI's own config folder, and the measured
// attempts of CLI 2.184.0 (METRICS 4.9 Timing model) that every source of the format reads.
using namespace agentspeed;
using devin_test::TempDir;
namespace fs = std::filesystem;

namespace {

constexpr std::int64_t kT0 = 1792746000000;  // 2026-10-23T09:00:00Z
constexpr std::int64_t kMin = std::numeric_limits<std::int64_t>::min();
const std::int64_t kNowMs =
    std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
const std::int64_t kTodayStartMs = kNowMs - 2 * 3'600'000;

Environment environmentOf(std::map<std::string, std::string> values) {
    return [values](const std::string& name) -> std::optional<std::string> {
        const auto it = values.find(name);
        return it == values.end() ? std::nullopt : std::optional<std::string>(it->second);
    };
}

class Collector : public ResponseSink {
public:
    void onResponse(const ParsedResponse& response, bool completion) override {
        emitted.emplace_back(response, completion);
        latest[response.key] = response;
    }
    std::vector<std::pair<ParsedResponse, bool>> emitted;
    std::map<std::string, ParsedResponse> latest;
};

std::string user(std::int64_t ms) {
    return R"({"id":"u)" + std::to_string(ms) + R"(","type":"message","role":"user","timestamp":)" + std::to_string(ms) +
           R"j(,"cwd":"/w/nova","content":[{"type":"input_text","text":"(prompt)"}]})j";
}

// `rawUsage` with the call's tokens and, unless empty, `model_calls` as given.
std::string rawUsage(const std::string& modelCalls) {
    return R"("rawUsage":{"prompt_tokens":1000,"completion_tokens":40)" + (modelCalls.empty() ? std::string() : ",\"model_calls\":" + modelCalls) +
           "}";
}

// The assistant line of call `messageId` that carries its usage.
std::string answer(std::int64_t ms, const std::string& messageId, const std::string& usage) {
    return R"({"id":"a)" + std::to_string(ms) + R"(","type":"message","role":"assistant","status":"completed","timestamp":)" +
           std::to_string(ms) + R"(,"providerData":{"messageId":")" + messageId + R"(","model":"glm-5.3",)" + usage + "}}";
}

std::string modelUsage(std::int64_t ms, const std::string& messageId, const std::string& usage) {
    return R"({"id":"m)" + std::to_string(ms) + R"(","type":"model-usage","timestamp":)" + std::to_string(ms) +
           R"(,"providerData":{"messageId":")" + messageId + R"(","model":"glm-5.3",)" + usage + "}}";
}

struct Run {
    std::unique_ptr<Deduper> deduper = std::make_unique<Deduper>();
    Collector sink;
    std::unique_ptr<WorkBuddyParser> parser;
};

Run run(const std::vector<std::string>& lines, SourceKind source = SourceKind::CodeBuddyCode) {
    Run out;
    out.parser = std::make_unique<WorkBuddyParser>("/h/.codebuddy/projects/w-nova/s1.jsonl", *out.deduper, source, "/h/.codebuddy/projects");
    for (const auto& line : lines) out.parser->feed(line, out.sink);
    return out;
}

std::string fixture(const std::string& relative) { return fsx::normalize((fixtures::fixtureDir() / relative).string()); }

const SourceRow& rowOf(const Snapshot& snap, SourceKind kind) {
    return *std::find_if(snap.sources.begin(), snap.sources.end(), [&](const SourceRow& r) { return r.source == kind; });
}

}  // namespace

// MARK: - Measured attempts

TEST(CodeBuddyAttempts, TheLastAttemptGivesTheStartAndTheFirstToken) {
    const auto calls = R"([{"attempt_no":1,"duration_ms":3000,"result":"failed"},)"
                       R"({"attempt_no":2,"duration_ms":2400.9,"time_to_first_token_ms":600.7}])";
    auto parsed = run({user(kT0), answer(kT0 + 5000, "m1", rawUsage(calls))});
    ASSERT_EQ(parsed.sink.emitted.size(), 1u);
    const auto& [response, completion] = parsed.sink.emitted[0];
    EXPECT_TRUE(completion);
    EXPECT_EQ(response.source, SourceKind::CodeBuddyCode);
    EXPECT_EQ(response.startMs, kT0 + 2600) << "the line's time minus the truncated duration";
    EXPECT_EQ(response.firstMs, kT0 + 3200);
    EXPECT_EQ(response.endMs, kT0 + 5000);
    EXPECT_EQ(parsed.parser->phase(), Phase::Idle);
}

TEST(CodeBuddyAttempts, AttemptsThatGiveNoStartKeepTheInferredOne) {
    for (const std::string calls : {R"({"duration_ms":1000})", "[]", R"j([{"duration_ms":900},"(text)"])j", R"([{"duration_ms":"2500"}])",
                                    R"([{"duration_ms":0.9}])", R"([{"duration_ms":1e999}])", R"([{"duration_ms":true}])",
                                    R"([{"duration_ms":-5}])", R"([{"duration_ms":2000,"duration_ms":null}])", R"([{"duration_ms":0}])",
                                    "null", R"([{"time_to_first_token_ms":100}])"}) {
        SCOPED_TRACE(calls);
        auto parsed = run({user(kT0), answer(kT0 + 3000, "m1", rawUsage(calls))});
        ASSERT_EQ(parsed.sink.emitted.size(), 1u);
        EXPECT_EQ(parsed.sink.emitted[0].first.startMs, kT0);
        EXPECT_EQ(parsed.sink.emitted[0].first.firstMs, std::nullopt);
    }
    // Only `rawUsage` carries the attempts.
    const auto usage = R"("usage":{"inputTokens":1000,"outputTokens":40,"model_calls":[{"duration_ms":2000}]})";
    auto parsed = run({user(kT0), answer(kT0 + 3000, "m1", usage)});
    EXPECT_EQ(parsed.sink.emitted.at(0).first.startMs, kT0);
}

TEST(CodeBuddyAttempts, AFirstTokenOutsideTheDurationIsDropped) {
    const std::vector<std::pair<std::string, std::optional<std::int64_t>>> cases = {
        {"3000", std::nullopt}, {"2000.5", kT0 + 3000}, {"-5", std::nullopt},  {"0.5", std::nullopt},
        {"1e999", std::nullopt}, {"\"9\"", std::nullopt}, {"99999999999999999999", std::nullopt}, {"2000", kT0 + 3000},
        {"1", kT0 + 1001}};
    for (const auto& [ttft, first] : cases) {
        SCOPED_TRACE(ttft);
        auto parsed = run({user(kT0), answer(kT0 + 3000, "m1", rawUsage(R"([{"duration_ms":2000,"time_to_first_token_ms":)" + ttft + "}]"))});
        ASSERT_EQ(parsed.sink.emitted.size(), 1u);
        EXPECT_EQ(parsed.sink.emitted[0].first.startMs, kT0 + 1000);
        EXPECT_EQ(parsed.sink.emitted[0].first.firstMs, first);
    }
}

// A call whose usage line lacks raw usage starts at the input line until the end-of-run `model-usage` line brings its
// attempts; the first measured start is kept.
TEST(CodeBuddyAttempts, ALaterLineWithAttemptsMovesTheStartOnce) {
    auto parsed = run({user(kT0), answer(kT0 + 5000, "m1", R"("usage":{"inputTokens":1000,"outputTokens":40})"),
                       modelUsage(kT0 + 4990, "m1", rawUsage(R"([{"duration_ms":2000,"time_to_first_token_ms":100}])")),
                       modelUsage(kT0 + 6000, "m1", rawUsage(R"([{"duration_ms":500,"time_to_first_token_ms":100}])"))});
    ASSERT_EQ(parsed.sink.emitted.size(), 3u);
    EXPECT_EQ(parsed.sink.emitted[0].first.startMs, kT0) << "inferred from the user line";
    EXPECT_TRUE(parsed.sink.emitted[0].second);
    EXPECT_FALSE(parsed.sink.emitted[1].second);
    EXPECT_FALSE(parsed.sink.emitted[2].second);
    const auto& last = parsed.sink.latest.at("m1");
    EXPECT_EQ(last.startMs, kT0 + 2990);
    EXPECT_EQ(last.firstMs, kT0 + 3090);
    EXPECT_EQ(last.endMs, kT0 + 6000);
    EXPECT_EQ(parsed.parser->phase(), Phase::Idle);
}

// A measured first line is kept although a later line of the call has other attempts; an unchanged line emits nothing.
TEST(CodeBuddyAttempts, TheFirstMeasuredStartIsKept) {
    const auto usage = rawUsage(R"([{"duration_ms":1500,"time_to_first_token_ms":300}])");
    auto parsed = run({user(kT0), answer(kT0 + 4000, "m1", usage), modelUsage(kT0 + 4000, "m1", rawUsage(R"([{"duration_ms":3500}])"))});
    ASSERT_EQ(parsed.sink.emitted.size(), 1u);
    EXPECT_EQ(parsed.sink.emitted[0].first.startMs, kT0 + 2500);
    EXPECT_EQ(parsed.sink.emitted[0].first.firstMs, kT0 + 2800);
}

// METRICS 3.6: a start before the signed 64-bit range saturates (year 0000 minus a duration that truncates to 2^63 − 1).
TEST(CodeBuddyAttempts, AMeasuredStartSaturates) {
    const std::string line = R"({"id":"a","timestamp":"0000-01-01T00:00:00Z","type":"message","role":"assistant","status":"completed",)"
                             R"("providerData":{"messageId":"m1",)" +
                             rawUsage(R"([{"duration_ms":1e19,"time_to_first_token_ms":5}])") + R"(},"cwd":"/w/x"})";
    auto parsed = run({line});
    ASSERT_EQ(parsed.sink.emitted.size(), 1u);
    EXPECT_EQ(parsed.sink.emitted[0].first.startMs, kMin);
    EXPECT_EQ(parsed.sink.emitted[0].first.firstMs, kMin + 5);

    const workbuddy::Attempt attempt{10, std::nullopt};
    EXPECT_EQ(attempt.startMs(kMin + 10), kMin);
    EXPECT_EQ(attempt.startMs(kMin + 9), kMin);
    EXPECT_EQ(attempt.startMs(kMin + 11), kMin + 1);

    // A later line of the call may end it after 1970: the interval check never overflows on such a start.
    engine::Response saturated;
    saturated.startMs = kMin;
    saturated.endMs = kT0;
    EXPECT_EQ(engine::interval(saturated), std::nullopt);
    saturated.endMs = kMin + 600'000;
    EXPECT_EQ(engine::interval(saturated), std::make_pair(kMin, kMin + 600'000));
}

// The WorkBuddy editions write the same lines: their calls are measured too, and so their history is analysed again.
TEST(CodeBuddyAttempts, WorkBuddyCallsAreMeasuredToo) {
    auto parsed = run({user(kT0), answer(kT0 + 5000, "m1", rawUsage(R"([{"duration_ms":1000,"time_to_first_token_ms":250}])"))},
                      SourceKind::WorkBuddy);
    ASSERT_EQ(parsed.sink.emitted.size(), 1u);
    EXPECT_EQ(parsed.sink.emitted[0].first.source, SourceKind::WorkBuddy);
    EXPECT_EQ(parsed.sink.emitted[0].first.startMs, kT0 + 4000);
    EXPECT_EQ(parsed.sink.emitted[0].first.firstMs, kT0 + 4250);
    EXPECT_EQ(history::Analyzer::kVersion, 7) << "measured attempts change WorkBuddy durations and firstMs";
}

// MARK: - Roots and location lists

TEST(CodeBuddyRoots, TheConfigFolderComesFromTheVariableWhenItIsNotBlank) {
    const auto folder = [](std::map<std::string, std::string> env) { return codebuddy::configFolder(environmentOf(std::move(env)), "/home/dev"); };
    EXPECT_EQ(folder({}), "/home/dev/.codebuddy");
    EXPECT_EQ(folder({{"CODEBUDDY_CONFIG_DIR", "/data/cb"}}), "/data/cb");
    EXPECT_EQ(folder({{"CODEBUDDY_CONFIG_DIR", ""}}), "/home/dev/.codebuddy");
    EXPECT_EQ(folder({{"CODEBUDDY_CONFIG_DIR", " \t　 "}}), "/home/dev/.codebuddy") << "Python whitespace is blank";
    EXPECT_EQ(folder({{"CODEBUDDY_CONFIG_DIR", " /data/cb"}}), " /data/cb") << "used as it is";
    EXPECT_EQ(folder({{"WORKBUDDY_CONFIG_DIR", "/data/wb"}}), "/home/dev/.codebuddy");

    // The reference's cases (reference.py check_codebuddy_roots).
    const auto root = [](std::map<std::string, std::string> env) { return codebuddy::automaticRoot(environmentOf(std::move(env)), "/home/dev"); };
    EXPECT_EQ(root({}), "/home/dev/.codebuddy/projects");
    EXPECT_EQ(root({{"CODEBUDDY_CONFIG_DIR", "/data/cb"}}), "/data/cb/projects");
    EXPECT_EQ(root({{"CODEBUDDY_CONFIG_DIR", "/data/cb/"}}), "/data/cb/projects");
    EXPECT_EQ(root({{"CODEBUDDY_CONFIG_DIR", "/data/cb/projects"}}), "/data/cb/projects/projects");
    EXPECT_EQ(root({{"CODEBUDDY_CONFIG_DIR", " \t"}}), "/home/dev/.codebuddy/projects");
    EXPECT_EQ(SourceLocations::defaultRoot(SourceKind::CodeBuddyCode, environmentOf({{"CODEBUDDY_CONFIG_DIR", "/srv/cb"}}), "/h"),
              "/srv/cb/projects");

    EXPECT_EQ(codebuddy::projectsOf("/work/cb/"), "/work/cb/projects");
    EXPECT_EQ(codebuddy::projectsOf("/work/cb/projects"), "/work/cb/projects");
    EXPECT_EQ(codebuddy::projectsOf("/work/cb/projects/"), "/work/cb/projects");
    EXPECT_EQ(userRoot("/work/cb", RootFamily::CodeBuddyCode), "/work/cb/projects");
    EXPECT_EQ(codebuddy::configFolderOf("/work/cb/projects"), "/work/cb");
    EXPECT_EQ(codebuddy::configFolderOf("/data/cb/projects/projects"), "/data/cb/projects");
}

TEST(CodeBuddyRoots, AUserLocationIsReadAsItsProjectsFolder) {
    LocationLists lists;
    lists.set(RootFamily::CodeBuddyCode, {DataLocation::at("/work/cb")});
    auto l = SourceLocations::resolve(lists, environmentOf({{"CODEBUDDY_CONFIG_DIR", "/data/cb"}}), "/h");
    EXPECT_EQ(l.codeBuddyCodeRoots, std::vector<std::string>{"/work/cb/projects"}) << "a chosen folder replaces the variable";

    lists.set(RootFamily::CodeBuddyCode, {DataLocation::automaticEntry(), DataLocation::at("/work/cb/projects"), DataLocation::at("/data/cb")});
    l = SourceLocations::resolve(lists, environmentOf({{"CODEBUDDY_CONFIG_DIR", "/data/cb"}}), "/h");
    EXPECT_EQ(l.codeBuddyCodeRoots, (std::vector<std::string>{"/data/cb/projects", "/work/cb/projects"})) << "each root once";
    const auto roots = l.fileRoots();
    ASSERT_FALSE(roots.empty());
    EXPECT_NE(std::find(roots.begin(), roots.end(), std::pair<RootFamily, std::string>{RootFamily::CodeBuddyCode, "/work/cb/projects"}), roots.end());
    EXPECT_TRUE(SourceLocations::resolve(LocationLists(), environmentOf({}), "/h").codeBuddyCodeRoots ==
                std::vector<std::string>{"/h/.codebuddy/projects"});
    EXPECT_TRUE(SourceLocations::resolve(LocationLists(), environmentOf({}), "/h").workBuddyRoots ==
                std::vector<std::string>{"/h/.workbuddy/projects"})
        << "WorkBuddy never reads ~/.codebuddy";
}

TEST(CodeBuddyRoots, AConfigFolderAndItsProjectsFolderAreOneLocation) {
    TempDir temp;
    fs::create_directories(temp.file("cfg/projects"));
    const auto environment = environmentOf({{"CODEBUDDY_CONFIG_DIR", temp.file("cfg")}});
    const auto key = [&](const std::string& relative) { return locationKey(temp.file(relative), RootFamily::CodeBuddyCode); };
    EXPECT_EQ(key("cfg"), key("cfg/projects"));
    EXPECT_EQ(key("cfg"), temp.file("cfg/projects"));
    const auto automatic = automaticKeys(RootFamily::CodeBuddyCode, LocationLists(), environment, "/h");
    EXPECT_EQ(automatic.keys, std::vector<std::string>{temp.file("cfg/projects")});
    LocationLists lists;
    EXPECT_EQ(lists.add(temp.file("cfg"), RootFamily::CodeBuddyCode, automatic), AddOutcome::AlreadyAutomatic);
    EXPECT_EQ(lists.add(temp.file("cfg/projects"), RootFamily::CodeBuddyCode, automatic), AddOutcome::AlreadyAutomatic);
    EXPECT_EQ(lists.add(temp.file("work/.codebuddy"), RootFamily::CodeBuddyCode, automatic), AddOutcome::Added);
    EXPECT_EQ(lists.add(temp.file("work/.codebuddy/projects"), RootFamily::CodeBuddyCode, automatic), AddOutcome::AlreadyListed);
    EXPECT_FALSE(automaticIsWiderThanDefault(RootFamily::CodeBuddyCode));
    EXPECT_FALSE(acceptsDatabaseFile(RootFamily::CodeBuddyCode));

    EXPECT_EQ(defaultLocations(RootFamily::CodeBuddyCode, environment, "/h"), std::vector<std::string>{temp.file("cfg/projects")});
    EXPECT_EQ(automaticShown(RootFamily::CodeBuddyCode, environment, "/h"), std::vector<std::string>{temp.file("cfg/projects")});
    const auto entry = locationEntry(RootFamily::CodeBuddyCode, DataLocation::automaticEntry(), environment, "/h");
    EXPECT_TRUE(entry.exists);
    EXPECT_EQ(entry.reveal, temp.file("cfg/projects"));
    EXPECT_FALSE(locationEntry(RootFamily::CodeBuddyCode, DataLocation::automaticEntry(), environmentOf({}), temp.file("none")).exists);
}

TEST(CodeBuddyRoots, TheSourceAndItsListFollowQoderCli) {
    const auto& order = locationListOrder();
    ASSERT_GT(order.size(), 19u);
    EXPECT_EQ(order[18], RootFamily::QoderCli);
    EXPECT_EQ(order[19], RootFamily::CodeBuddyCode);
    EXPECT_EQ(listName(RootFamily::CodeBuddyCode), "codebuddyCode");
    EXPECT_EQ(listNamed("codebuddyCode"), RootFamily::CodeBuddyCode);
    EXPECT_EQ(familyOf(SourceKind::CodeBuddyCode), RootFamily::CodeBuddyCode);
    EXPECT_EQ(sourceId(SourceKind::CodeBuddyCode), "codebuddyCode");
    EXPECT_EQ(sourceFromId("codebuddyCode"), SourceKind::CodeBuddyCode);
    EXPECT_TRUE(MonitorConfig::allSources().contains(SourceKind::CodeBuddyCode));
    EXPECT_EQ(static_cast<int>(SourceKind::CodeBuddyCode), static_cast<int>(SourceKind::QoderCli) + 1);
}

// MARK: - Discovery

TEST(CodeBuddyDiscovery, FindsOnlySessionFiles) {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "parser-expected.json");
    ASSERT_TRUE(expected.ok);
    const auto root = codebuddy::projectsOf(fixture("codebuddy"));
    const auto prefix = fsx::normalize(fixtures::fixtureDir().string()) + "/";
    std::vector<std::string> found, want;
    for (const auto& file : discovery::enumerate(root, RootFamily::CodeBuddyCode)) {
        found.push_back(file.path.substr(prefix.size()));
        EXPECT_TRUE(discovery::isAcceptablePath(root, file.path, RootFamily::CodeBuddyCode)) << file.path;
    }
    std::sort(found.begin(), found.end());
    for (const auto& path : expected.root()["codebuddyCode"]["discover"].elements()) want.push_back(*path.string());
    EXPECT_EQ(found, want);
    EXPECT_EQ(want.size(), 3u);
    for (const auto* relative : {"projects/stray.jsonl", "projects/Users-dev-projects-nova/3aa9b700-dbaa-4d96-adf7-9cfbc7523bc2/tool-results/call_cb1.jsonl",
                                 "projects/Users-dev-projects-nova/3aa9b700-dbaa-4d96-adf7-9cfbc7523bc2/subagents/workflows/wf_1/journal.jsonl",
                                 "projects/Users-dev-projects-nova/00fcb17c-cc55-4b75-a36b-26db100e9428/stdio/2026-10-23T09-00-00-000Z-4242.jsonl"}) {
        EXPECT_FALSE(discovery::isAcceptablePath(root, fixture(std::string("codebuddy/") + relative), RootFamily::CodeBuddyCode)) << relative;
    }
}

// MARK: - Configured effort

TEST(CodeBuddyModes, TheConfiguredEffortComesFromTheConfigFolder) {
    ModeContext context;
    const auto root = codebuddy::projectsOf(fixture("codebuddy"));
    context.addRoot(RootFamily::CodeBuddyCode, root);
    EXPECT_TRUE(context.refresh());
    const auto configured = context.configured(RootFamily::CodeBuddyCode, root, std::nullopt);
    ASSERT_EQ(configured.facts().size(), 1u);
    EXPECT_EQ(configured.facts()[0].value, "high");
    EXPECT_EQ(configured.facts()[0].evidence, ModeEvidence::Configured);
    EXPECT_TRUE(context.session(root, "3aa9b700-dbaa-4d96-adf7-9cfbc7523bc2").empty()) << "no session database";
    EXPECT_FALSE(context.refresh());
}

// MARK: - Monitor

// A config folder without `projects` is not found (the CodeBuddy IDE keeps settings there and writes no transcript).
TEST(CodeBuddyMonitor, TheSourceIsFoundByItsProjectsFolder) {
    TempDir temp;
    temp.write(".codebuddy/settings.json", R"({"reasoningEffort":"low"})");
    MonitorConfig config;
    config.locations = SourceLocations::resolve(LocationLists(), environmentOf({}), temp.path.string());
    config.enabled = {SourceKind::CodeBuddyCode};
    Monitor monitor(config);
    monitor.start(kNowMs, kTodayStartMs);
    auto snap = monitor.snapshot(kNowMs, kTodayStartMs);
    EXPECT_FALSE(rowOf(snap, SourceKind::CodeBuddyCode).available);
    EXPECT_EQ(rowOf(snap, SourceKind::CodeBuddyCode).path, temp.file(".codebuddy/projects"));
}

// Through the monitor: today's main session files, the sub-agent under its session, the configured effort, measured
// calls and the working folder.
TEST(CodeBuddyMonitor, MainSessionsSubAgentsAndTheConfiguredEffort) {
    TempDir temp;
    const auto t = kNowMs - 60'000;
    const auto calls = R"([{"duration_ms":4000,"time_to_first_token_ms":900}])";
    temp.write(".codebuddy/settings.json", R"({"reasoningEffort":"xhigh"})");
    temp.write(".codebuddy/projects/w-nova/s1.jsonl", user(t) + "\n" + answer(t + 5000, "m1", rawUsage(calls)) + "\n");
    temp.write(".codebuddy/projects/w-nova/s1/subagents/agent-a.jsonl", user(t + 6000) + "\n" + answer(t + 9000, "m2", rawUsage("")) + "\n");
    temp.write(".codebuddy/projects/w-nova/s1/subagents/workflows/wf_1/journal.jsonl", answer(t + 9500, "m3", rawUsage("")) + "\n");
    MonitorConfig config;
    config.locations = SourceLocations::resolve(LocationLists(), environmentOf({}), temp.path.string());
    config.enabled = {SourceKind::CodeBuddyCode};
    Monitor monitor(config);
    monitor.start(kNowMs, kTodayStartMs);
    const auto snap = monitor.snapshot(kNowMs, kTodayStartMs);
    EXPECT_EQ(snap.todayRequests, 2) << "the workflow journal is not a transcript";
    const auto& row = rowOf(snap, SourceKind::CodeBuddyCode);
    EXPECT_TRUE(row.available);
    EXPECT_EQ(row.path, temp.file(".codebuddy/projects"));
    EXPECT_EQ(row.sessions, 1) << "main session files only";
    const auto main = temp.file(".codebuddy/projects/w-nova/s1.jsonl");
    const auto sub = temp.file(".codebuddy/projects/w-nova/s1/subagents/agent-a.jsonl");
    EXPECT_EQ(monitor.parentOf(sub), main);
    EXPECT_TRUE(monitor.agents().at(sub).isSubagent);
    EXPECT_EQ(monitor.agents().at(main).source, SourceKind::CodeBuddyCode);
    for (const auto& agent : snap.agents) {
        SCOPED_TRACE(agent.id);
        EXPECT_EQ(agent.effort, "xhigh");
        EXPECT_TRUE(agent.effortConfigured);
    }
    const auto responses = monitor.responses();
    const auto measured = std::find_if(responses.begin(), responses.end(), [](const ParsedResponse& r) { return r.key == "m1"; });
    ASSERT_NE(measured, responses.end());
    EXPECT_EQ(measured->startMs, t + 1000);
    EXPECT_EQ(measured->firstMs, t + 1900);
    EXPECT_EQ(monitor.cwdOf(main), "/w/nova") << "the latest cwd (METRICS 10.6)";
}

// MARK: - Traffic

TEST(CodeBuddyTraffic, TheNpmCliIsTheAgent) {
    using namespace traffic;
    Env env;
    env.platform = Platform::Linux;
    env.home = "/home/dev";
    const auto process = [](std::string exe, std::vector<std::string> args) {
        Process p;
        p.pid = 10;
        p.exe = std::move(exe);
        p.args = std::move(args);
        return p;
    };
    EXPECT_EQ(matchSource(env, process("/usr/bin/node", {"node", "/usr/lib/node_modules/@tencent-ai/codebuddy-code/bin/codebuddy"}), {}),
              SourceKind::CodeBuddyCode);
    EXPECT_EQ(matchSource(env, process("/home/dev/.nvm/versions/node/v24.1.0/bin/node",
                                       {"node", "/home/dev/.nvm/versions/node/v24.1.0/lib/node_modules/@tencent-ai/codebuddy-code/bin/cbc-prewarm"}),
                          {}),
              SourceKind::CodeBuddyCode);
    EXPECT_EQ(matchSource(env, process("/home/dev/.local/bin/codebuddy", {"codebuddy"}), {}), std::nullopt) << "no native build";
    EXPECT_EQ(matchSource(env, process("/usr/bin/node", {"node", "/home/dev/src/codebuddy-code/bin/codebuddy"}), {}), std::nullopt);
    EXPECT_EQ(matchSource(env, process("/usr/bin/python3", {"python3", "/x/@tencent-ai/codebuddy-code/bin/codebuddy"}), {}), std::nullopt)
        << "a script host only";
}
