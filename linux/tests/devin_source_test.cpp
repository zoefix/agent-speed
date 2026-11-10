#include <gtest/gtest.h>

#include <algorithm>
#include <chrono>
#include <set>

#include "agentspeed/devin_source.hpp"
#include "agentspeed/history_analyzer.hpp"
#include "agentspeed/history_store.hpp"
#include "agentspeed/location_entries.hpp"
#include "agentspeed/monitor.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/tz.hpp"
#include "devin_test_support.hpp"

// Devin CLI's rules one by one (METRICS 4.17): what a row says, live reading, several databases and the location list.
using namespace agentspeed;
using devin_test::CliDatabase;
using devin_test::TempDir;
namespace fs = std::filesystem;

namespace {

const std::int64_t kNowSeconds =
    std::chrono::duration_cast<std::chrono::seconds>(std::chrono::system_clock::now().time_since_epoch()).count();
// A start of day two hours ago: every row these tests write now is today's.
const std::int64_t kTodayStartMs = (kNowSeconds - 2 * 3600) * 1000;

Environment environmentOf(std::map<std::string, std::string> values) {
    return [values](const std::string& name) -> std::optional<std::string> {
        const auto it = values.find(name);
        return it == values.end() ? std::nullopt : std::optional<std::string>(it->second);
    };
}

devin::Message parse(const std::string& text) {
    auto message = devin::Message::parse(text);
    EXPECT_TRUE(message.has_value()) << text;
    return message.value_or(devin::Message{});
}

std::vector<std::string> keysOf(const devin::Update& update) {
    std::vector<std::string> out;
    for (const auto& [response, completion] : update.responses) out.push_back(response.key);
    return out;
}

SourceLocations devinLocations(const LocationList& list) {
    LocationLists lists;
    lists.set(RootFamily::Devin, list);
    return SourceLocations::resolve(lists, environmentOf({}), "/nonexistent-home");
}

MonitorConfig devinConfig(const LocationList& list) {
    MonitorConfig config;
    config.locations = devinLocations(list);
    config.enabled = {SourceKind::Devin};
    return config;
}

const SourceRow& rowOf(const Snapshot& snap, SourceKind kind) {
    return *std::find_if(snap.sources.begin(), snap.sources.end(), [&](const SourceRow& r) { return r.source == kind; });
}

}  // namespace

// MARK: - Rows

