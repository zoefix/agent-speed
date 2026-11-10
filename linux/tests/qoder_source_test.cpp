#include <gtest/gtest.h>

#include <algorithm>
#include <chrono>
#include <ctime>
#include <set>

#include "agentspeed/claude_parser.hpp"
#include "agentspeed/history_analyzer.hpp"
#include "agentspeed/history_store.hpp"
#include "agentspeed/location_entries.hpp"
#include "agentspeed/monitor.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/qoder_source.hpp"
#include "agentspeed/tz.hpp"
#include "qoder_test_support.hpp"

// Qoder IDE's and Qoder CLI's rules one by one (METRICS 4.18, 4.19): what a row says, the startup window, live reading
// with turn records, several databases, the monitor and the location lists.
using namespace agentspeed;
using qoder_test::IdeDatabase;
using qoder_test::TempDir;
namespace fs = std::filesystem;

namespace {

const std::int64_t kNowMs =
    std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
// A start of day two hours ago: every row these tests write near now is today's.
const std::int64_t kTodayStartMs = kNowMs - 2 * 3'600'000;
// A fixed day for the rules that need no clock: 2026-10-20T00:00:00Z.
constexpr std::int64_t kDayStart = 1792454400000;

Environment environmentOf(std::map<std::string, std::string> values) {
    return [values](const std::string& name) -> std::optional<std::string> {
        const auto it = values.find(name);
        return it == values.end() ? std::nullopt : std::optional<std::string>(it->second);
    };
}

std::vector<std::string> keysOf(const qoder::Update& update) {
    std::vector<std::string> out;
    for (const auto& [response, completion] : update.responses) out.push_back(response.key);
    return out;
}

std::vector<qoder::Update> refreshed(qoder::Source& source) { return source.refresh(); }

SourceLocations qoderLocations(const LocationList& list, const Environment& environment = environmentOf({})) {
    LocationLists lists;
    lists.set(RootFamily::Qoder, list);
    return SourceLocations::resolve(lists, environment, "/nonexistent-home");
}

MonitorConfig qoderConfig(const LocationList& list) {
    MonitorConfig config;
    config.locations = qoderLocations(list);
    config.enabled = {SourceKind::Qoder};
    return config;
}

const SourceRow& rowOf(const Snapshot& snap, SourceKind kind) {
    return *std::find_if(snap.sources.begin(), snap.sources.end(), [&](const SourceRow& r) { return r.source == kind; });
}

// RFC 3339 in UTC with milliseconds.
std::string isoOf(std::int64_t ms) {
    const auto seconds = static_cast<std::time_t>(ms / 1000);
    std::tm tm{};
    gmtime_r(&seconds, &tm);
    char buffer[96];
    std::snprintf(buffer, sizeof buffer, "%04d-%02d-%02dT%02d:%02d:%02d.%03dZ", tm.tm_year + 1900, tm.tm_mon + 1, tm.tm_mday, tm.tm_hour,
                  tm.tm_min, tm.tm_sec, static_cast<int>(ms % 1000));
    return buffer;
}

class Collector : public ResponseSink {
public:
    void onResponse(const ParsedResponse& response, bool completion) override { emitted.emplace_back(response, completion); }
    std::vector<std::pair<ParsedResponse, bool>> emitted;
};

}  // namespace

// MARK: - Rows

