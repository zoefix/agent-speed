#include <gtest/gtest.h>
#include <unistd.h>

#include <algorithm>
#include <filesystem>
#include <fstream>
#include <set>

#include "agentspeed/history_analyzer.hpp"
#include "agentspeed/history_store.hpp"
#include "agentspeed/mode_context.hpp"
#include "agentspeed/monitor.hpp"
#include "agentspeed/opencode_set.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/sqlite.hpp"
#include "agentspeed/tz.hpp"
#include "agentspeed/zcode_set.hpp"
#include "fixture_support.hpp"

// METRICS 2.1 in the live monitor and the daily history: several locations of one source are read together, a file or
// database reachable twice counts once, and every database keeps its own identity (issue #45).
using namespace agentspeed;
namespace fs = std::filesystem;

namespace {

constexpr std::int64_t kDayStart = 1792454400000;  // 2026-10-20T00:00:00Z
constexpr std::int64_t kNow = kDayStart + 86'000'000;
const std::string kModern = "rollout-2026-10-20T10-00-00-modern.jsonl";
const std::string kLegacy = "rollout-2026-10-20T10-01-00-legacy.jsonl";

struct TempDir {
    fs::path path;
    TempDir() {
        std::string pattern = (fs::temp_directory_path() / "agentspeed-multi-XXXXXX").string();
        path = fs::canonical(::mkdtemp(pattern.data()));
    }
    ~TempDir() {
        std::error_code ignored;
        fs::remove_all(path, ignored);
    }
    std::string file(const std::string& name) const { return (path / name).string(); }
    void write(const std::string& name, std::string_view text) const {
        fs::create_directories(fs::path(file(name)).parent_path());
        std::ofstream(file(name), std::ios::binary) << text;
    }
};

const DataLocation kAutomatic = DataLocation::automaticEntry();
DataLocation at(const std::string& path) { return DataLocation::at(path); }

Environment environmentOf(std::map<std::string, std::string> values) {
    return [values](const std::string& name) -> std::optional<std::string> {
        const auto it = values.find(name);
        return it == values.end() ? std::nullopt : std::optional<std::string>(it->second);
    };
}

// Codex fixture rollouts in `folder/2026/10/20/`.
void copyCodex(const std::vector<std::string>& names, const std::string& folder) {
    const auto day = fs::path(folder) / "2026/10/20";
    fs::create_directories(day);
    for (const auto& name : names) fs::copy_file(fixtures::fixtureDir() / "codex/sessions/2026/10/20" / name, day / name);
}

// A copy of the OpenCode fixture database whose ids carry `suffix`, so it holds other sessions and rows.
void opencodeCopy(const std::string& folder, const std::string& suffix = "") {
    fs::create_directories(folder);
    const auto target = fs::path(folder) / "opencode.db";
    fs::copy_file(fixtures::fixtureDir() / "opencode/opencode.db", target);
    if (suffix.empty()) return;
    const auto writer = sqlite::Connection::open(target.string(), sqlite::OpenMode::ReadWrite);
    writer.execute("UPDATE session_v2 SET id = id || '" + suffix + "', parent_id = parent_id || '" + suffix + "'");
    writer.execute("UPDATE session_message SET id = id || '" + suffix + "', session_id = session_id || '" + suffix + "'");
}

MonitorConfig configOf(const LocationLists& lists, std::set<SourceKind> enabled, const Environment& environment = environmentOf({})) {
    MonitorConfig config;
    config.locations = SourceLocations::resolve(lists, environment, "/nonexistent-home");
    config.enabled = std::move(enabled);
    return config;
}

MonitorConfig codex(const LocationList& list) {
    LocationLists lists;
    lists.set(RootFamily::Codex, list);
    return configOf(lists, {SourceKind::Codex});
}

const SourceRow& rowOf(const Snapshot& snap, SourceKind kind) {
    return *std::find_if(snap.sources.begin(), snap.sources.end(), [&](const SourceRow& r) { return r.source == kind; });
}

std::set<std::string> keysOf(const Monitor& monitor) {
    std::set<std::string> keys;
    for (const auto& r : monitor.responses()) keys.insert(r.key);
    return keys;
}

std::string hermesTurn(const std::string& session, const std::string& time, const std::string& callTime) {
    const auto prefix = "2026-10-20 ";
    return prefix + time + ",000 INFO [" + session + "] agent.turn_context: conversation turn: session=" + session +
           " model=hermes-4-405b provider=nous platform=cli history=1 msg='hi'\n" + prefix + callTime + ",000 INFO [" + session +
           "] agent.conversation_loop: API call #1: model=hermes-4-405b provider=nous in=900 out=120 total=1020 latency=2.0s\n" +
           prefix + callTime + ",100 INFO [" + session +
           "] agent.conversation_loop: Turn ended: reason=text_response(finish_reason=stop) model=hermes-4-405b api_calls=1/90 "
           "budget=1/90 tool_turns=0 last_msg_role=assistant response_len=4 session=" +
           session + "\n";
}

}  // namespace

