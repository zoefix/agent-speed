// The facts a living character reacts to (CHARACTERS 5, Behavior, Inputs), on each agent of the list: whether output
// came since its in-flight request started, the newest end of its responses, and a live failure right after a failure.
// Ported from the Windows CharacterFactsTests.
#include <gtest/gtest.h>

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <random>

#include "agentspeed/monitor.hpp"
#include "agentspeed/work_signals.hpp"

using namespace agentspeed;
namespace fs = std::filesystem;

namespace {

constexpr std::int64_t kDayStart = 1792454400000;  // 2026-10-20T00:00:00Z
constexpr std::int64_t kNow = kDayStart + 12 * 3'600'000;

std::string iso(std::int64_t ms) {
    const auto day = ms - kDayStart;
    char text[64];
    std::snprintf(text, sizeof text, "2026-10-20T%02lld:%02lld:%02lld.%03lldZ", static_cast<long long>(day / 3'600'000),
                  static_cast<long long>(day / 60'000 % 60), static_cast<long long>(day / 1000 % 60), static_cast<long long>(day % 1000));
    return text;
}

std::string user(std::int64_t ms, const std::string& content) {
    return R"({"type":"user","timestamp":")" + iso(ms) + R"(","cwd":"/home/me/proj","message":{"role":"user","content":)" + content + "}}";
}

std::string assistant(std::int64_t ms, const std::string& id, int output, const std::string& stop, const std::string& content) {
    return R"({"type":"assistant","timestamp":")" + iso(ms) + R"(","requestId":"req_)" + id + R"(","uuid":"uuid-)" + id + "-" +
           std::to_string(ms) + R"(","message":{"id":")" + id + R"(","type":"message","role":"assistant","model":"claude-opus-5-5","content":)" +
           content + R"(,"stop_reason":)" + stop + R"(,"usage":{"input_tokens":1,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":)" +
           std::to_string(output) + "}}}";
}

// Failing `Bash` runs: each a call, then its failed result a second later, three seconds apart.
std::string runs(int first, int count, std::int64_t startMs) {
    std::string out;
    for (int i = first; i < first + count; ++i) {
        const std::int64_t at = startMs + i * 3000LL;
        const auto id = std::to_string(i);
        out += assistant(at, "m" + id, 5, "\"tool_use\"",
                         R"([{"type":"tool_use","id":"t)" + id + R"(","name":"Bash","input":{"command":"swift test"}}])") + "\n";
        out += user(at + 1000, R"([{"type":"tool_result","tool_use_id":"t)" + id + R"(","is_error":true,"content":"failed"}])") + "\n";
    }
    return out;
}

struct Folder {
    fs::path root = fs::temp_directory_path() / ("character-facts-" + std::to_string(std::random_device{}()));
    Folder() { fs::create_directories(root / "claude/projects/p"); }
    ~Folder() {
        std::error_code ec;
        fs::remove_all(root, ec);
    }
    void append(const fs::path& path, const std::string& text) const {
        std::ofstream out(path, std::ios::binary | std::ios::app);
        out << text;
    }
    MonitorConfig config() const {
        RootSettings s;
        s.claude = (root / "claude/projects").string();
        for (auto* other : {&s.codex, &s.dsh, &s.openCode, &s.gemini, &s.omp, &s.antigravity, &s.workBuddy, &s.workBuddyAi, &s.zcode,
                            &s.kimi, &s.grokBuild, &s.pi, &s.openClaw, &s.hermes, &s.droid, &s.devin, &s.qoder, &s.qoderCli}) {
            *other = (root / "none").string();
        }
        MonitorConfig c;
        c.locations = SourceLocations::resolve(s, processEnvironment(), (root / "home").string());
        c.enabled = {SourceKind::ClaudeCode};
        return c;
    }
};

const AgentRow& only(const Snapshot& snap) {
    EXPECT_EQ(snap.agents.size(), 1u);
    return snap.agents.front();
}

work::Action result(std::int64_t at, std::optional<bool> failed, bool seeded = false) {
    work::Action a;
    a.callMs = at - 100;
    a.resultMs = at;
    a.tool = "Bash";
    a.failed = failed;
    a.seeded = seeded;
    return a;
}

}  // namespace