TEST(Devin, ARowGivesItsCallFromTheMetricsAlone) {
    const auto message = parse(
        R"({"role":"assistant","content":"x","tool_calls":[{}],"metadata":{"request_id":" r1 ","generation_model":"glm-5-2","finish_reason":"stop",)"
        R"("metrics":{"input_tokens":67,"output_tokens":103,"cache_read_tokens":32256,"cache_creation_tokens":-4,"total_time_ms":13666,"ttft_ms":13394}}})");
    EXPECT_EQ(message.role, devin::Role::Assistant);
    EXPECT_EQ(message.phase(), Phase::Tools);
    EXPECT_EQ(message.requestId, "r1");
    const auto record = devin::response(message, "s", 7, 20'000, std::nullopt, "db#s");
    ASSERT_TRUE(record);
    EXPECT_EQ(record->key, "devin:s:request:r1");
    EXPECT_EQ(record->tokens, (TokenCounts{32'323, 32'256, 0, 103, 0, 0}));
    EXPECT_EQ(record->startMs, 20'000 - 13'666);
    EXPECT_EQ(record->firstMs, 20'000 - 13'666 + 13'394);
    EXPECT_EQ(record->model, "glm-5-2");
    EXPECT_EQ(record->provider, std::nullopt);
    EXPECT_TRUE(record->final);
    EXPECT_EQ(record->source, SourceKind::Devin);

    // No request id: the row is the call; a session model of `adaptive` is no model; half a millisecond is no duration.
    const auto plain = parse(R"({"role":"assistant","metadata":{"metrics":{"output_tokens":1,"total_time_ms":0.5}}})");
    const auto row = devin::response(plain, "s", 9, 1'000, devin::sessionModel(" Adaptive "), "db#s");
    ASSERT_TRUE(row);
    EXPECT_EQ(row->key, "devin:s:row:9");
    EXPECT_EQ(row->model, std::nullopt);
    EXPECT_EQ(row->startMs, std::nullopt);
    EXPECT_EQ(row->firstMs, std::nullopt);
    EXPECT_EQ(devin::sessionModel(" kimi-k2-7 "), "kimi-k2-7");
    EXPECT_EQ(devin::sessionModel("   "), std::nullopt);

    EXPECT_EQ(parse(R"({"role":"tool","content":"…"})").phase(), Phase::Generating);
    EXPECT_EQ(parse(R"({"role":"user"})").phase(), Phase::Generating);
    EXPECT_EQ(parse(R"({"role":"assistant","metadata":{"finish_reason":"tool_calls"}})").phase(), Phase::Tools);
    EXPECT_EQ(parse(R"({"role":"assistant","tool_calls":[],"metadata":{"finish_reason":"stop"}})").phase(), Phase::Idle);
    EXPECT_EQ(parse(R"({"role":"system"})").phase(), std::nullopt);

    // Strict JSON objects only: NaN, trailing text, arrays and truncated text are malformed rows.
    EXPECT_FALSE(devin::Message::parse(R"({"role":"assistant","metadata":{"metrics":{"input_tokens":NaN}}})"));
    EXPECT_FALSE(devin::Message::parse(R"({"role":"user"} x)"));
    EXPECT_FALSE(devin::Message::parse("[1]"));
    EXPECT_FALSE(devin::Message::parse(R"({"role":"user" )"));

    // A later metadata member replaces the earlier one; a non-finite total time gives no start.
    const auto replaced = parse(
        R"({"role":"assistant","metadata":{"request_id":"a","metrics":{"output_tokens":5}},"metadata":{"metrics":{"output_tokens":2,"total_time_ms":1e999}}})");
    const auto last = devin::response(replaced, "s", 3, 5'000, "m", "db#s");
    ASSERT_TRUE(last);
    EXPECT_EQ(last->key, "devin:s:row:3");
    EXPECT_EQ(last->tokens.output, 2);
    EXPECT_EQ(last->startMs, std::nullopt);
    EXPECT_EQ(last->model, "m");

    // Zero tokens: no call (the row still sets the phase).
    const auto empty = parse(R"({"role":"assistant","metadata":{"metrics":{"input_tokens":0}}})");
    EXPECT_FALSE(devin::response(empty, "s", 1, 0, std::nullopt, "a"));
    EXPECT_EQ(empty.phase(), Phase::Idle);
}

// MARK: - Live

TEST(Devin, TheStartupWindowThenNewRowsAsLiveCompletions) {
    TempDir temp;
    CliDatabase database(temp.file("cli/sessions.db"));
    database.session("old-pond");
    database.session("aurora");
    const auto start = kTodayStartMs / 1000;
    database.call("old-pond", "old", start - 3 * 3600);
    database.row("aurora", R"({"role":"user","content":"go"})", start + 60);
    database.call("aurora", "r1", start + 65, 100, 40, true);
    Deduper deduper;
    devin::Source source({devin::Root::location(temp.file("cli"))}, deduper);

    const auto seeded = source.seed(kTodayStartMs);
    ASSERT_EQ(seeded.size(), 1u);
    EXPECT_TRUE(seeded[0].seeded);
    EXPECT_EQ(keysOf(seeded[0]), std::vector<std::string>{"devin:aurora:request:r1"});
    const auto id = database.path() + "#aurora";
    ASSERT_TRUE(seeded[0].agents.contains(id));
    EXPECT_EQ(seeded[0].agents.at(id).phase, Phase::Tools);
    EXPECT_EQ(seeded[0].agents.at(id).project, "aurora");
    EXPECT_FALSE(seeded[0].agents.contains(database.path() + "#old-pond")) << "only rows of the startup window";
    EXPECT_EQ(source.activeSessionCount(kTodayStartMs), 1);

    // Devin stores the call twice, then the tool result starts the next request.
    database.call("aurora", "r1", start + 65, 100, 40, true);
    database.row("aurora", R"({"role":"tool","tool_call_id":"c1","content":"…"})", start + 70);
    const auto tool = source.refresh();
    ASSERT_EQ(tool.size(), 1u);
    EXPECT_TRUE(tool[0].responses.empty()) << "a stored copy is no new request";
    EXPECT_EQ(tool[0].agents.at(id).phase, Phase::Generating);
    EXPECT_EQ(tool[0].agents.at(id).inFlightStartMs, (start + 70) * 1000);

    database.call("aurora", "r2", start + 80, 10, 5);
    const auto next = source.refresh();
    ASSERT_EQ(next.size(), 1u);
    EXPECT_FALSE(next[0].seeded);
    ASSERT_EQ(next[0].responses.size(), 1u);
    EXPECT_EQ(next[0].responses[0].first.key, "devin:aurora:request:r2");
    EXPECT_TRUE(next[0].responses[0].second) << "a new call is a live completion";
    EXPECT_EQ(next[0].agents.at(id).phase, Phase::Idle);
    EXPECT_TRUE(source.refresh().empty());
}

// The startup read goes down from the highest row id; a row without a time neither ends it nor is left out.
TEST(Devin, ARowWithoutATimeIsReadAtStartupAndCountsAsMalformed) {
    TempDir temp;
    CliDatabase database(temp.file("cli/sessions.db"));
    database.session("old-pond");
    database.session("aurora");
    const auto start = kTodayStartMs / 1000;
    database.call("old-pond", "old", start - 3 * 3600);
    database.call("aurora", "r1", start + 60);
    database.untimedRow("aurora", R"({"role":"user","content":"go"})");
    database.call("aurora", "r2", start + 90);
    Deduper deduper;
    devin::Source source({devin::Root::location(temp.file("cli"))}, deduper);
    const auto seeded = source.seed(kTodayStartMs);
    ASSERT_EQ(seeded.size(), 1u);
    EXPECT_EQ(keysOf(seeded[0]), (std::vector<std::string>{"devin:aurora:request:r1", "devin:aurora:request:r2"}));
    const auto& agent = seeded[0].agents.at(database.path() + "#aurora");
    EXPECT_EQ(agent.malformedRows, 1);
    EXPECT_EQ(agent.lastLineMs, (start + 90) * 1000);
    EXPECT_EQ(source.malformedRows(), 1);
    EXPECT_FALSE(seeded[0].agents.contains(database.path() + "#old-pond")) << "the first older row ends the read";
}

TEST(Devin, AReplacedDatabaseIsReadAgainLikeAtStartup) {
    TempDir temp;
    const auto path = temp.file("cli/sessions.db");
    {
        CliDatabase database(path);
        database.session("aurora");
        database.call("aurora", "r1", kNowSeconds - 60);
    }
    Deduper deduper;
    devin::Source source({devin::Root::location(temp.file("cli"))}, deduper);
    source.seed(kTodayStartMs);
    const auto other = temp.file("other/sessions.db");
    {
        CliDatabase database(other);
        database.session("aurora");
        database.call("aurora", "r1", kNowSeconds - 60);
        database.call("aurora", "r2", kNowSeconds - 30);
        database.row("aurora", R"({"role":"user"})", kNowSeconds - 20);
    }
    fs::rename(other, path);
    const auto updates = source.refresh();
    ASSERT_EQ(updates.size(), 1u);
    EXPECT_TRUE(updates[0].seeded) << "a replaced database is read like at startup";
    ASSERT_EQ(updates[0].responses.size(), 2u);
    EXPECT_EQ(updates[0].responses[0].first.key, "devin:aurora:request:r1");
    EXPECT_FALSE(updates[0].responses[0].second) << "its keys update their responses";
    EXPECT_EQ(updates[0].responses[1].first.key, "devin:aurora:request:r2");
    EXPECT_TRUE(updates[0].responses[1].second);
    EXPECT_EQ(updates[0].agents.at(path + "#aurora").phase, Phase::Generating);
}

TEST(Devin, ADatabaseIsNeverALinkAndASessionWithoutAFolderIsNamedByItsId) {
    TempDir temp;
    CliDatabase database(temp.file("real/sessions.db"));
    database.session("night-owl", "swe-1-6", "");
    database.call("night-owl", "r1", kNowSeconds - 20);
    fs::create_directories(temp.file("linked"));
    fs::create_symlink(database.path(), temp.file("linked/sessions.db"));
    EXPECT_EQ(devin::Root::location(temp.file("linked")).databaseNow(), std::nullopt);
    EXPECT_EQ(devin::Root::database(temp.file("linked/sessions.db")).databaseNow(), std::nullopt);
    fs::create_directory_symlink(temp.file("real"), temp.file("folder-link"));
    EXPECT_EQ(devin::Root::location(temp.file("folder-link")).databaseNow(), temp.file("folder-link/sessions.db"))
        << "the folders above it may be links";

    Deduper deduper;
    devin::Source source({devin::Root::location(temp.file("real"))}, deduper);
    const auto updates = source.seed(kTodayStartMs);
    ASSERT_EQ(updates.size(), 1u);
    const auto& state = updates[0].agents.at(database.path() + "#night-owl");
    EXPECT_EQ(state.project, "night-owl");
    EXPECT_EQ(state.cwdPath, std::nullopt);
    ASSERT_EQ(updates[0].responses.size(), 1u);
    EXPECT_EQ(updates[0].responses[0].first.agentId, database.path() + "#night-owl");
}

TEST(Devin, ACopiedSessionInALaterDatabaseIsSkipped) {
    TempDir temp;
    CliDatabase first(temp.file("a/sessions.db")), second(temp.file("b/sessions.db"));
    for (auto* database : {&first, &second}) {
        database->session("aurora");
        database->call("aurora", "r1", kNowSeconds - 20);
    }
    // The later copy also has a call the earlier database lacks: the whole session is skipped, not only its known keys.
    second.call("aurora", "r3", kNowSeconds - 15);
    second.session("comet");
    second.call("comet", "r9", kNowSeconds - 10);
    Deduper deduper;
    devin::Source source({devin::Root::location(temp.file("a")), devin::Root::location(temp.file("b"))}, deduper);
    std::vector<std::string> agents;
    for (const auto& update : source.seed(kTodayStartMs)) {
        for (const auto& [response, completion] : update.responses) agents.push_back(response.agentId);
    }
    std::sort(agents.begin(), agents.end());
    EXPECT_EQ(agents, (std::vector<std::string>{first.path() + "#aurora", second.path() + "#comet"}));
    EXPECT_EQ(source.activeSessionCount(kTodayStartMs), 2);
    for (const auto& agent : source.readers()[1]->agents()) EXPECT_NE(agent.sessionId, "aurora");

    fs::create_directories(temp.file("history"));
    auto store = history::Store::open(temp.file("history/history.sqlite"));
    history::Analyzer analyzer(*store, std::make_shared<pricing::PriceBook>());
    const std::set<SourceKind> enabled{SourceKind::Devin};
    const auto report = analyzer.run(devinLocations({DataLocation::at(temp.file("a")), DataLocation::at(temp.file("b"))}), enabled,
                                     TimeZone::utc(), "UTC");
    EXPECT_EQ(report.sessionsAnalyzed, 2);
    std::int64_t requests = 0;
    for (const auto& day : analyzer.days(&enabled)) requests += day.totals.requests;
    EXPECT_EQ(requests, 2);
}

// Through the monitor and the engine: today's calls, each stored copy once; a Devin Desktop folder in the list adds
// nothing.
TEST(Devin, TodayCountsEachCallOnce) {
    TempDir temp;
    CliDatabase database(temp.file("cli/sessions.db"));
    database.session("aurora");
    for (int copy = 0; copy < 2; ++copy) database.call("aurora", "r1", kNowSeconds - 30, 100, 40);
    temp.write("Devin/User/acp-events/d.ndjson",
               R"({"notification":{"sessionUpdate":"usage_update","used":9,"size":9,"_meta":{"cognition.ai/inputTokens":300,"cognition.ai/outputTokens":9}}})"
               "\n");
    Monitor monitor(devinConfig({DataLocation::at(temp.file("cli")), DataLocation::at(temp.file("Devin"))}));
    const auto now = kNowSeconds * 1000;
    monitor.start(now, kTodayStartMs);
    auto snap = monitor.snapshot(now, kTodayStartMs);
    EXPECT_EQ(snap.todayRequests, 1);
    EXPECT_EQ(snap.todayOutput, 40);
    EXPECT_EQ(snap.todayInput, 1'000);
    const auto& row = rowOf(snap, SourceKind::Devin);
    EXPECT_EQ(row.sessions, 1);
    EXPECT_TRUE(row.available);
    EXPECT_EQ(row.path, temp.file("cli"));
    EXPECT_TRUE(monitor.takeCompletions().empty()) << "the startup read raises no event";

    database.call("aurora", "r2", kNowSeconds, 10, 5);
    EXPECT_TRUE(monitor.poll(now + 1000, kTodayStartMs));
    snap = monitor.snapshot(now + 1000, kTodayStartMs);
    EXPECT_EQ(snap.todayRequests, 2);
    EXPECT_EQ(snap.todayOutput, 45);
    EXPECT_EQ(monitor.takeCompletions().size(), 1u);
    const auto agent = std::find_if(snap.agents.begin(), snap.agents.end(), [](const AgentRow& a) { return a.source == SourceKind::Devin; });
    ASSERT_NE(agent, snap.agents.end());
    EXPECT_EQ(agent->project, "aurora");
    EXPECT_EQ(agent->model, "swe-1-6");
    EXPECT_FALSE(agent->subagent);
    EXPECT_EQ(monitor.cwdOf(agent->id), "/w/aurora");
}

TEST(Devin, ADatabaseWithoutTheTablesIsUnsupported) {
    TempDir temp;
    fs::create_directories(temp.file("cli"));
    {
        const auto writer = sqlite::Connection::open(temp.file("cli/sessions.db"), sqlite::OpenMode::ReadWrite);
        writer.execute("CREATE TABLE sessions (id TEXT PRIMARY KEY, working_directory TEXT, model TEXT)");
    }
    Monitor monitor(devinConfig({DataLocation::at(temp.file("cli"))}));
    monitor.start(kNowSeconds * 1000, kTodayStartMs);
    const auto warnings = monitor.warnings();
    EXPECT_NE(std::find(warnings.unsupported.begin(), warnings.unsupported.end(), SourceKind::Devin), warnings.unsupported.end());
    EXPECT_EQ(warnings.unreadableFiles, 0);
    EXPECT_TRUE(rowOf(monitor.snapshot(kNowSeconds * 1000, kTodayStartMs), SourceKind::Devin).available);
}

// MARK: - Location list

TEST(Devin, SameLocationsAndTheAutomaticEntry) {
    TempDir temp;
    const auto database = temp.write("share/devin/cli/sessions.db", "");
    const auto key = [&](const std::string& relative) { return locationKey(temp.file(relative), RootFamily::Devin); };
    EXPECT_EQ(key("share/devin"), key("share/devin/cli"));
    EXPECT_NE(key("share"), key("share/devin"));
    EXPECT_EQ(key("empty"), temp.file("empty/sessions.db"));

    const AutomaticKeys automatic{{devin::Root::database(database).locationKey()}};
    LocationLists lists;
    EXPECT_EQ(lists.add(temp.file("share/devin"), RootFamily::Devin, automatic), AddOutcome::AlreadyAutomatic);
    EXPECT_EQ(lists.add(temp.file("work"), RootFamily::Devin, automatic), AddOutcome::Added);
    lists.remove(RootFamily::Devin, DataLocation::automaticEntry());
    EXPECT_EQ(lists.add(temp.file("share/devin/cli"), RootFamily::Devin, automatic), AddOutcome::RestoredAutomatic)
        << "Devin is not one of the sources whose automatic set is wider than its default";
    EXPECT_TRUE(lists[RootFamily::Devin].front().automatic);
    EXPECT_FALSE(automaticIsWiderThanDefault(RootFamily::Devin));
    EXPECT_FALSE(acceptsDatabaseFile(RootFamily::Devin));
    EXPECT_EQ(listName(RootFamily::Devin), "devin");
    const auto& order = locationListOrder();
    EXPECT_EQ(std::find(order.begin(), order.end(), RootFamily::Devin) - order.begin(), 16) << "after Factory Droid";
    EXPECT_EQ(familyOf(SourceKind::Devin), RootFamily::Devin);

    EXPECT_EQ(devin::databases({devin::Root::database(database), devin::Root::location(temp.file("share/devin"))}),
              std::vector<std::string>{database});
    temp.write("Support/Devin/User/acp-events/d.ndjson", "{}\n");
    EXPECT_EQ(devin::Root::location(temp.file("Support/Devin/User/acp-events")).databaseNow(), std::nullopt);
    EXPECT_EQ(devin::Root::location(temp.file("Support/Devin")).databaseNow(), std::nullopt) << "Devin Desktop is not read";

    LocationLists three;
    three.set(RootFamily::Devin, {DataLocation::automaticEntry(), DataLocation::at(temp.file("share/devin")), DataLocation::at(temp.file("work"))});
    const auto l = SourceLocations::resolve(three, environmentOf({{"XDG_DATA_HOME", "/x"}}), "/h");
    ASSERT_EQ(l.devinRoots.size(), 3u);
    EXPECT_TRUE(l.devinRoots[0].isDatabase);
    EXPECT_FALSE(l.devinRoots[1].isDatabase);
    EXPECT_FALSE(l.devinRoots[2].isDatabase);
    EXPECT_EQ(l.devinRoots[0].path, "/x/devin/cli/sessions.db");
    EXPECT_FALSE(l.readsSame(devinLocations({DataLocation::at(temp.file("work"))})));
}

TEST(Devin, TheAutomaticRootsFollowTheEnvironment) {
    EXPECT_EQ(devin::automaticDatabase(environmentOf({}), "/home/me"), "/home/me/.local/share/devin/cli/sessions.db");
    EXPECT_EQ(devin::automaticDatabase(environmentOf({{"XDG_DATA_HOME", "/x"}}), "/home/me"), "/x/devin/cli/sessions.db");
    EXPECT_EQ(devin::automaticDatabase(environmentOf({{"XDG_DATA_HOME", "/x"}, {"CHISEL_SESSION_DB", "/y/s.db"}}), "/h"), "/y/s.db");
    EXPECT_EQ(devin::automaticDatabase(environmentOf({{"CHISEL_SESSION_DB", ""}, {"XDG_DATA_HOME", ""}}), "/h"),
              "/h/.local/share/devin/cli/sessions.db");
    const auto environment = environmentOf({{"XDG_DATA_HOME", "/nonexistent/data"}});
    EXPECT_EQ(SourceLocations::defaultRoot(SourceKind::Devin, environment, "/h"), "/nonexistent/data/devin/cli")
        << "Settings names the CLI folder";
    EXPECT_EQ(automaticKeys(RootFamily::Devin, LocationLists(), environment, "/h").keys,
              std::vector<std::string>{"/nonexistent/data/devin/cli/sessions.db"});
    EXPECT_EQ(automaticShown(RootFamily::Devin, environment, "/h"), std::vector<std::string>{"/nonexistent/data/devin/cli"});
}