// MARK: - Files

TEST(MultiLocation, TwoCodexFoldersAreReadTogether) {
    TempDir directory;
    const auto official = directory.file("official/sessions"), company = directory.file("company/sessions");
    copyCodex({kModern}, official);
    copyCodex({kLegacy}, company);
    Monitor monitor(codex({at(official), at(company)}));
    monitor.start(kNow, kDayStart);
    EXPECT_EQ(monitor.trackedFiles(), 2u);
    const auto keys = keysOf(monitor);
    EXPECT_TRUE(keys.contains("resp-1"));
    EXPECT_TRUE(keys.contains("codex-legacy:thread-legacy:20213")) << "both folders' responses";
    EXPECT_EQ(monitor.sessionCount(SourceKind::Codex, kDayStart), 2);
    const auto snap = monitor.snapshot(kNow, kDayStart);
    EXPECT_TRUE(rowOf(snap, SourceKind::Codex).available);
    EXPECT_EQ(rowOf(snap, SourceKind::Codex).path, official) << "the row's path is the first detected root";
}

TEST(MultiLocation, TheFirstDetectedRootNamesTheSourceAndAnyRootDetectsIt) {
    TempDir directory;
    const auto company = directory.file("company/sessions");
    copyCodex({kLegacy}, company);
    Monitor monitor(codex({at(directory.file("missing")), at(company)}));
    monitor.start(kNow, kDayStart);
    const auto snap = monitor.snapshot(kNow, kDayStart);
    EXPECT_TRUE(rowOf(snap, SourceKind::Codex).available);
    EXPECT_EQ(rowOf(snap, SourceKind::Codex).path, company);
    EXPECT_EQ(rowOf(snap, SourceKind::Codex).sessions, 1);
}

