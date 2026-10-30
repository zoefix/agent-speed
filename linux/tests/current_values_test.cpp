#include <gtest/gtest.h>
#include <sqlite3.h>
#include <unistd.h>

#include <chrono>
#include <filesystem>
#include <fstream>

#include "agentspeed/configured_modes.hpp"
#include "agentspeed/grok_parser.hpp"
#include "agentspeed/locations.hpp"
#include "agentspeed/monitor.hpp"
#include "agentspeed/openclaw.hpp"
#include "agentspeed/report_values.hpp"
#include "agentspeed/sqlite.hpp"
#include "fixture_support.hpp"

// What an agent shows beyond its own facts (EXECUTION-MODES, Current values): the config files, the session-current
// stores, Brotli rows, and that none of them reaches a response or a report.
using namespace agentspeed;
namespace fs = std::filesystem;

namespace {

constexpr std::int64_t kBase = 1792497600000;  // 2026-10-20T12:00:00Z
constexpr std::int64_t kDayStart = 1792454400000;

std::string decodeBase64(std::string_view text) {
    std::string out;
    std::uint32_t bits = 0;
    int count = 0;
    for (const char c : text) {
        if (c == '=') break;
        const auto index = std::string_view("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/").find(c);
        bits = (bits << 6) | static_cast<std::uint32_t>(index);
        count += 6;
        if (count >= 8) {
            count -= 8;
            out.push_back(static_cast<char>((bits >> count) & 0xFF));
        }
    }
    return out;
}

constexpr std::string_view kRow =
    "ExAIAMBSpVW1aCqQ7LtNAIS2pkFABXzx3creJH4SO792Dp2iPD1waVGreWCt5RjQdznoL/8WXv0gcrshGUbpvnBqRzMJ+hkmXQSMsmoOs/z6ZvJPQyt2hUOVtP6o1VQyuHPOxdcIBg1d6OO9eiLq/IMQ+Wci8tSmA1EHh1o2CwqGbbmlReHQrquC4/adFm0SpQkcWgWjp/7Jd+R7+urumYjp4UTU/a3Q/+A+lj+RdFprKnkvsA3f9AqGjJobL5Ly4HCwLbHCtuw7OPwW+6mrBH1JBsb5u6rVc9TL+fQ6OesUDif1eccklrVW8H42Wp7D4bCUqDN4x2rlkqIaGJLbZGVNAQ5ZFgUjzLJF9WXdqn/0j3CQNckv+kVrlVErHAap7aNEBQ8yV3VoU8o/73rRGYwpjRMcTKWWnPLI1ZRhwOEQSh7SCN5hGiU0jeAduTQF4xanG+nf12bSUsl3N6ThOI4D";

struct TempDir {
    fs::path path;
    TempDir() {
        std::string pattern = (fs::temp_directory_path() / "agentspeed-current-XXXXXX").string();
        path = ::mkdtemp(pattern.data());
    }
    ~TempDir() {
        std::error_code ignored;
        fs::remove_all(path, ignored);
    }
    fs::path file(const std::string& name) const { return path / name; }
    void write(const std::string& name, std::string_view text) const {
        fs::create_directories(file(name).parent_path());
        std::ofstream(file(name), std::ios::binary) << text;
    }
};

std::string effortOf(const ExecutionModes& modes) { return effort_speed::current(ModeKind::Effort, modes).mode; }

ExecutionModes recorded(const char* token) { return ExecutionModes::of(native::effort(token, ModeEvidence::Recorded, ModeScope::Turn)); }

std::string iso(std::int64_t ms) {
    const auto day = ms - kDayStart;
    char text[64];
    std::snprintf(text, sizeof text, "2026-10-20T%02lld:%02lld:%02lld.%03lldZ", static_cast<long long>(day / 3'600'000),
                  static_cast<long long>(day / 60'000 % 60), static_cast<long long>(day / 1000 % 60), static_cast<long long>(day % 1000));
    return text;
}

std::string assistant(const std::string& id, std::int64_t ms, std::int64_t start) {
    return R"({"type":"message","id":")" + id + R"(","timestamp":")" + iso(ms) +
           R"(","message":{"role":"assistant","content":[],"api":"anthropic-messages","provider":"anthropic","model":"claude-opus-5-5","usage":{"input":10,"output":20,"cacheRead":5,"cacheWrite":0},"stopReason":"stop","timestamp":)" +
           std::to_string(start) + "}}";
}

struct Sink : ResponseSink {
    std::vector<std::pair<ParsedResponse, bool>> all;
    void onResponse(const ParsedResponse& r, bool completion) override { all.emplace_back(r, completion); }
};

void writeWorkBuddy(const fs::path& folder, bool wal, const char* level) {
    {
        auto connection = sqlite::Connection::open((folder / "workbuddy.db").string(), sqlite::OpenMode::ReadWrite);
        if (wal) connection.execute("PRAGMA journal_mode=WAL");
        connection.execute("CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, title TEXT, thought_level TEXT)");
        connection.execute("INSERT OR REPLACE INTO sessions VALUES('s-1','private title'," +
                           (level ? "'" + std::string(level) + "'" : std::string("NULL")) + ")");
    }
    // A cleanly closed WAL database leaves no `-wal` or `-shm`.
    for (const char* suffix : {"-wal", "-shm"}) fs::remove(folder / (std::string("workbuddy.db") + suffix));
}

MonitorConfig workBuddyConfig(const fs::path& data, std::set<SourceKind> enabled) {
    RootSettings settings;
    settings.workBuddy = data.string();
    MonitorConfig config;
    config.locations = SourceLocations::resolve(settings, processEnvironment(), "/nonexistent-home");
    config.enabled = std::move(enabled);
    return config;
}

}  // namespace

TEST(CurrentValues, ABrotliRowDecodesToExactlyItsSize) {
    const auto text = openclaw::brotliDecode(decodeBase64(kRow), 2065);
    ASSERT_TRUE(text.has_value());
    EXPECT_EQ(text->size(), 2065u);
    const auto modes = openclaw::traceModes(*text);
    ASSERT_TRUE(modes.has_value());
    EXPECT_EQ(effort_speed::effort(*modes)->mode, "high");
    EXPECT_FALSE(effort_speed::speed(*modes).has_value()) << "fastMode false says nothing";
}

TEST(CurrentValues, DamagedTruncatedAndOversizedStreamsGiveNothing) {
    EXPECT_FALSE(openclaw::brotliDecode(decodeBase64("G/8gbm90IGEgYnJvdGxpIHN0cmVhbQ=="), 64)) << "damaged";
    EXPECT_FALSE(openclaw::brotliDecode(decodeBase64("E/QHAADKbamOyyJ9rT1uURBZUMSSxA=="), 2030)) << "truncated";
    EXPECT_FALSE(openclaw::brotliDecode(decodeBase64("Ez4AAMBcW+qXH+kL2VKGf6C490UORcZKo40iiYT+mzDo8Y3LoLy7HA1siTE7A5bQflznypcBQ8wPVQU="), 9))
        << "longer than declared";
    EXPECT_FALSE(openclaw::brotliDecode(
        decodeBase64("y///PwAkAOKxQHLv//j//weABEAcFoDu/R////8AkACIwwLQvf/j//8fABIAcVgAuvd//P//A0ACIA4LQPf+j///fwBIAMRhAeje//H//w8ACYA4LADd+z/+//8BIAEQhwWge/83"),
        2065))
        << "a small stream that expands past its size";
    const auto stream = decodeBase64(kRow);
    EXPECT_FALSE(openclaw::brotliDecode(std::string_view(stream).substr(0, stream.size() - 1), 2065)) << "cut short";
    EXPECT_FALSE(openclaw::brotliDecode(stream, 2064)) << "shorter than decoded";
    EXPECT_FALSE(openclaw::brotliDecode(stream, 2066)) << "longer than decoded";
}

TEST(CurrentValues, TraceRowsKeepOnlyThinkLevelAndFastMode) {
    const auto full = openclaw::traceModes(R"({"type":"trace.metadata","data":{"config":{"x":1},"model":{"thinkLevel":"adaptive","fastMode":true,"reasoningLevel":"on"}}})");
    ASSERT_TRUE(full.has_value());
    EXPECT_EQ(effort_speed::effort(*full)->mode, "adaptive");
    const auto speed = effort_speed::speed(*full);
    ASSERT_TRUE(speed.has_value());
    EXPECT_EQ(speed->mode, "fast");
    EXPECT_EQ(speed->evidence, "selected");
    EXPECT_TRUE(openclaw::traceModes(R"({"type":"trace.metadata","data":{"model":{"fastMode":false}}})")->empty()) << "false says nothing";
    EXPECT_TRUE(openclaw::traceModes(R"({"type":"trace.metadata","data":{"trimmed":true}})")->empty()) << "kept, without facts";
    EXPECT_FALSE(openclaw::traceModes(R"({"type":"model.completed","data":{"model":{"thinkLevel":"high"}}})"));
    EXPECT_FALSE(openclaw::traceModes("[1]"));
    EXPECT_FALSE(openclaw::traceModes(R"({"type":"trace.metadata")"));
}

TEST(CurrentValues, ALateTraceRowUpdatesItsResponsesAndATrimmedRowEndsTheReach) {
    Deduper deduper;
    openclaw::Window window;
    window.sessionId = "w1";
    window.createdMs = window.updatedMs = kBase;
    window.runtime = "openclaw";
    openclaw::Parser parser("/a/openclaw-agent.sqlite", window, deduper);
    Sink sink;
    parser.addTraces({{kBase, recorded("low")}}, sink);
    parser.feed(R"({"type":"session","version":4,"id":"w1","timestamp":")" + iso(kBase) + R"(","cwd":"/w/claw"})", sink);
    parser.feed(assistant("a", kBase + 2000, kBase + 1000), sink);
    parser.feed(assistant("b", kBase + 4000, kBase + 3000), sink);
    ASSERT_EQ(sink.all.size(), 2u);
    EXPECT_EQ(effortOf(sink.all[0].first.modes), "low");
    EXPECT_EQ(effortOf(sink.all[1].first.modes), "low");
    sink.all.clear();

    parser.addTraces({{kBase + 2500, recorded("max")}}, sink);
    ASSERT_EQ(sink.all.size(), 1u);
    EXPECT_FALSE(sink.all[0].second);
    EXPECT_EQ(sink.all[0].first.key.rfind("openclaw:b:", 0), 0u);
    EXPECT_EQ(effortOf(sink.all[0].first.modes), "max");
    EXPECT_EQ(effortOf(parser.modes()), "max");
    sink.all.clear();

    parser.addTraces({{kBase + 2800, {}}}, sink);
    ASSERT_FALSE(sink.all.empty());
    EXPECT_TRUE(sink.all[0].first.modes.empty()) << "a trimmed row leaves its attempt unknown";
    EXPECT_TRUE(parser.modes().empty());
}

TEST(CurrentValues, ATraceRowWrittenAfterItsTranscriptReachesTheLiveWindow) {
    TempDir directory;
    const std::string relative = "agents/main/agent/openclaw-agent.sqlite";
    const auto path = directory.file(relative);
    fs::create_directories(path.parent_path());
    fs::copy_file(fixtures::fixtureDir() / "openclaw/.openclaw" / relative, path);
    const std::string window = "0f1c2d3e-0001-4a00-8000-00000000c001";
    Deduper deduper;
    openclaw::Source source([&] { return std::vector<std::string>{path.string()}; }, deduper);
    const auto seeded = source.seed(0);
    const auto agentId = path.string() + "#" + window;
    const ParsedResponse* last = nullptr;
    for (const auto& [response, completion] : seeded.responses) {
        if (response.agentId == agentId && response.startMs) last = &response;
    }
    ASSERT_NE(last, nullptr);
    EXPECT_NE(effort_speed::current(ModeKind::Effort, last->modes).mode, "ultra");
    {
        auto writer = sqlite::Connection::open(path.string(), sqlite::OpenMode::ReadWrite);
        auto insert = writer.prepare(R"(INSERT INTO trajectory_runtime_events (session_id, seq, event_json, created_at) VALUES (?1, 99, '{"type":"trace.metadata","data":{"model":{"thinkLevel":"ultra"}}}', ?2))");
        insert.bind(1, window);
        insert.bind(2, *last->startMs);
        insert.step();
    }
    // The change is noticed by the file's stamp, and a coarse clock can give the write the seed's time.
    fs::last_write_time(path, fs::last_write_time(path) + std::chrono::seconds(1));
    const auto update = source.refresh();
    const auto parsers = source.parsers();
    const auto parser = std::find_if(parsers.begin(), parsers.end(), [&](const openclaw::Parser* p) { return p->agentId() == agentId; });
    ASSERT_NE(parser, parsers.end());
    EXPECT_EQ(effortOf((*parser)->modes()), "ultra");
    int changed = 0;
    for (const auto& [response, completion] : update.responses) {
        if (response.key != last->key) continue;
        ++changed;
        EXPECT_FALSE(completion);
        EXPECT_EQ(effortOf(response.modes), "ultra") << "a late row updates a response like any upsert";
    }
    EXPECT_EQ(changed, 1);
}

TEST(CurrentValues, GeminiOverridesCountOnlyForTheAgentsModel) {
    TempDir directory;
    directory.write("settings.json", R"({"modelConfigs":{
      "overrides":[
        {"match":{"model":"gemini-a"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingLevel":"LOW"}}}},
        {"match":{"model":"gemini-b","overrideScope":"core"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingLevel":"HIGH"}}}},
        {"match":{"model":"gemini-c"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingBudget":0}}}},
        {"match":{"model":"gemini-c"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingBudget":8192}}}}
      ],
      "customOverrides":[
        {"match":{"model":"gemini-a"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingLevel":"HIGH"}}}}
      ]}})");
    const auto efforts = configured::gemini(directory.file("settings.json").string());
    const auto current = [&](const char* model) { return effort_speed::current(ModeKind::Effort, {}, {}, efforts.modes(std::string(model))); };
    EXPECT_EQ(current("gemini-a"), (ModeAttribution{ModeKind::Effort, "high", "configured", std::nullopt}));
    EXPECT_FALSE(current("gemini-b").known()) << "a match with another member is not this model's";
    EXPECT_EQ(current("gemini-c").mode, "off") << "a budget that names no level is passed over";
    EXPECT_FALSE(efforts.modes(std::string("gemini-a")).attribute(ModeKind::Effort).known()) << "configured never counts as attribution";
}

TEST(CurrentValues, ConfigFilesAreRegularBoundedUtf8AndKeepOnlyTheNamedKey) {
    TempDir directory;
    directory.write("settings.json", "\xEF\xBB\xBF{\"reasoningEffort\":\"xhigh\",\"apiKey\":\"secret\"}");
    const auto path = directory.file("settings.json").string();
    const auto modes = configured::workBuddy(path);
    ASSERT_EQ(modes.facts().size(), 1u);
    EXPECT_EQ(modes.facts()[0].value, "xhigh");
    EXPECT_EQ(modes.facts()[0].evidence, ModeEvidence::Configured);
    directory.write("big.json", R"({"reasoningEffort":"high"})" + std::string(1 << 20, ' '));
    EXPECT_TRUE(configured::workBuddy(directory.file("big.json").string()).empty());
    directory.write("invalid.json", "{\"reasoningEffort\":\"\xFF\"}");
    EXPECT_TRUE(configured::workBuddy(directory.file("invalid.json").string()).empty());
    directory.write("list.json", R"([{"reasoningEffort":"high"}])");
    EXPECT_TRUE(configured::workBuddy(directory.file("list.json").string()).empty());
    fs::create_symlink(path, directory.file("link.json"));
    EXPECT_TRUE(configured::workBuddy(directory.file("link.json").string()).empty());
}

TEST(CurrentValues, AntigravityTierGoesThroughTheGoogleAdapter) {
    TempDir directory;
    const auto tier = [&](const std::string& value) {
        directory.write("config.json", R"({"userSettings":{"vertexServiceTier":")" + value + R"("}})");
        return effort_speed::current(ModeKind::Speed, {}, {}, configured::antigravity(directory.file("config.json").string()));
    };
    EXPECT_EQ(tier("priority"), (ModeAttribution{ModeKind::Speed, "fast", "configured", "google"}));
    EXPECT_EQ(tier("flex").mode, "flex");
    EXPECT_FALSE(tier("unspecified").known());
}

TEST(CurrentValues, ConfigPathsFollowTheRoots) {
    EXPECT_EQ(paths::antigravityConfigOf("/home/a/.gemini/antigravity-cli/conversations"), "/home/a/.gemini/config/config.json");
    const Environment none = [](const std::string&) { return std::nullopt; };
    EXPECT_EQ(paths::geminiSettingsOf({"/fixtures/gemini/tmp"}, none, "/home/a"), "/fixtures/gemini/settings.json");
    EXPECT_EQ(paths::geminiSettingsOf({}, none, "/home/a"), "/home/a/.gemini/settings.json");
    EXPECT_EQ(configured::codexConfigPath("/home/a/.codex/sessions"), "/home/a/.codex/config.toml");
}

TEST(CurrentValues, SessionStoresCreateNoFilesAndGiveNothingForAMissingRow) {
    TempDir directory;
    writeWorkBuddy(directory.path, true, "disabled");
    const auto path = directory.file("workbuddy.db").string();
    const auto before = fixtures::listFiles(directory.path);
    const auto values = session_modes::workBuddy(path, {"s-1", "s-2"});
    ASSERT_TRUE(values.contains("s-1"));
    EXPECT_EQ(values.at("s-1").facts()[0].value, "off");
    EXPECT_EQ(values.at("s-1").facts()[0].evidence, ModeEvidence::Selected);
    EXPECT_FALSE(values.contains("s-2"));
    EXPECT_EQ(fixtures::listFiles(directory.path), before);
    EXPECT_TRUE(session_modes::workBuddy(directory.file("none.db").string(), {"s-1"}).empty());
    EXPECT_TRUE(session_modes::hermes(path, {"s-1"}).empty()) << "a table without the column gives nothing";
}

TEST(CurrentValues, HermesReasoningConfigTakesOnlyItsEffort) {
    TempDir directory;
    const auto path = directory.file("state.db").string();
    {
        auto connection = sqlite::Connection::open(path, sqlite::OpenMode::ReadWrite);
        connection.execute("CREATE TABLE sessions(id TEXT PRIMARY KEY, model_config TEXT, system_prompt TEXT)");
        const std::vector<std::pair<std::string, std::string>> rows = {
            {"on", R"({"reasoning_config":{"enabled":true,"effort":"high"},"service_tier":"fast"})"},
            {"off", R"({"reasoning_config":{"enabled":false,"effort":"high"}})"},
            {"bare", R"({"model":"m"})"},
            {"broken", "not json"},
            {"huge", R"({"reasoning_config":{"effort":"high"},"x":")" + std::string(70'000, 'x') + R"("})"},
        };
        for (const auto& [id, config] : rows) {
            auto insert = connection.prepare("INSERT INTO sessions VALUES(?1,?2,'prompt')");
            insert.bind(1, id);
            insert.bind(2, config);
            insert.step();
        }
    }
    const auto values = session_modes::hermes(path, {"on", "off", "bare", "broken", "huge", "none"});
    EXPECT_EQ(values.at("on").facts()[0].value, "high");
    EXPECT_EQ(values.at("off").facts()[0].value, "off");
    EXPECT_TRUE(values.at("bare").empty());
    EXPECT_TRUE(values.at("broken").empty());
    EXPECT_FALSE(values.contains("huge"));
    EXPECT_FALSE(values.contains("none"));
}

TEST(CurrentValues, WorkBuddyAgentsTakeTheStoresAtRediscoveryOnly) {
    TempDir directory;
    const auto data = directory.file("data");
    const auto transcript = data / "projects/slug/s-1.jsonl";
    directory.write("data/projects/slug/s-1.jsonl", R"({"id":"m1","type":"message","role":"user","timestamp":1792497600000,"cwd":"/w/p","content":[{"type":"input_text","text":"x"}]})"
                                                    "\n");
    directory.write("data/settings.json", R"({"reasoningEffort":"low"})");
    writeWorkBuddy(data, false, "high");
    Monitor monitor(workBuddyConfig(data, {SourceKind::WorkBuddy}));
    const auto now = kBase + 100'000;
    monitor.start(now, kDayStart);
    ASSERT_NE(monitor.agents().find(transcript.string()), monitor.agents().end());
    EXPECT_EQ(monitor.snapshot(now, kDayStart).agents.at(0).effort, "high");
    EXPECT_FALSE(monitor.snapshot(now, kDayStart).agents.at(0).effortConfigured);
    writeWorkBuddy(data, false, nullptr);
    monitor.poll(now + 1000, kDayStart);
    EXPECT_EQ(monitor.snapshot(now + 1000, kDayStart).agents.at(0).effort, "high") << "read again only at rediscovery";
    EXPECT_TRUE(monitor.poll(now + 20'000, kDayStart));
    const auto snapshot = monitor.snapshot(now + 20'000, kDayStart);
    EXPECT_EQ(snapshot.agents.at(0).effort, "low");
    EXPECT_TRUE(snapshot.agents.at(0).effortConfigured);
    EXPECT_FALSE(monitor.poll(now + 40'000, kDayStart)) << "nothing changed";
}

TEST(CurrentValues, ADisabledSourcesFilesAreNotRead) {
    TempDir directory;
    const auto data = directory.file("data");
    directory.write("data/projects/slug/s-1.jsonl", R"({"id":"m1","type":"message","role":"user","timestamp":1792497600000,"cwd":"/w/p","content":[{"type":"input_text","text":"x"}]})"
                                                    "\n");
    directory.write("data/settings.json", R"({"reasoningEffort":"low"})");
    ModeContext context;
    EXPECT_FALSE(context.refresh());
    EXPECT_TRUE(context.configured(RootFamily::WorkBuddy, (data / "projects").string(), std::nullopt).empty());
    Monitor monitor(workBuddyConfig(data, {SourceKind::Codex}));
    monitor.start(kBase + 100'000, kDayStart);
    EXPECT_TRUE(monitor.agents().empty());
}

TEST(CurrentValues, ALiveGrokResponseFreezesTheSummarysEffort) {
    TempDir directory;
    const std::string wire = "sessions/work/one/updates.jsonl";
    directory.write("sessions/work/one/summary.json", R"({"info":{"id":"one","cwd":"/projects/nova"},"current_model_id":"grok-4.5","reasoning_effort":"high"})");
    directory.write(wire, "");
    RootSettings settings;
    settings.grokBuild = directory.file("sessions").string();
    MonitorConfig config;
    config.locations = SourceLocations::resolve(settings, processEnvironment(), "/nonexistent-home");
    config.enabled = {SourceKind::GrokBuild};
    Monitor monitor(std::move(config));
    monitor.start(kBase + 1000, kDayStart);
    std::ofstream(directory.file(wire), std::ios::app)
        << R"({"timestamp":1792497603,"method":"_x.ai/session/update","params":{"sessionId":"one","update":{"sessionUpdate":"turn_completed","prompt_id":"pending-prompt","usage":{"inputTokens":1200,"cachedReadTokens":300,"outputTokens":200,"modelCalls":2,"apiDurationMs":2000,"modelUsage":{"grok-4.5":{"inputTokens":1200,"cachedReadTokens":300,"outputTokens":200,"modelCalls":2,"apiDurationMs":2000}}}},"_meta":{"agentTimestampMs":1792497603000}}})"
           "\n";
    const auto now = kBase + 5000;
    monitor.poll(now, kDayStart);
    monitor.poll(now + 20'000, kDayStart);
    const auto snapshot = monitor.snapshot(now + 20'000, kDayStart);
    std::vector<std::string> efforts;
    for (const auto& [key, usage] : snapshot.report.counters->byMode()) {
        if (key.kind == ModeKind::Effort) efforts.push_back(key.mode + "/" + key.evidence);
    }
    EXPECT_EQ(efforts, std::vector<std::string>{"high/selected"});
    const auto responses = monitor.responses();
    ASSERT_FALSE(responses.empty());
    EXPECT_TRUE(responses.front().modes.empty()) << "a session-current value is not a response's";
}

TEST(CurrentValues, ALiveResponseFreezesTheSessionCurrentEffortAndNeverAConfiguredOne) {
    const auto rows = [](const char* token, bool isConfigured) {
        report::Ledger ledger;
        ParsedResponse response;
        response.key = "k";
        response.source = SourceKind::GrokBuild;
        response.agentId = "a";
        response.startMs = kBase;
        response.endMs = kBase + 1000;
        response.tokens.output = 10;
        response.final = true;
        ExecutionModes current;
        if (token) current = ExecutionModes::of(native::effort(token, isConfigured ? ModeEvidence::Configured : ModeEvidence::Selected,
                                                               isConfigured ? ModeScope::Configuration : ModeScope::Session));
        ledger.count(response, std::nullopt, "m", isConfigured ? effort_speed::current(ModeKind::Effort, {}, {}, current)
                                                               : effort_speed::current(ModeKind::Effort, {}, current));
        std::vector<std::string> efforts;
        for (const auto& [key, usage] : ledger.counters().byMode()) {
            if (key.kind == ModeKind::Effort) efforts.push_back(key.mode + "/" + key.evidence);
        }
        return efforts;
    };
    EXPECT_EQ(rows("high", false), std::vector<std::string>{"high/selected"});
    EXPECT_EQ(rows("high", true), std::vector<std::string>{"unknown/unknown"});
    EXPECT_EQ(rows(nullptr, false), std::vector<std::string>{"unknown/unknown"});
}
