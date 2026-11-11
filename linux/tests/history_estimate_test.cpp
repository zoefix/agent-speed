// QUOTAS 10.3: the history analysis keeps each file's recent spend for the estimate.
#include <gtest/gtest.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <chrono>
#include <cstdio>
#include <ctime>
#include <filesystem>
#include <fstream>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/history_analyzer.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/tz.hpp"

using namespace agentspeed;
using namespace agentspeed::history;
namespace fs = std::filesystem;

namespace {

constexpr std::int64_t kDayMs = 86'400'000;

struct Temp {
    fs::path root;
    Temp() {
        char pattern[] = "/tmp/agentspeed-estimate-XXXXXX";
        root = fs::path(fsx::normalize(mkdtemp(pattern)));
    }
    ~Temp() {
        std::error_code ec;
        fs::remove_all(root, ec);
    }
    std::string store() const { return (root / "store" / "history.sqlite").string(); }
};

std::int64_t nowMs() {
    return static_cast<std::int64_t>(
        std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count());
}

std::string isoOf(std::int64_t ms) {
    const auto seconds = static_cast<std::time_t>(ms / 1000);
    std::tm utc{};
    gmtime_r(&seconds, &utc);
    char text[40];
    const auto n = std::strftime(text, sizeof text, "%Y-%m-%dT%H:%M:%S", &utc);
    std::snprintf(text + n, sizeof text - n, ".%03dZ", static_cast<int>(ms % 1000));
    return text;
}

void write(const fs::path& path, const std::string& text, std::optional<std::int64_t> mtimeMs = std::nullopt) {
    fs::create_directories(path.parent_path());
    std::ofstream(path, std::ios::binary) << text;
    if (!mtimeMs) return;
    struct timespec times[2];
    times[0].tv_sec = times[1].tv_sec = static_cast<time_t>(*mtimeMs / 1000);
    times[0].tv_nsec = times[1].tv_nsec = static_cast<long>((*mtimeMs % 1000) * 1'000'000);
    utimensat(AT_FDCWD, path.c_str(), times, 0);
}

std::string line(std::int64_t ms, const std::string& rest) { return "{\"timestamp\":\"" + isoOf(ms) + "\"," + rest + "}\n"; }

const std::string kLimits =
    "{ \"limit_id\": \"codex\",\t \"primary\": {\"used_percent\": 12.5, \"window_minutes\": 300, \"resets_at\": 1794897479},"
    " \"secondary\": null, \"note\": \"a b\" }";
const std::string kLimitsCompact =
    "{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":12.5,\"window_minutes\":300,\"resets_at\":1794897479},"
    "\"secondary\":null,\"note\":\"a b\"}";

// One legacy Codex turn starting at `t0`, with a token count line carrying rate limits.
std::string codexRollout(std::int64_t t0, const std::string& thread) {
    const std::string usage =
        "{\"input_tokens\":12000,\"cached_input_tokens\":2000,\"output_tokens\":800,\"reasoning_output_tokens\":100,\"total_tokens\":12800}";
    return line(t0, "\"type\":\"session_meta\",\"payload\":{\"id\":\"" + thread + "\",\"timestamp\":\"" + isoOf(t0) +
                        "\",\"cwd\":\"/tmp/p\",\"originator\":\"codex_cli_rs\",\"model_provider\":\"openai\",\"source\":\"cli\"}") +
           line(t0 + 100, "\"type\":\"turn_context\",\"payload\":{\"cwd\":\"/tmp/p\",\"model\":\"gpt-5.5\"}") +
           line(t0 + 500, "\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"t-1\"}") +
           line(t0 + 500, "\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"hi\"}") +
           line(t0 + 600, "\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null,\"rate_limits\":null}") +
           line(t0 + 4000, "\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":" + usage +
                               ",\"last_token_usage\":" + usage + "},\"rate_limits\":" + kLimits + "}") +
           line(t0 + 4100, "\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"t-1\"}");
}

std::string claudeAssistant(std::int64_t ms, const std::string& id, const std::string& model) {
    return "{\"cwd\":\"/tmp/p\",\"sessionId\":\"s-est\",\"version\":\"2.1.300\",\"entrypoint\":\"cli\",\"type\":\"assistant\",\"requestId\":\"req_" +
           id + "\",\"uuid\":\"uuid-" + id + "\",\"message\":{\"id\":\"" + id + "\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"" + model +
           "\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":100,"
           "\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":50}},\"timestamp\":\"" +
           isoOf(ms) + "\"}\n";
}