TEST(MultiLocation, AFileUnderTwoNestedRootsIsReadOnce) {
    TempDir directory;
    const auto root = directory.file("codex/sessions");
    copyCodex({kModern, kLegacy}, root);
    Monitor monitor(codex({at(root), at(root + "/2026/10")}));
    monitor.start(kNow, kDayStart);
    EXPECT_EQ(monitor.trackedFiles(), 2u);
    EXPECT_EQ(monitor.sessionCount(SourceKind::Codex, kDayStart), 2);
    for (const auto& r : monitor.responses()) EXPECT_EQ(r.agentId.rfind(root + "/", 0), 0u) << r.agentId;
    EXPECT_FALSE(monitor.poll(kNow + 11'000, kDayStart)) << "the nested root finds nothing new";
    EXPECT_EQ(monitor.trackedFiles(), 2u);
}

TEST(MultiLocation, ARootThatResolvesToAnEarlierRootIsReadOnce) {
    TempDir directory;
    const auto root = directory.file("codex/sessions");
    copyCodex({kModern}, root);
    fs::create_directory_symlink(root, directory.file("link"));
    Monitor monitor(codex({at(root), at(directory.file("link"))}));
    monitor.start(kNow, kDayStart);
    EXPECT_EQ(monitor.trackedFiles(), 1u);
    EXPECT_EQ(monitor.sessionCount(SourceKind::Codex, kDayStart), 1);
    monitor.poll(kNow + 11'000, kDayStart);
    EXPECT_EQ(monitor.trackedFiles(), 1u) << "also after a rediscovery";
}

// MARK: - Databases

TEST(MultiLocation, EveryOpencodeDatabaseIsRead) {
    TempDir directory;
    opencodeCopy(directory.file("a"));
    opencodeCopy(directory.file("b"), "_b");
    LocationLists lists;
    lists.set(RootFamily::OpenCode, {at(directory.file("a")), at(directory.file("b/opencode.db"))});
    Monitor monitor(configOf(lists, {SourceKind::OpenCode}));
    monitor.start(kNow, 0);
    EXPECT_EQ(keysOf(monitor), (std::set<std::string>{"msg_oc1", "msg_oc2", "cmp_1", "msg_oc3", "msg_oc1_b", "msg_oc2_b", "cmp_1_b", "msg_oc3_b"}));
    EXPECT_EQ(monitor.parentOf("opencode:ses_sub_b"), std::optional<std::string>("opencode:ses_main_b"));
    EXPECT_EQ(monitor.sessionCount(SourceKind::OpenCode, 0), 4);
    EXPECT_EQ(rowOf(monitor.snapshot(kNow, 0), SourceKind::OpenCode).path, directory.file("a"));
}

TEST(MultiLocation, ASessionAnotherOpencodeDatabaseProvidesIsSkipped) {
    TempDir directory;
    opencodeCopy(directory.file("live"));
    opencodeCopy(directory.file("backup"));
    LocationLists lists;
    lists.set(RootFamily::OpenCode, {at(directory.file("live")), at(directory.file("backup"))});
    Monitor monitor(configOf(lists, {SourceKind::OpenCode}));
    monitor.start(kNow, 0);
    EXPECT_EQ(monitor.responses().size(), 4u);
    EXPECT_EQ(monitor.sessionCount(SourceKind::OpenCode, 0), 2);
    monitor.poll(kNow + 11'000, 0);
    EXPECT_EQ(monitor.sessionCount(SourceKind::OpenCode, 0), 2) << "also after a rediscovery";

    Deduper deduper;
    opencode::SourceSet set({directory.file("live"), directory.file("backup")}, deduper);
    const auto update = set.seed(0);
    EXPECT_FALSE(set.sources()[0]->agents().empty());
    EXPECT_TRUE(set.sources()[1]->agents().empty()) << "the copy's sessions stay with the first";
    EXPECT_EQ(update.responses.size(), 4u);
    const auto again = set.rediscover(kNow, 0);
    EXPECT_TRUE(set.sources()[1]->agents().empty());
    EXPECT_TRUE(again.seeded.responses.empty() && again.live.responses.empty());
}

TEST(MultiLocation, ASessionAnotherZcodeDatabaseProvidesIsSkipped) {
    TempDir directory;
    for (const char* name : {"a", "b"}) {
        fs::create_directories(directory.file(name));
        fs::copy_file(fixtures::fixtureDir() / "zcode/cli/db/db.sqlite", directory.file(std::string(name) + "/db.sqlite"));
    }
    const auto monitorOf = [&](const std::vector<std::string>& names) {
        LocationLists lists;
        LocationList list;
        for (const auto& name : names) list.push_back(at(directory.file(name)));
        lists.set(RootFamily::ZCode, list);
        auto monitor = std::make_unique<Monitor>(configOf(lists, {SourceKind::ZCode}));
        monitor->start(kNow, kDayStart);
        return monitor;
    };
    const auto one = monitorOf({"a"}), two = monitorOf({"a", "b"});
    const auto count = one->sessionCount(SourceKind::ZCode, kDayStart);
    EXPECT_GT(count, 0);
    EXPECT_EQ(two->sessionCount(SourceKind::ZCode, kDayStart), count);
    EXPECT_EQ(keysOf(*two), keysOf(*one));
    EXPECT_EQ(two->agents().size(), one->agents().size());

    Deduper deduper;
    zcode::SourceSet set({directory.file("a"), directory.file("b")}, deduper);
    set.seed(kDayStart);
    EXPECT_FALSE(set.sources()[0]->agents().empty());
    EXPECT_TRUE(set.sources()[1]->agents().empty()) << "the copy's sessions stay with the first";
    EXPECT_FALSE(set.unsupported());
}

// MARK: - Hermes Agent

TEST(MultiLocation, HermesReadsTheHomeAndAnAddedLogsFolder) {
    TempDir home, other;
    home.write("logs/agent.log", hermesTurn("20261020_120000_aaaaaa", "12:00:00", "12:00:02"));
    other.write("logs/agent.log", hermesTurn("20261020_120000_bbbbbb", "12:00:00", "12:00:03"));
    LocationLists lists;
    lists.set(RootFamily::Hermes, {kAutomatic, at(other.file("logs"))});
    const auto environment = environmentOf({{"HERMES_HOME", home.path.string()}});
    const auto locations = SourceLocations::resolve(lists, environment, "/nonexistent-home");
    EXPECT_EQ(locations.hermesFolders(), (std::vector<std::string>{home.file("logs"), other.file("logs")}));

    Deduper deduper;
    hermes::Source source(locations.hermesRoots, deduper, TimeZone::utc());
    std::set<std::string> keys;
    for (const auto& [response, completion] : source.seed(0).responses) keys.insert(response.key);
    EXPECT_EQ(keys, (std::set<std::string>{"hermes:20261020_120000_aaaaaa:1:2026-10-20 12:00:02,000",
                                           "hermes:20261020_120000_bbbbbb:1:2026-10-20 12:00:03,000"}));

    Monitor monitor(configOf(lists, {SourceKind::Hermes}, environment));
    monitor.start(kNow, 0);
    const auto snap = monitor.snapshot(kNow, 0);
    EXPECT_EQ(rowOf(snap, SourceKind::Hermes).path, home.path.string());
    EXPECT_EQ(rowOf(snap, SourceKind::Hermes).sessions, 2);
}

// MARK: - Gemini CLI configured effort

TEST(MultiLocation, EachGeminiRootTakesItsOwnSettings) {
    TempDir directory;
    const auto settings = [](const std::string& level) {
        return R"({"modelConfigs":{"overrides":[{"match":{"model":"gemini-3-pro"},"modelConfig":{"generateContentConfig":{"thinkingConfig":{"thinkingLevel":")" +
               level + R"("}}}}]}})";
    };
    directory.write("home/.gemini/settings.json", settings("HIGH"));
    directory.write("work/.gemini/settings.json", settings("LOW"));
    fs::create_directories(directory.file("home/.gemini/tmp"));
    fs::create_directories(directory.file("work/.gemini/tmp"));
    LocationLists lists;
    lists.set(RootFamily::Gemini, {kAutomatic, at(directory.file("work/.gemini/tmp"))});
    const auto locations = SourceLocations::resolve(lists, environmentOf({{"GEMINI_CLI_HOME", directory.file("home")}}), "/nonexistent-home");
    ASSERT_EQ(locations.geminiRoots.size(), 2u);
    ModeContext context;
    for (const auto& [root, file] : locations.geminiSettings) context.setGeminiSettings(root, file);
    context.refresh();
    const auto effort = [&](const std::string& root) {
        return effort_speed::current(ModeKind::Effort, {}, {}, context.configured(RootFamily::Gemini, root, "gemini-3-pro")).mode;
    };
    EXPECT_NE(effort(locations.geminiRoots[0]), "unknown");
    EXPECT_NE(effort(locations.geminiRoots[0]), effort(locations.geminiRoots[1])) << "the home's file and the added folder's file differ";
}