TEST(Qoder, TheJsonColumnsGiveTokensTierAndFinish) {
    EXPECT_EQ(qoder::tokensOf(R"({"prompt_tokens":16846,"completion_tokens":481,"cached_tokens":2048,"max_input_tokens":200000})"),
              (qoder::Tokens{16846, 481, 2048}));
    EXPECT_EQ(qoder::tokensOf(R"({"prompt_tokens":-3,"completion_tokens":12.9,"cached_tokens":"7"})"), (qoder::Tokens{0, 12, 0}));
    EXPECT_EQ(qoder::tokensOf(R"({"prompt_tokens":1e20})"), (qoder::Tokens{1'000'000'000'000, 0, 0}));
    EXPECT_FALSE(qoder::tokensOf("[1]"));
    EXPECT_FALSE(qoder::tokensOf(R"({"prompt_tokens":NaN})"));
    EXPECT_FALSE(qoder::tokensOf(R"({"prompt_tokens":1} x)"));

    EXPECT_EQ(qoder::modelKeyOf(R"({"model_key":" auto ","is_reasoning":false})"), "auto");
    EXPECT_EQ(qoder::modelKeyOf(R"({"model_key":"  "})"), std::nullopt);
    EXPECT_EQ(qoder::modelKeyOf(R"({"model_key":3})"), std::nullopt);
    EXPECT_EQ(qoder::modelKeyOf("auto"), std::nullopt);

    EXPECT_EQ(qoder::finishedAtOf(R"({"ai-coding/turn-info":{"finishedAt":1792490464950.7}})"), 1792490464950);
    EXPECT_EQ(qoder::finishedAtOf(R"({"ai-coding/turn-info":{"finishedAt":1e999}})"), std::nullopt);
    EXPECT_EQ(qoder::finishedAtOf(R"({"ai-coding/turn-info":"done"})"), std::nullopt);
    EXPECT_EQ(qoder::finishedAtOf(R"({"finishedAt":1})"), std::nullopt);

    // A row's call: the prompt includes the cache read; the cache read is at most the prompt; no id names the row.
    const auto call = qoder::response({1200, 300, 5000}, "s", std::nullopt, 7, 1'000, 2'000, std::string("lite"), "db#s");
    ASSERT_TRUE(call);
    EXPECT_EQ(call->key, "qoder:s:row:7");
    EXPECT_EQ(call->tokens, (TokenCounts{1200, 1200, 0, 300, 0, 0}));
    EXPECT_EQ(call->startMs, 1'000);
    EXPECT_EQ(call->firstMs, std::nullopt);
    EXPECT_EQ(call->model, "lite");
    EXPECT_EQ(call->provider, std::nullopt);
    EXPECT_TRUE(call->final);
    EXPECT_FALSE(call->fast);
    EXPECT_EQ(call->source, SourceKind::Qoder);
    EXPECT_EQ(qoder::callKey("s", std::string("6f1d-0002"), 7), "qoder:6f1d-0002");
    EXPECT_FALSE(qoder::response({0, 0, 9}, "s", std::nullopt, 1, std::nullopt, 0, std::nullopt, "a")) << "no tokens, no call";
}

TEST(Qoder, TheProjectIsTheFolderTheUriNames) {
    EXPECT_EQ(qoder::projectPath(std::string("file:///c%3A/work/app")), "c:/work/app");
    EXPECT_EQ(qoder::projectPath(std::string("file:///Users/dev/projects/aurora%20labs")), "/Users/dev/projects/aurora labs");
    EXPECT_EQ(qoder::projectPath(std::string("FILE://host/share/x")), "/share/x");
    EXPECT_EQ(qoder::projectPath(std::string("file:///%E4%B8%AD%ZZ")), "/\xE4\xB8\xAD%ZZ");
    EXPECT_EQ(qoder::projectPath(std::string("file:///%FF")), "/\xEF\xBF\xBD");
    EXPECT_EQ(qoder::projectPath(std::string("C:\\Users\\dev\\work\\ledger")), "C:\\Users\\dev\\work\\ledger");
    EXPECT_EQ(qoder::projectPath(std::string("file://")), std::nullopt);
    EXPECT_EQ(qoder::projectPath(std::string(" \t")), std::nullopt);
    EXPECT_EQ(qoder::projectPath(std::nullopt), std::nullopt);
}

// MARK: - Reading

// The startup window from the highest rowid down; each session's call starts at its row just below the window. Reading
// never creates a `-wal` or `-shm` next to a database the core closed.
TEST(Qoder, TheStartupWindowTimesItsFirstCallFromTheRowBelowIt) {
    TempDir temp;
    IdeDatabase database(temp.file("Qoder/SharedClientCache/cache/db/local.db"));
    database.session("old-pond", "file:///w/old-pond");
    database.session("aurora", "file:///w/aurora");
    database.row("m1", "old-pond", "user", kDayStart - 3 * 3'600'000);
    database.call("m2", "old-pond", kDayStart - 3 * 3'600'000 + 5'000, "r-old");
    database.row("m3", "aurora", "user", kDayStart - 20 * 60'000);
    database.call("m4", "aurora", kDayStart - 10 * 60'000, "r1");
    database.row("m5", "aurora", "tool", kDayStart - 9 * 60'000);
    database.call("m6", "aurora", kDayStart - 8 * 60'000, "r2", 2000, 50, 1500, "efficient");
    database.close();
    ASSERT_EQ(qoder_test::sideFiles(temp.path), std::vector<std::string>{});

    Deduper deduper;
    qoder::Source source({qoder::Root::location(temp.file("Qoder"))}, deduper);
    const auto seeded = source.seed(kDayStart);
    ASSERT_EQ(seeded.size(), 1u);
    EXPECT_TRUE(seeded[0].seeded);
    EXPECT_EQ(keysOf(seeded[0]), (std::vector<std::string>{"qoder:m4", "qoder:m6"}));
    EXPECT_EQ(seeded[0].responses[0].first.startMs, kDayStart - 20 * 60'000) << "the row just below the window";
    EXPECT_EQ(seeded[0].responses[1].first.startMs, kDayStart - 9 * 60'000);
    const auto id = database.path() + "#aurora";
    ASSERT_TRUE(seeded[0].agents.contains(id));
    const auto& agent = seeded[0].agents.at(id);
    EXPECT_EQ(agent.phase, Phase::Tools);
    EXPECT_EQ(agent.project, "aurora");
    EXPECT_EQ(agent.cwdPath, "/w/aurora");
    EXPECT_EQ(agent.model, "efficient");
    EXPECT_EQ(agent.lastLineMs, kDayStart - 8 * 60'000) << "the row below the window is not processed";
    EXPECT_FALSE(seeded[0].agents.contains(database.path() + "#old-pond")) << "only rows of the startup window";
    EXPECT_EQ(qoder_test::sideFiles(temp.path), std::vector<std::string>{}) << "read-only, immutable for a closed WAL database";
    EXPECT_TRUE(source.refresh().empty());
}

// While the IDE runs: new rows above the highest rowid, each call timed from its session's previous row (also one of an
// earlier read); a call waits in `tools` until its turn record says the request finished.
TEST(Qoder, LiveRowsAndTurnRecordsMoveThePhase) {
    TempDir temp;
    IdeDatabase database(temp.file("db/local.db"));
    database.session("aurora", "/w/aurora");
    const auto t0 = kDayStart + 3'600'000;
    database.row("u1", "aurora", "user", t0, "q1");
    Deduper deduper;
    qoder::Source source({qoder::Root::location(temp.file("db"))}, deduper);
    const auto id = database.path() + "#aurora";
    auto updates = source.seed(kDayStart);
    ASSERT_EQ(updates.size(), 1u);
    EXPECT_EQ(updates[0].agents.at(id).phase, Phase::Generating);
    EXPECT_EQ(updates[0].agents.at(id).inFlightStartMs, t0);

    database.call("a1", "aurora", t0 + 4'000, "q1");
    updates = refreshed(source);
    ASSERT_EQ(updates.size(), 1u);
    EXPECT_FALSE(updates[0].seeded);
    ASSERT_EQ(updates[0].responses.size(), 1u);
    EXPECT_EQ(updates[0].responses[0].first.key, "qoder:a1");
    EXPECT_EQ(updates[0].responses[0].first.startMs, t0) << "the previous row, read before";
    EXPECT_TRUE(updates[0].responses[0].second) << "a new call is a live completion";
    EXPECT_EQ(updates[0].agents.at(id).phase, Phase::Tools) << "no turn record yet";

    // Only the turn record changes: the waiting call's request finished.
    database.finished("q1", "aurora", t0 + 4'050);
    updates = refreshed(source);
    ASSERT_EQ(updates.size(), 1u);
    EXPECT_TRUE(updates[0].responses.empty());
    EXPECT_EQ(updates[0].agents.at(id).phase, Phase::Idle);
    EXPECT_EQ(updates[0].agents.at(id).inFlightStartMs, std::nullopt);

    // A tool row, then a call whose turn record is already written: idle at once.
    database.row("t1", "aurora", "tool", t0 + 10'000, "q2");
    database.call("a2", "aurora", t0 + 12'000, "q2", 3000, 200, 2000, "performance");
    database.finished("q2", "aurora", t0 + 12'000);
    updates = refreshed(source);
    ASSERT_EQ(updates.size(), 1u);
    ASSERT_EQ(updates[0].responses.size(), 1u);
    EXPECT_EQ(updates[0].responses[0].first.startMs, t0 + 10'000);
    EXPECT_EQ(updates[0].agents.at(id).phase, Phase::Idle);
    EXPECT_EQ(updates[0].agents.at(id).model, "performance");

    // A stored copy older than the row that set the phase never moves it back; other roles only note the time.
    database.row("u2", "aurora", "user", t0 + 20'000, "q3");
    database.call("old-copy", "aurora", t0 + 15'000, "q-old", 10, 1, 0);
    database.row("s1", "aurora", "system", t0 + 21'000);
    updates = refreshed(source);
    ASSERT_EQ(updates.size(), 1u);
    const auto& agent = updates[0].agents.at(id);
    EXPECT_EQ(agent.phase, Phase::Generating);
    EXPECT_EQ(agent.inFlightStartMs, t0 + 20'000);
    EXPECT_EQ(agent.lastLineMs, t0 + 21'000);
    EXPECT_EQ(source.activeSessionCount(kDayStart), 1);
    EXPECT_TRUE(source.refresh().empty());
}

// A row without a time, or whose role is no string, is malformed: it counts toward `lastLineMs` when it has a time, it
// does not end the startup read, and the next call has no start.
TEST(Qoder, MalformedRowsAreCountedAndNeverEndTheStartupRead) {
    TempDir temp;
    IdeDatabase database(temp.file("db/local.db"));
    database.session("old-pond");
    database.session("aurora");
    database.call("c0", "old-pond", kDayStart - 3'600'000, "r0");
    database.call("c1", "aurora", kDayStart + 60'000, "r1");
    database.row("u1", "aurora", "user", kDayStart + 61'000);
    database.execute("UPDATE chat_message SET gmt_create = 'soon' WHERE id = 'u1'");
    database.call("c2", "aurora", kDayStart + 90'000, "r2");
    database.row("x1", "aurora", "user", kDayStart + 95'000);
    database.execute("UPDATE chat_message SET role = NULL WHERE id = 'x1'");
    database.call("c3", "aurora", kDayStart + 99'000, "r3", 5, 0, 0);
    database.execute("UPDATE chat_message SET token_info = '[1]' WHERE id = 'c3'");
    Deduper deduper;
    qoder::Source source({qoder::Root::location(temp.file("db"))}, deduper);
    const auto seeded = source.seed(kDayStart);
    ASSERT_EQ(seeded.size(), 1u);
    EXPECT_EQ(keysOf(seeded[0]), (std::vector<std::string>{"qoder:c1", "qoder:c2"})) << "a token_info that is no object is no call";
    EXPECT_EQ(seeded[0].responses[1].first.startMs, std::nullopt) << "the previous row has no time";
    const auto& agent = seeded[0].agents.at(database.path() + "#aurora");
    EXPECT_EQ(agent.malformedRows, 2);
    EXPECT_EQ(agent.lastLineMs, kDayStart + 99'000);
    EXPECT_EQ(agent.phase, Phase::Tools) << "an assistant row without a call still sets the phase";
    EXPECT_EQ(source.malformedRows(), 2);
    EXPECT_FALSE(seeded[0].agents.contains(database.path() + "#old-pond"));
}

// A session `chat_session` does not list yet is not read; once it is, its call starts at its own earlier row.
TEST(Qoder, ASessionIsReadOnceItIsListed) {
    TempDir temp;
    IdeDatabase database(temp.file("db/local.db"));
    database.session("aurora");
    database.call("a1", "aurora", kDayStart + 1'000, "q1");
    Deduper deduper;
    qoder::Source source({qoder::Root::location(temp.file("db"))}, deduper);
    source.seed(kDayStart);
    database.row("u1", "comet", "user", kDayStart + 5'000);
    auto updates = refreshed(source);
    for (const auto& update : updates) EXPECT_FALSE(update.agents.contains(database.path() + "#comet"));
    database.session("comet", "file:///w/comet", "aurora");
    database.call("c1", "comet", kDayStart + 9'000, "q2");
    updates = refreshed(source);
    ASSERT_EQ(updates.size(), 1u);
    ASSERT_EQ(updates[0].responses.size(), 1u);
    EXPECT_EQ(updates[0].responses[0].first.startMs, kDayStart + 5'000);
    const auto& comet = updates[0].agents.at(database.path() + "#comet");
    EXPECT_TRUE(comet.isSubagent);
    EXPECT_EQ(comet.parentAgentId, database.path() + "#aurora");
    EXPECT_EQ(comet.project, "comet");
}

TEST(Qoder, AReplacedDatabaseIsReadAgainLikeAtStartup) {
    TempDir temp;
    const auto path = temp.file("db/local.db");
    {
        IdeDatabase database(path);
        database.session("aurora");
        database.call("a1", "aurora", kDayStart + 1'000, "q1");
    }
    Deduper deduper;
    qoder::Source source({qoder::Root::location(temp.file("db"))}, deduper);
    source.seed(kDayStart);
    const auto other = temp.file("other/local.db");
    {
        IdeDatabase database(other);
        database.session("aurora");
        database.call("a1", "aurora", kDayStart + 1'000, "q1");
        database.call("a2", "aurora", kDayStart + 4'000, "q2");
        database.row("u1", "aurora", "user", kDayStart + 6'000);
    }
    fs::rename(other, path);
    const auto updates = source.refresh();
    ASSERT_EQ(updates.size(), 1u);
    EXPECT_TRUE(updates[0].seeded) << "a replaced database is read like at startup";
    ASSERT_EQ(updates[0].responses.size(), 2u);
    EXPECT_EQ(updates[0].responses[0].first.key, "qoder:a1");
    EXPECT_FALSE(updates[0].responses[0].second) << "its keys update their responses";
    EXPECT_TRUE(updates[0].responses[1].second);
    EXPECT_EQ(updates[0].agents.at(path + "#aurora").phase, Phase::Generating);
}

TEST(Qoder, ADatabaseIsNeverALink) {
    TempDir temp;
    IdeDatabase database(temp.file("real/local.db"));
    database.session("aurora");
    database.close();
    fs::create_directories(temp.file("linked"));
    fs::create_symlink(database.path(), temp.file("linked/local.db"));
    EXPECT_EQ(qoder::Root::location(temp.file("linked")).databaseNow(), std::nullopt);
    EXPECT_EQ(qoder::Root::database(temp.file("linked/local.db")).databaseNow(), std::nullopt);
    fs::create_directory_symlink(temp.file("real"), temp.file("folder-link"));
    EXPECT_EQ(qoder::Root::location(temp.file("folder-link")).databaseNow(), temp.file("folder-link/local.db"))
        << "the folders above it may be links";
}

// A session an earlier database of the list has is skipped in a later one; a key another database owns is ignored.
TEST(Qoder, ACopiedSessionInALaterDatabaseIsSkipped) {
    TempDir temp;
    IdeDatabase first(temp.file("a/local.db")), second(temp.file("b/local.db"));
    for (auto* database : {&first, &second}) {
        database->session("aurora");
        database->call("a1", "aurora", kDayStart + 1'000, "q1");
    }
    first.session("zeta");
    first.call("z1", "zeta", kDayStart + 1'500, "q5");
    // The later copy also has a call the earlier database lacks: the whole session is skipped, not only its known keys.
    second.call("a3", "aurora", kDayStart + 2'000, "q3");
    second.session("comet");
    second.call("z1", "comet", kDayStart + 3'000, "q9");
    second.call("c2", "comet", kDayStart + 4'000, "q10");
    Deduper deduper;
    qoder::Source source({qoder::Root::location(temp.file("a")), qoder::Root::location(temp.file("b"))}, deduper);
    std::vector<std::string> keys;
    for (const auto& update : source.seed(kDayStart)) {
        for (const auto& [response, completion] : update.responses) keys.push_back(response.agentId + " " + response.key);
    }
    std::sort(keys.begin(), keys.end());
    EXPECT_EQ(keys, (std::vector<std::string>{first.path() + "#aurora qoder:a1", first.path() + "#zeta qoder:z1",
                                              second.path() + "#comet qoder:c2"}))
        << "a key the earlier database owns is ignored";
    EXPECT_EQ(source.activeSessionCount(kDayStart), 3);
    for (const auto& agent : source.readers()[1]->agents()) EXPECT_NE(agent.sessionId, "aurora");

    fs::create_directories(temp.file("history"));
    auto store = history::Store::open(temp.file("history/history.sqlite"));
    history::Analyzer analyzer(*store, std::make_shared<pricing::PriceBook>());
    const std::set<SourceKind> enabled{SourceKind::Qoder};
    const auto report =
        analyzer.run(qoderLocations({DataLocation::at(temp.file("a")), DataLocation::at(temp.file("b"))}), enabled, TimeZone::utc(), "UTC");
    EXPECT_EQ(report.sessionsAnalyzed, 3);
    std::int64_t requests = 0;
    for (const auto& day : analyzer.days(&enabled)) requests += day.totals.requests;
    EXPECT_EQ(requests, 3);
}

// MARK: - Monitor

// Through the monitor and the engine: today's calls, the tiers as models, a sub-agent session under its parent and
// today's sessions with the sub-agent counted.
TEST(Qoder, TheMonitorShowsTodaysCallsAndSubAgents) {
    TempDir temp;
    IdeDatabase database(temp.file("Qoder/SharedClientCache/cache/db/local.db"));
    database.session("aurora", "file:///w/aurora");
    database.session("scout", "file:///w/aurora", "aurora");
    database.session("orphan", "file:///w/orphan", "gone");
    database.row("u1", "aurora", "user", kNowMs - 60'000);
    database.call("a1", "aurora", kNowMs - 55'000, "q1", 1000, 100, 400);
    database.row("u2", "scout", "user", kNowMs - 50'000);
    database.call("a2", "scout", kNowMs - 45'000, "q2", 500, 20, 0, "lite");
    database.row("u3", "orphan", "user", kNowMs - 40'000);
    Monitor monitor(qoderConfig({DataLocation::at(temp.file("Qoder"))}));
    monitor.start(kNowMs, kTodayStartMs);
    auto snap = monitor.snapshot(kNowMs, kTodayStartMs);
    EXPECT_EQ(snap.todayRequests, 2);
    EXPECT_EQ(snap.todayOutput, 120);
    EXPECT_EQ(snap.todayInput, 1500);
    const auto& row = rowOf(snap, SourceKind::Qoder);
    EXPECT_EQ(row.sessions, 3) << "sub-agent sessions count too";
    EXPECT_TRUE(row.available);
    EXPECT_EQ(row.path, temp.file("Qoder"));
    EXPECT_TRUE(monitor.takeCompletions().empty()) << "the startup read raises no event";
    const auto aurora = database.path() + "#aurora", scout = database.path() + "#scout", orphan = database.path() + "#orphan";
    EXPECT_EQ(monitor.parentOf(scout), aurora);
    EXPECT_EQ(monitor.parentOf(orphan), std::nullopt) << "its parent session is not tracked";
    EXPECT_TRUE(monitor.agents().at(orphan).isSubagent);
    EXPECT_EQ(monitor.agents().at(scout).model, "lite");
    EXPECT_EQ(monitor.cwdOf(aurora), "/w/aurora");

    database.call("a3", "aurora", kNowMs - 1'000, "q3", 100, 30, 0);
    EXPECT_TRUE(monitor.poll(kNowMs + 1000, kTodayStartMs));
    snap = monitor.snapshot(kNowMs + 1000, kTodayStartMs);
    EXPECT_EQ(snap.todayRequests, 3);
    EXPECT_EQ(monitor.takeCompletions().size(), 1u);
    const auto agent = std::find_if(snap.agents.begin(), snap.agents.end(), [&](const AgentRow& a) { return a.id == aurora; });
    ASSERT_NE(agent, snap.agents.end());
    EXPECT_EQ(agent->project, "aurora");
    EXPECT_EQ(agent->model, "auto");
    EXPECT_EQ(agent->phase, Phase::Tools);
}

TEST(Qoder, ADatabaseWithoutTheColumnsIsUnsupported) {
    TempDir temp;
    fs::create_directories(temp.file("db"));
    {
        const auto writer = sqlite::Connection::open(temp.file("db/local.db"), sqlite::OpenMode::ReadWrite);
        writer.execute("CREATE TABLE chat_session (session_id varchar(64) PRIMARY KEY, project_uri text);"
                       "CREATE TABLE chat_message (id varchar(64) PRIMARY KEY, session_id varchar(64), role text, gmt_create INTEGER);");
    }
    Monitor monitor(qoderConfig({DataLocation::at(temp.file("db"))}));
    monitor.start(kNowMs, kTodayStartMs);
    const auto warnings = monitor.warnings();
    EXPECT_NE(std::find(warnings.unsupported.begin(), warnings.unsupported.end(), SourceKind::Qoder), warnings.unsupported.end());
    EXPECT_EQ(warnings.unreadableFiles, 0);
    EXPECT_TRUE(rowOf(monitor.snapshot(kNowMs, kTodayStartMs), SourceKind::Qoder).available);
}

// MARK: - Location lists

TEST(Qoder, SameLocationsAndTheAutomaticEntry) {
    TempDir temp;
    const auto database = temp.write("cfg/Qoder/SharedClientCache/cache/db/local.db", "");
    // The China edition is installed but has not written its database yet.
    fs::create_directories(temp.file("cfg/QoderCN/SharedClientCache/cache/db"));
    const auto key = [&](const std::string& relative) { return locationKey(temp.file(relative), RootFamily::Qoder); };
    EXPECT_EQ(key("cfg/Qoder"), database);
    EXPECT_EQ(key("cfg/Qoder/SharedClientCache"), database);
    EXPECT_EQ(key("cfg/Qoder/SharedClientCache/cache/db"), database);
    EXPECT_NE(key("cfg/Qoder/SharedClientCache/cache"), database);
    // Without a database yet, a folder names the database its layout would keep.
    fs::create_directories(temp.file("empty/SharedClientCache/cache/db"));
    EXPECT_EQ(key("empty"), temp.file("empty/SharedClientCache/cache/db/local.db"));
    EXPECT_EQ(key("empty/SharedClientCache"), key("empty"));
    EXPECT_EQ(key("missing"), temp.file("missing/local.db"));

    const auto environment = environmentOf({{"XDG_CONFIG_HOME", temp.file("cfg")}});
    const auto automatic = automaticKeys(RootFamily::Qoder, LocationLists(), environment, "/h");
    EXPECT_EQ(automatic.keys, (std::vector<std::string>{database, temp.file("cfg/QoderCN/SharedClientCache/cache/db/local.db")}));
    LocationLists lists;
    EXPECT_EQ(lists.add(temp.file("cfg/Qoder"), RootFamily::Qoder, automatic), AddOutcome::AlreadyAutomatic);
    EXPECT_EQ(lists.add(temp.file("cfg/QoderCN"), RootFamily::Qoder, automatic), AddOutcome::AlreadyAutomatic) << "the China edition too";
    EXPECT_EQ(lists.add(temp.file("portable/user-data"), RootFamily::Qoder, automatic), AddOutcome::Added);
    EXPECT_EQ(lists.add(temp.file("portable/user-data/SharedClientCache/cache/db"), RootFamily::Qoder, automatic), AddOutcome::Added)
        << "without a database, two layouts that do not exist name different files";
    lists.remove(RootFamily::Qoder, DataLocation::automaticEntry());
    EXPECT_EQ(lists.add(temp.file("cfg/Qoder/SharedClientCache"), RootFamily::Qoder, automatic), AddOutcome::RestoredAutomatic);
    EXPECT_TRUE(lists[RootFamily::Qoder].front().automatic);
    EXPECT_FALSE(automaticIsWiderThanDefault(RootFamily::Qoder));
    EXPECT_FALSE(acceptsDatabaseFile(RootFamily::Qoder));

    EXPECT_EQ(SourceLocations::defaultRoot(SourceKind::Qoder, environment, "/h"), temp.file("cfg/Qoder/SharedClientCache/cache/db"))
        << "Settings names the folder that holds local.db";
    EXPECT_EQ(automaticShown(RootFamily::Qoder, environment, "/h"),
              (std::vector<std::string>{temp.file("cfg/Qoder/SharedClientCache/cache/db"), temp.file("cfg/QoderCN/SharedClientCache/cache/db")}));
    const auto entry = locationEntry(RootFamily::Qoder, DataLocation::automaticEntry(), environment, "/h");
    ASSERT_EQ(entry.shown.size(), 2u);
    EXPECT_TRUE(entry.shown[0].second);
    EXPECT_FALSE(entry.shown[1].second) << "the China edition has no database";
    EXPECT_TRUE(entry.exists);
    EXPECT_EQ(SourceLocations::defaultRoot(SourceKind::Qoder, environmentOf({{"XDG_CONFIG_HOME", ""}}), "/h"),
              "/h/.config/Qoder/SharedClientCache/cache/db");

    LocationLists three;
    three.set(RootFamily::Qoder, {DataLocation::automaticEntry(), DataLocation::at(temp.file("cfg/Qoder")), DataLocation::at(temp.file("work"))});
    const auto l = SourceLocations::resolve(three, environmentOf({{"XDG_CONFIG_HOME", "/x"}}), "/h");
    ASSERT_EQ(l.qoderRoots.size(), 4u);
    EXPECT_EQ(l.qoderRoots[0], qoder::Root::database("/x/Qoder/SharedClientCache/cache/db/local.db"));
    EXPECT_EQ(l.qoderRoots[1], qoder::Root::database("/x/QoderCN/SharedClientCache/cache/db/local.db"));
    EXPECT_FALSE(l.qoderRoots[2].isDatabase);
    EXPECT_FALSE(l.readsSame(qoderLocations({DataLocation::at(temp.file("work"))})));
    EXPECT_EQ(qoder::databases({qoder::Root::database(database), qoder::Root::location(temp.file("cfg/Qoder"))}),
              std::vector<std::string>{database});
}

TEST(Qoder, ListsAndFamiliesOfBothSources) {
    const auto& order = locationListOrder();
    ASSERT_GE(order.size(), 3u);
    EXPECT_EQ(std::vector<RootFamily>(order.end() - 3, order.end()), (std::vector<RootFamily>{RootFamily::Devin, RootFamily::Qoder, RootFamily::QoderCli}));
    EXPECT_EQ(listName(RootFamily::Qoder), "qoder");
    EXPECT_EQ(listName(RootFamily::QoderCli), "qoderCli");
    EXPECT_EQ(familyOf(SourceKind::Qoder), RootFamily::Qoder);
    EXPECT_EQ(familyOf(SourceKind::QoderCli), RootFamily::QoderCli);
    EXPECT_EQ(sourceId(SourceKind::Qoder), "qoder");
    EXPECT_EQ(sourceId(SourceKind::QoderCli), "qoderCli");
    EXPECT_EQ(sourceFromId("qoderCli"), SourceKind::QoderCli);
    EXPECT_EQ(*MonitorConfig::allSources().rbegin(), SourceKind::QoderCli) << "last in the source order";
}

TEST(Qoder, CliLocationsAreProjectsFolders) {
    TempDir temp;
    fs::create_directories(temp.file("cn/projects"));
    const auto environment = environmentOf({{"QODERCN_CONFIG_DIR", temp.file("cn")}});
    const auto key = [&](const std::string& relative) { return locationKey(temp.file(relative), RootFamily::QoderCli); };
    EXPECT_EQ(key("cn"), key("cn/projects")) << "a config folder and its projects folder";
    EXPECT_EQ(qoder::cliRoots(environment, "/h"), (std::vector<std::string>{"/h/.qoder/projects", temp.file("cn/projects")}));
    EXPECT_EQ(SourceLocations::defaultRoot(SourceKind::QoderCli, environmentOf({{"QODER_CONFIG_DIR", "/srv/q"}}), "/h"), "/srv/q/projects");
    const auto automatic = automaticKeys(RootFamily::QoderCli, LocationLists(), environment, "/h");
    EXPECT_EQ(automatic.keys, (std::vector<std::string>{"/h/.qoder/projects", temp.file("cn/projects")}));
    LocationLists lists;
    EXPECT_EQ(lists.add(temp.file("cn"), RootFamily::QoderCli, automatic), AddOutcome::AlreadyAutomatic);
    EXPECT_EQ(lists.add(temp.file("work/.qoder"), RootFamily::QoderCli, automatic), AddOutcome::Added);
    EXPECT_EQ(lists.add(temp.file("work/.qoder/projects"), RootFamily::QoderCli, automatic), AddOutcome::AlreadyListed);
    EXPECT_EQ(automaticShown(RootFamily::QoderCli, environment, "/h"), (std::vector<std::string>{"/h/.qoder/projects", temp.file("cn/projects")}));
    const auto l = SourceLocations::resolve(lists, environment, "/h");
    EXPECT_EQ(l.qoderCliRoots, (std::vector<std::string>{"/h/.qoder/projects", temp.file("cn/projects"), temp.file("work/.qoder/projects")}));
}

// MARK: - Qoder CLI

// METRICS 4.19: Claude's parser with Qoder's keys, no response without usage, Qoder's input rule and no api-request.
TEST(QoderCli, ALineWithoutUsageClaimsItsKeyAndConsumesTheStart) {
    Deduper deduper;
    ClaudeParser parser("/q/projects/-w-atlas/s1.jsonl", deduper, ClaudeParser::Variant::QoderCli);
    Collector sink;
    const auto t = kDayStart + 3'600'000;
    const auto line = [&](const std::string& json) { parser.feed(json, sink); };
    EXPECT_EQ(parser.source(), SourceKind::QoderCli);
    line(R"({"type":"api-request","timestamp":")" + isoOf(t) + R"("})");
    EXPECT_EQ(parser.phase(), Phase::Idle) << "Qoder writes no api-request: any other line";
    EXPECT_EQ(parser.lastLineMs(), t);
    line(R"({"type":"user","entrypoint":"claude-desktop","cwd":"/w/atlas","message":{"role":"user","content":"go"},"timestamp":")" + isoOf(t + 1'000) + R"("})");
    EXPECT_EQ(parser.source(), SourceKind::QoderCli) << "entrypoint is not used";
    EXPECT_EQ(parser.phase(), Phase::Generating);
    EXPECT_EQ(parser.inFlightStartMs(), t + 1'000);
    line(R"({"type":"assistant","message":{"id":"m1","model":"auto","stop_reason":"tool_use","content":[{"type":"tool_use","id":"x","name":"Read"}]},"timestamp":")" +
         isoOf(t + 5'000) + R"("})");
    EXPECT_TRUE(sink.emitted.empty()) << "no usage, no response";
    EXPECT_EQ(parser.phase(), Phase::Tools);
    EXPECT_EQ(parser.model(), "auto");
    EXPECT_EQ(parser.project(), "atlas");

    // Billing usage: zeros are no usage either; the pending start is consumed, so the next call starts at its own row.
    line(R"({"type":"assistant","message":{"id":"m2","model":"auto","stop_reason":null,"usage":{"input_tokens":0,"output_tokens":0,"credits":3}},"timestamp":")" +
         isoOf(t + 6'000) + R"("})");
    EXPECT_TRUE(sink.emitted.empty());
    EXPECT_EQ(parser.phase(), Phase::Generating);
    EXPECT_EQ(parser.inFlightStartMs(), t + 6'000) << "no response and no pending start: the line's own time";
    line(R"({"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"x","content":"ok"}]},"timestamp":")" +
         isoOf(t + 7'000) + R"("})");
    line(R"({"type":"assistant","uuid":"u3","message":{"model":"claude-sonnet-4-5","stop_reason":"end_turn","usage":{"input_tokens":20341,"cache_read_input_tokens":20096,"output_tokens":50,"speed":"fast","output_tokens_details":{"thinking_tokens":9}}},"timestamp":")" +
         isoOf(t + 9'000) + R"("})");
    ASSERT_EQ(sink.emitted.size(), 1u);
    const auto& [first, completion] = sink.emitted[0];
    EXPECT_TRUE(completion);
    EXPECT_EQ(first.key, "qodercli:u3") << "message.id, else uuid";
    EXPECT_EQ(first.source, SourceKind::QoderCli);
    EXPECT_EQ(first.startMs, t + 7'000);
    EXPECT_EQ(first.endMs, t + 9'000);
    EXPECT_EQ(first.firstMs, std::nullopt);
    EXPECT_EQ(first.tokens, (TokenCounts{20341, 20096, 0, 50, 9, 0})) << "input_tokens already includes the cache read";
    EXPECT_FALSE(first.fast);
    EXPECT_TRUE(first.final);
    EXPECT_TRUE(first.modes.empty());
    EXPECT_EQ(parser.phase(), Phase::Idle);
    EXPECT_EQ(parser.model(), "claude-sonnet-4-5");

    // A custom Anthropic provider's own accounting: input below the cache, so Claude's sum.
    line(R"({"type":"user","message":{"role":"user","content":"again"},"timestamp":")" + isoOf(t + 20'000) + R"("})");
    line(R"({"type":"assistant","message":{"id":"m4","model":"claude-sonnet-4-5","stop_reason":"end_turn","usage":{"input_tokens":5,"cache_read_input_tokens":100,"cache_creation_input_tokens":20,"output_tokens":7}},"timestamp":")" +
         isoOf(t + 22'000) + R"("})");
    ASSERT_EQ(sink.emitted.size(), 2u);
    EXPECT_EQ(sink.emitted[1].first.tokens, (TokenCounts{125, 100, 20, 7, 0, 0}));

    // The first usage of a key a line without usage claimed is its completion.
    line(R"({"type":"assistant","message":{"id":"m1","model":"auto","stop_reason":"tool_use","usage":{"input_tokens":900,"output_tokens":30}},"timestamp":")" +
         isoOf(t + 23'000) + R"("})");
    ASSERT_EQ(sink.emitted.size(), 3u);
    EXPECT_EQ(sink.emitted[2].first.key, "qodercli:m1");
    EXPECT_TRUE(sink.emitted[2].second);
    EXPECT_EQ(sink.emitted[2].first.startMs, std::nullopt);

    // Another file's copy of a claimed key is copied history, with or without usage.
    ClaudeParser copy("/q/projects/-w-atlas/s2.jsonl", deduper, ClaudeParser::Variant::QoderCli);
    Collector copySink;
    copy.feed(R"({"type":"assistant","message":{"id":"m1","model":"auto","stop_reason":"end_turn","usage":{"input_tokens":900,"output_tokens":30}},"timestamp":")" +
                  isoOf(t + 30'000) + R"("})",
              copySink);
    copy.feed(R"({"type":"assistant","message":{"id":"m2","model":"auto","stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}},"timestamp":")" +
                  isoOf(t + 31'000) + R"("})",
              copySink);
    EXPECT_TRUE(copySink.emitted.empty());
    EXPECT_EQ(copy.phase(), Phase::Idle) << "a copied line changes nothing";
    EXPECT_EQ(copy.model(), std::nullopt);
}

TEST(QoderCli, UltracodeMarkersAndSidechainsAsClaudeWithoutModes) {
    Deduper deduper;
    ClaudeParser parser("/q/projects/-w-atlas/s1/subagents/agent-1.jsonl", deduper, ClaudeParser::Variant::QoderCli);
    Collector sink;
    EXPECT_TRUE(parser.isSubagent());
    const auto t = kDayStart;
    parser.feed(R"({"type":"attachment","attachment":{"type":"ultra_effort_enter"},"timestamp":")" + isoOf(t) + R"("})", sink);
    parser.feed(R"({"type":"assistant","effort":"high","message":{"id":"m1","model":"auto","stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":2}},"timestamp":")" +
                    isoOf(t + 1'000) + R"("})",
                sink);
    ASSERT_EQ(sink.emitted.size(), 1u);
    EXPECT_TRUE(sink.emitted[0].first.modes.empty()) << "no effort or speed facts";
    EXPECT_TRUE(parser.modes().empty());
}

// Through the monitor: both editions' roots, today's main session files and the sub-agent under its session.
TEST(QoderCli, TheMonitorCountsMainSessionsOnly) {
    TempDir temp;
    const auto line = [&](const std::string& type, const std::string& id, std::int64_t ms, bool usage) {
        std::string text = R"({"type":")" + type + R"(","cwd":"/w/atlas","timestamp":")" + isoOf(ms) + R"(","message":{"role":")" + type + "\"";
        if (type == "assistant") {
            text += R"(,"id":")" + id + R"(","model":"auto","stop_reason":"end_turn")";
            if (usage) text += R"(,"usage":{"input_tokens":2000,"cache_read_input_tokens":1500,"output_tokens":80})";
        } else {
            text += R"(,"content":"go")";
        }
        return text + "}}\n";
    };
    temp.write(".qoder/projects/-w-atlas/s1.jsonl", line("user", "", kNowMs - 60'000, false) + line("assistant", "m1", kNowMs - 55'000, true));
    temp.write(".qoder/projects/-w-atlas/s1/subagents/agent-a.jsonl",
               line("user", "", kNowMs - 50'000, false) + line("assistant", "m2", kNowMs - 45'000, false));
    temp.write("cn/projects/-w-beta/s3.jsonl", line("user", "", kNowMs - 40'000, false) + line("assistant", "m3", kNowMs - 35'000, true));
    MonitorConfig config;
    config.locations = SourceLocations::resolve(LocationLists(), environmentOf({{"QODERCN_CONFIG_DIR", temp.file("cn")}}), temp.path.string());
    config.enabled = {SourceKind::QoderCli};
    Monitor monitor(config);
    monitor.start(kNowMs, kTodayStartMs);
    auto snap = monitor.snapshot(kNowMs, kTodayStartMs);
    EXPECT_EQ(snap.todayRequests, 2) << "the sub-agent's call kept no usage";
    EXPECT_EQ(snap.todayInput, 4000);
    const auto& row = rowOf(snap, SourceKind::QoderCli);
    EXPECT_EQ(row.sessions, 2) << "main session files only";
    EXPECT_TRUE(row.available);
    EXPECT_EQ(row.path, temp.file(".qoder/projects"));
    const auto sub = temp.file(".qoder/projects/-w-atlas/s1/subagents/agent-a.jsonl");
    EXPECT_EQ(monitor.parentOf(sub), temp.file(".qoder/projects/-w-atlas/s1.jsonl"));
    EXPECT_TRUE(monitor.agents().at(sub).isSubagent);
    EXPECT_EQ(monitor.agents().at(sub).model, "auto");

    std::ofstream(temp.file("cn/projects/-w-beta/s3.jsonl"), std::ios::app) << line("assistant", "m4", kNowMs, true);
    EXPECT_TRUE(monitor.poll(kNowMs + 1000, kTodayStartMs));
    EXPECT_EQ(monitor.takeCompletions().size(), 1u);
}