std::string claudeUser(std::int64_t ms, const std::string& id) {
    return "{\"cwd\":\"/tmp/p\",\"sessionId\":\"s-est\",\"version\":\"2.1.300\",\"entrypoint\":\"cli\",\"type\":\"user\",\"uuid\":\"u-" + id +
           "\",\"message\":{\"role\":\"user\",\"content\":\"go\"},\"timestamp\":\"" + isoOf(ms) + "\"}\n";
}

std::string piSession(std::int64_t t0) {
    return "{\"type\":\"session\",\"version\":3,\"id\":\"01a1594a-3d40-76f3-bfd9-0015a195a333\",\"timestamp\":\"" + isoOf(t0) +
           "\",\"cwd\":\"/tmp/flat\"}\n"
           "{\"type\":\"message\",\"id\":\"e5b40001\",\"parentId\":null,\"timestamp\":\"" +
           isoOf(t0 + 2000) + "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}],\"timestamp\":" +
           std::to_string(t0 + 2000) +
           "}}\n"
           "{\"type\":\"message\",\"id\":\"e5b40002\",\"parentId\":\"e5b40001\",\"timestamp\":\"" +
           isoOf(t0 + 4100) +
           "\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"api\":\"openai-responses\","
           "\"provider\":\"openai\",\"model\":\"gpt-5.5\",\"usage\":{\"input\":1200,\"output\":140,\"cacheRead\":0,\"cacheWrite\":0,"
           "\"totalTokens\":1340},\"stopReason\":\"stop\",\"timestamp\":" +
           std::to_string(t0 + 2006) + ",\"durationMs\":2090.2}}\n";
}

SourceLocations locationsIn(const Temp& temp) {
    SourceLocations l;
    l.codexRoots = {(temp.root / "codex" / "sessions").string()};
    l.claudeRoots = {(temp.root / "claude" / "projects").string()};
    l.piRoots = {(temp.root / "pi" / "sessions").string()};
    return l;
}

const std::set<SourceKind> kEnabled{SourceKind::Codex, SourceKind::ClaudeCode, SourceKind::Pi};

std::int64_t estimateRows(const Store& store) { return store.connection().scalarInt64("SELECT count(*) FROM estimate_spend"); }

}  // namespace

TEST(HistoryEstimate, CodexRolloutKeepsItsRecentResponseAndRateLimitLine) {
    Temp temp;
    const auto t0 = nowMs() - 3'600'000;
    const auto path = (temp.root / "codex" / "sessions" / "2026" / "11" / "11" / "rollout-recent.jsonl").string();
    write(path, codexRollout(t0, "thread-recent"));
    auto store = Store::open(temp.store());
    Analyzer analyzer(*store, std::make_shared<pricing::PriceBook>());
    const auto report = analyzer.run(locationsIn(temp), kEnabled, TimeZone::utc(), "UTC");
    EXPECT_EQ(report.filesAnalyzed, 1);

    const auto spend = analyzer.estimateSpend(nowMs() - kEstimateKeepMs);
    EXPECT_EQ(spend.version, EstimateFile::kVersion);
    ASSERT_EQ(spend.responses.size(), 1u);
    const auto& r = spend.responses[0];
    EXPECT_EQ(r.source, SourceKind::Codex);
    EXPECT_EQ(r.agentId, path);
    EXPECT_EQ(r.model, "gpt-5.5");
    EXPECT_EQ(r.provider, std::optional<std::string>("openai"));
    EXPECT_EQ(r.startMs, std::optional<std::int64_t>(t0 + 500));
    EXPECT_EQ(r.endMs, t0 + 4000);
    EXPECT_EQ(r.requests, 1);
    EXPECT_FALSE(r.cloudRoute);
    // Priced and counted as the day's history row.
    const auto days = analyzer.days();
    ASSERT_EQ(days.size(), 1u);
    ASSERT_TRUE(r.costMicroUsd.has_value());
    EXPECT_GT(*r.costMicroUsd, 0);
    EXPECT_EQ(r.costMicroUsd, days[0].totals.costMicroUsd);
    EXPECT_EQ(r.tokens, days[0].totals.input + days[0].totals.output);

    ASSERT_EQ(spend.lines.size(), 1u);
    EXPECT_EQ(spend.lines[0].agentId, path);
    EXPECT_EQ(spend.lines[0].ts, t0 + 4000);
    EXPECT_EQ(spend.lines[0].rateLimits, kLimitsCompact);

    // Nothing ended after the window asked for.
    const auto later = analyzer.estimateSpend(t0 + 5000);
    EXPECT_TRUE(later.responses.empty());
    EXPECT_TRUE(later.lines.empty());
}