// MARK: - History

TEST(MultiLocation, HistoryReadsEveryLocationAndEachDatabaseOnce) {
    TempDir directory;
    const auto official = directory.file("official/sessions"), company = directory.file("company/sessions");
    copyCodex({kModern}, official);
    copyCodex({kLegacy}, company);
    opencodeCopy(directory.file("oc-live"));
    opencodeCopy(directory.file("oc-copy"));
    opencodeCopy(directory.file("oc-other"), "_b");
    for (const char* name : {"z-live", "z-copy"}) {
        fs::create_directories(directory.file(name));
        fs::copy_file(fixtures::fixtureDir() / "zcode/cli/db/db.sqlite", directory.file(std::string(name) + "/db.sqlite"));
    }
    const std::set<SourceKind> enabled{SourceKind::Codex, SourceKind::OpenCode, SourceKind::ZCode};
    const auto locationsOf = [](const LocationList& codexList, const LocationList& opencodeList, const LocationList& zcodeList) {
        LocationLists lists;
        lists.set(RootFamily::Codex, codexList);
        lists.set(RootFamily::OpenCode, opencodeList);
        lists.set(RootFamily::ZCode, zcodeList);
        return SourceLocations::resolve(lists, environmentOf({}), "/nonexistent-home");
    };
    const auto requests = [](const history::Analyzer& analyzer, SourceKind source) {
        const std::set<SourceKind> only{source};
        int total = 0;
        for (const auto& day : analyzer.days(&only)) total += static_cast<int>(day.totals.requests);
        return total;
    };
    const auto analyse = [&](const std::string& name, const SourceLocations& locations) {
        auto store = history::Store::open(directory.file("stores/" + name + ".sqlite"));
        auto analyzer = std::make_unique<history::Analyzer>(*store, std::make_shared<pricing::PriceBook>());
        analyzer->run(locations, enabled, TimeZone::utc(), "UTC");
        return std::make_pair(std::move(store), std::move(analyzer));
    };
    fs::create_directories(directory.file("stores"));
    const auto single = analyse("single", locationsOf({at(official)}, {at(directory.file("oc-live"))}, {at(directory.file("z-live"))}));
    const auto other = analyse("other", locationsOf({at(company)}, {at(directory.file("oc-other"))}, {at(directory.file("z-live"))}));
    fs::create_directory_symlink(official, directory.file("official-link"));
    const auto all = locationsOf({at(official), at(company), at(directory.file("official-link"))},
                                 {at(directory.file("oc-live")), at(directory.file("oc-copy")), at(directory.file("oc-other"))},
                                 {at(directory.file("z-live")), at(directory.file("z-copy"))});
    auto store = history::Store::open(directory.file("stores/all.sqlite"));
    history::Analyzer both(*store, std::make_shared<pricing::PriceBook>());
    const auto first = both.run(all, enabled, TimeZone::utc(), "UTC");
    EXPECT_EQ(first.filesDiscovered, 2 + 3 + 2);
    EXPECT_GT(requests(*single.second, SourceKind::Codex), 0);
    EXPECT_EQ(requests(both, SourceKind::Codex), requests(*single.second, SourceKind::Codex) + requests(*other.second, SourceKind::Codex));
    EXPECT_EQ(requests(both, SourceKind::OpenCode), requests(*single.second, SourceKind::OpenCode) + requests(*other.second, SourceKind::OpenCode))
        << "the copied database adds nothing";
    EXPECT_GT(requests(both, SourceKind::ZCode), 0);
    EXPECT_EQ(requests(both, SourceKind::ZCode), requests(*single.second, SourceKind::ZCode)) << "the copied ZCode database adds nothing";
    const auto again = both.run(all, enabled, TimeZone::utc(), "UTC");
    EXPECT_EQ(again.filesAnalyzed, 0);
    EXPECT_EQ(again.sessionsAnalyzed, 0) << "each database keeps its own identity";
}