TEST(CharacterFacts, LiveFailuresInARowAndTheWaitForOutputReachTheAgent) {
    Folder folder;
    const auto path = folder.root / "claude/projects/p/s-loop.jsonl";
    folder.append(path, user(kNow - 91'000, "\"go\"") + "\n" + runs(0, 2, kNow - 90'000));
    Monitor monitor(folder.config());
    monitor.start(kNow, kDayStart);
    EXPECT_FALSE(only(monitor.snapshot(kNow, kDayStart)).repeatFailureMs) << "failures read while seeding are no trouble";

    folder.append(path, runs(2, 2, kNow - 20'000));
    monitor.poll(kNow, kDayStart);
    const auto failing = only(monitor.snapshot(kNow, kDayStart));
    const std::int64_t newest = kNow - 20'000 + 3 * 3000 + 1000;
    EXPECT_EQ(failing.repeatFailureMs, newest);
    // The failed result asks the model again: it waits for output since then.
    EXPECT_EQ(failing.phase, Phase::Generating);
    EXPECT_EQ(failing.inFlightSinceMs, newest);
    EXPECT_FALSE(failing.outputSinceInFlight);
    ASSERT_TRUE(failing.lastResponseEndMs);
    EXPECT_LT(*failing.lastResponseEndMs, newest);

    folder.append(path, assistant(kNow - 3000, "m9", 4, "null", R"([{"type":"text","text":"x"}])") + "\n");
    monitor.poll(kNow + 1000, kDayStart);
    const auto answering = only(monitor.snapshot(kNow + 1000, kDayStart));
    EXPECT_TRUE(answering.outputSinceInFlight);
    ASSERT_TRUE(answering.lastResponseEndMs);
    EXPECT_GE(*answering.lastResponseEndMs, newest);
}

TEST(CharacterFacts, AnIdleAgentHasNoInFlightStart) {
    Folder folder;
    const auto path = folder.root / "claude/projects/p/s-done.jsonl";
    folder.append(path, user(kNow - 5000, "\"hi\"") + "\n" + assistant(kNow - 2000, "m1", 40, "\"end_turn\"", R"([{"type":"text","text":"x"}])") + "\n");
    Monitor monitor(folder.config());
    monitor.start(kNow, kDayStart);
    const auto agent = only(monitor.snapshot(kNow, kDayStart));
    EXPECT_EQ(agent.phase, Phase::Idle);
    EXPECT_FALSE(agent.inFlightSinceMs);
    EXPECT_EQ(agent.lastResponseEndMs, kNow - 2000);
    EXPECT_FALSE(agent.repeatFailureMs);
}

TEST(CharacterFacts, ASuccessBetweenFailuresIsNoTrouble) {
    using Log = std::deque<work::Action>;
    EXPECT_EQ(work::repeatFailure(Log{result(1000, true), result(2000, true)}), 2000);
    EXPECT_FALSE(work::repeatFailure(Log{result(1000, true), result(1500, false), result(2000, true)}));
    EXPECT_FALSE(work::repeatFailure(Log{result(1000, true), result(2000, true, true)}));
    // Results count in the order they were read, not in call order.
    EXPECT_EQ(work::repeatFailure(Log{result(3000, true), result(1000, false), result(2000, true)}), 3000);
    EXPECT_FALSE(work::repeatFailure(Log{result(1000, true)}));
    work::Action open;
    open.callMs = 1500;
    EXPECT_FALSE(work::repeatFailure(Log{result(1000, true), open, result(2000, std::nullopt)}));
    EXPECT_FALSE(work::repeatFailure(Log{}));
}