TEST(HistoryEstimate, LeavesOutWhatEndedMoreThanEightDaysAgo) {
    Temp temp;
    const auto old = nowMs() - 10 * kDayMs;
    write(temp.root / "codex" / "sessions" / "2026" / "11" / "01" / "rollout-old.jsonl", codexRollout(old, "thread-old"), old + 5000);
    auto store = Store::open(temp.store());
    Analyzer analyzer(*store, std::make_shared<pricing::PriceBook>());
    analyzer.run(locationsIn(temp), kEnabled, TimeZone::utc(), "UTC");
    ASSERT_EQ(analyzer.days().size(), 1u);
    const auto spend = analyzer.estimateSpend(old - kDayMs);
    EXPECT_TRUE(spend.responses.empty());
    EXPECT_TRUE(spend.lines.empty());
    // Analysed for the estimate all the same.
    const auto stored = store->loadFiles();
    ASSERT_EQ(stored.size(), 1u);
    EXPECT_EQ(stored.begin()->second.estimate.version, EstimateFile::kVersion);
}

TEST(HistoryEstimate, ANonQuotaSourceKeepsNone) {
    Temp temp;
    const auto t0 = nowMs() - 3'600'000;
    write(temp.root / "pi" / "sessions" / "--tmp-flat--" / "2026-11-11T10-00-00-000Z_01a1594a-3d40-76f3-bfd9-0015a195a333.jsonl",
          piSession(t0));
    auto store = Store::open(temp.store());
    Analyzer analyzer(*store, std::make_shared<pricing::PriceBook>());
    analyzer.run(locationsIn(temp), kEnabled, TimeZone::utc(), "UTC");
    const auto days = analyzer.days();
    ASSERT_EQ(days.size(), 1u);
    EXPECT_EQ(days[0].key.source, SourceKind::Pi);
    EXPECT_EQ(days[0].totals.requests, 1);
    const auto spend = analyzer.estimateSpend(0);
    EXPECT_TRUE(spend.responses.empty());
    EXPECT_TRUE(spend.lines.empty());
    const auto stored = store->loadFiles();
    ASSERT_EQ(stored.size(), 1u);
    EXPECT_EQ(stored.begin()->second.estimate.version, EstimateFile::kVersion);
}

TEST(HistoryEstimate, ClaudeModelsInACloudFormAreMarked) {
    Temp temp;
    const auto t0 = nowMs() - 3'600'000;
    write(temp.root / "claude" / "projects" / "-tmp-p" / "s-est.jsonl",
          claudeUser(t0, "1") + claudeAssistant(t0 + 2000, "msg_A", "claude-opus-5-5") + claudeUser(t0 + 3000, "2") +
              claudeAssistant(t0 + 5000, "msg_B", "us.anthropic.claude-opus-5-5-v1:0") + claudeUser(t0 + 6000, "3") +
              claudeAssistant(t0 + 8000, "msg_C", "anthropic/claude-opus-5-5"));
    auto store = Store::open(temp.store());
    Analyzer analyzer(*store, std::make_shared<pricing::PriceBook>());
    analyzer.run(locationsIn(temp), kEnabled, TimeZone::utc(), "UTC");
    const auto spend = analyzer.estimateSpend(0);
    ASSERT_EQ(spend.responses.size(), 3u);
    for (const auto& r : spend.responses) {
        EXPECT_EQ(r.source, SourceKind::ClaudeCode);
        EXPECT_EQ(r.model, "claude-opus-5-5");
        EXPECT_EQ(r.tokens, 150);
        EXPECT_TRUE(r.costMicroUsd.has_value());
    }
    EXPECT_FALSE(spend.responses[0].cloudRoute);
    EXPECT_TRUE(spend.responses[1].cloudRoute);
    // A route prefix is no cloud form.
    EXPECT_FALSE(spend.responses[2].cloudRoute);
    EXPECT_TRUE(spend.lines.empty());
}

TEST(HistoryEstimate, StoreRoundTripsTheEstimate) {
    Temp temp;
    StoredFile file;
    file.path = "/x/rollout.jsonl";
    file.family = "Codex";
    file.size = 10;
    file.modified = 20;
    file.version = Analyzer::kVersion;
    file.estimate.version = EstimateFile::kVersion;
    EstimateResponse a;
    a.source = SourceKind::Codex;
    a.agentId = "/x/rollout.jsonl";
    a.model = "gpt-5.5";
    a.provider = "openai";
    a.startMs = 1'000;
    a.endMs = 2'000;
    a.costMicroUsd = 1'234;
    a.tokens = 99;
    a.requests = 2;
    EstimateResponse b;
    b.source = SourceKind::ClaudeDesktop;
    b.agentId = "/x/other \"agent\"\n";
    b.model = "caf\xC3\xA9";
    b.endMs = -5;
    b.cloudRoute = true;
    file.estimate.responses = {a, b};
    file.estimate.lines = {{"/x/rollout.jsonl", 2'001, kLimitsCompact}, {"/x/other \"agent\"\n", 3'000, "not json"}};
    {
        auto store = Store::open(temp.store());
        std::vector<std::pair<StoredFile*, std::vector<std::uint64_t>>> batch{{&file, {}}};
        store->save(batch);
    }
    auto store = Store::open(temp.store());
    auto loaded = store->loadFiles();
    ASSERT_TRUE(loaded.contains(file.path));
    EXPECT_EQ(loaded[file.path].estimate, file.estimate);
    EXPECT_EQ(decodeEstimate(encodeEstimate(file.estimate)), std::optional<EstimateFile>([&] {
                  auto e = file.estimate;
                  e.version = 0;
                  return e;
              }()));

    // A row that does not parse: never analysed for the estimate.
    store->connection().execute("UPDATE estimate_spend SET data = '{\"a\":[\"x\"],\"r\":[[3]],\"l\":[]}'");
    loaded = store->loadFiles();
    EXPECT_EQ(loaded[file.path].estimate, EstimateFile{});
    EXPECT_EQ(estimateRows(*store), 1);
    store->clear();
    EXPECT_EQ(estimateRows(*store), 0);
}

TEST(HistoryEstimate, ARecentFileStoredWithoutItsEstimateIsAnalysedOnceMore) {
    Temp temp;
    const auto now = nowMs();
    const auto old = now - 10 * kDayMs;
    write(temp.root / "codex" / "sessions" / "2026" / "11" / "11" / "rollout-recent.jsonl", codexRollout(now - 3'600'000, "thread-recent"));
    write(temp.root / "codex" / "sessions" / "2026" / "11" / "01" / "rollout-old.jsonl", codexRollout(old, "thread-old"), old + 5000);
    auto store = Store::open(temp.store());
    {
        Analyzer analyzer(*store, std::make_shared<pricing::PriceBook>());
        EXPECT_EQ(analyzer.run(locationsIn(temp), kEnabled, TimeZone::utc(), "UTC").filesPlanned, 2);
        EXPECT_EQ(analyzer.run(locationsIn(temp), kEnabled, TimeZone::utc(), "UTC").filesPlanned, 0);
    }
    // A store from before the estimate.
    store->connection().execute("DELETE FROM estimate_spend");
    Analyzer upgraded(*store, std::make_shared<pricing::PriceBook>());
    EXPECT_TRUE(upgraded.estimateSpend(0).responses.empty());
    const auto report = upgraded.run(locationsIn(temp), kEnabled, TimeZone::utc(), "UTC");
    EXPECT_EQ(report.filesPlanned, 1);
    const auto spend = upgraded.estimateSpend(now - kEstimateKeepMs);
    EXPECT_EQ(spend.responses.size(), 1u);
    EXPECT_EQ(spend.lines.size(), 1u);
    EXPECT_EQ(upgraded.days().size(), 2u);
    // The old file keeps its stored row-less state and is not analysed again either.
    EXPECT_EQ(upgraded.run(locationsIn(temp), kEnabled, TimeZone::utc(), "UTC").filesPlanned, 0);
    EXPECT_EQ(estimateRows(*store), 1);
}
