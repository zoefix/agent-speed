#include <gtest/gtest.h>

#include <algorithm>
#include <set>

#include "agentspeed/history_store.hpp"
#include "agentspeed/remote_history.hpp"
#include "remote_history_support.hpp"

using namespace remote_history_lines;
namespace fs = std::filesystem;

namespace {

std::set<std::string> tables(const history::Store& store) {
    std::set<std::string> names;
    auto rows = store.connection().prepare("SELECT name FROM sqlite_master WHERE type = 'table'");
    while (rows.step()) names.insert(*rows.getString(0));
    return names;
}

bool hasRemoteTables(const history::Store& store) {
    const auto names = tables(store);
    return names.contains("remote_hosts") && names.contains("remote_days") && names.contains("remote_perf");
}

std::vector<std::string> keysOf(const std::map<std::string, std::string>& digests) {
    std::vector<std::string> out;
    for (const auto& [key, digest] : digests) out.push_back(key);
    return out;
}

HistoryLedger loaded(history::Store& store) {
    HistoryLedger ledger({}, HistorySyncState::kFlushBytes, [] { return std::string("Asia/Taipei"); });
    ledger.load(store);
    return ledger;
}

}  // namespace

// REMOTE 8.3: the side tables are created by the app on an existing store, without a schema change; the relay's store
// (`Store::open` alone) never has them.
TEST(RemoteHistoryStore, SideTablesAreCreatedOnAnExistingStoreWithoutChangingItsSchema) {
    TempStore temp;
    temp.store->connection().execute("DROP TABLE remote_hosts; DROP TABLE remote_days; DROP TABLE remote_perf;");
    temp.store.reset();
    auto opened = history::Store::open(temp.path());
    EXPECT_FALSE(hasRemoteTables(*opened));
    EXPECT_EQ(opened->meta("schema"), std::to_string(history::Store::kSchema));
    opened->ensureRemoteTables();
    opened->ensureRemoteTables();
    EXPECT_TRUE(hasRemoteTables(*opened));
    EXPECT_EQ(opened->meta("schema"), std::to_string(history::Store::kSchema));
}

TEST(RemoteHistoryStore, ABatchCommitsAtHistoryDone) {
    TempStore temp;
    auto ledger = loaded(*temp);
    EXPECT_EQ(receive(ledger, {status(StatusCode::Connected), event(day("2026-11-02")), event(perfLine("2026-11-02")), event(day("2026-11-03"))},
                      *temp),
              HistoryEffect::none());
    EXPECT_EQ(temp.count("remote_days"), 0);
    EXPECT_TRUE(ledger.rows().empty());

    EXPECT_EQ(ledger.receive(event(done()), *temp), HistoryEffect::changed());
    EXPECT_EQ(temp.count("remote_days"), 2);
    EXPECT_EQ(temp.count("remote_perf"), 1);
    const auto sources = codex();
    const auto days = ledger.rows().usage(&sources);
    ASSERT_EQ(days.size(), 2u);
    for (const auto& d : days) {
        EXPECT_EQ(d.host, kHost);
        EXPECT_EQ(d.totals.output, 340);
        EXPECT_FALSE(d.priceVersion.has_value());
    }
    const auto records = ledger.rows().perfRecords(&sources);
    ASSERT_EQ(records.size(), 1u);
    EXPECT_EQ(records[0].day, "2026-11-02");
    EXPECT_EQ(records[0].model, "gpt-5.5");
    EXPECT_EQ(records[0].record.n, 4);

    // A reopened ledger reads the same rows back.
    auto reloaded = loaded(*temp);
    const auto again = reloaded.rows().usage(&sources);
    ASSERT_EQ(again.size(), days.size());
    for (std::size_t k = 0; k < days.size(); ++k) {
        EXPECT_EQ(again[k].key, days[k].key);
        EXPECT_EQ(history::encode(again[k].totals), history::encode(days[k].totals));
    }
    EXPECT_EQ(reloaded.rows().digests(kHost), ledger.rows().digests(kHost));
    EXPECT_EQ(reloaded.rows().perfRecords(&sources).size(), 1u);
}

TEST(RemoteHistoryStore, AConnectionChangeRollsTheBatchBack) {
    TempStore temp;
    auto ledger = loaded(*temp);
    receive(ledger, {event(day("2026-11-01"), kHost, 1), event(day("2026-11-02"), kHost, 2), event(done(), kHost, 2)}, *temp);
    EXPECT_EQ(keysOf(ledger.rows().digests(kHost)), std::vector<std::string>{"2026-11-02/codex"});

    // A done of the old connection after the new one started commits nothing.
    receive(ledger, {event(day("2026-11-03"), kHost, 3), event(done(), kHost, 2)}, *temp);
    EXPECT_EQ(keysOf(ledger.rows().digests(kHost)), std::vector<std::string>{"2026-11-02/codex"});
}

TEST(RemoteHistoryStore, AStatusLeavingConnectedRollsTheBatchBack) {
    TempStore temp;
    auto ledger = loaded(*temp);
    receive(ledger, {status(StatusCode::Connected), event(day("2026-11-01")), status(StatusCode::Unreachable), event(done())}, *temp);
    EXPECT_TRUE(ledger.rows().empty());
    EXPECT_EQ(temp.count("remote_days"), 0);

    // A repeated `connected` (a new clock offset) keeps it; a failed analysis drops it.
    receive(ledger, {status(StatusCode::Connected), event(day("2026-11-01")), status(StatusCode::Connected), event(done())}, *temp);
    EXPECT_EQ(ledger.rows().digests(kHost).size(), 1u);
    receive(ledger, {event(day("2026-11-02")), event(historyFailed()), event(done())}, *temp);
    EXPECT_EQ(ledger.rows().digests(kHost).size(), 1u);
}

TEST(RemoteHistoryStore, ADayReplacesItsRowAndItsRecords) {
    TempStore temp;
    auto ledger = loaded(*temp);
    receive(ledger,
            {event(day("2026-11-02")), event(perfLine("2026-11-02", SourceKind::Codex, "a")), event(perfLine("2026-11-02", SourceKind::Codex, "b")),
             event(day("2026-11-03")), event(perfLine("2026-11-03", SourceKind::Codex, "a")), event(done())},
            *temp);
    EXPECT_EQ(temp.count("remote_perf"), 3);

    receive(ledger,
            {event(day("2026-11-02", SourceKind::Codex, aggregate(3, 999), "0123456789abcdef")),
             event(perfLine("2026-11-02", SourceKind::Codex, "b", record(9))), event(done())},
            *temp);
    const auto sources = codex();
    std::vector<std::string> records;
    for (const auto& r : ledger.rows().perfRecords(&sources)) records.push_back(r.day + " " + r.model + " " + std::to_string(r.record.n));
    EXPECT_EQ(records, (std::vector<std::string>{"2026-11-02 b 9", "2026-11-03 a 4"}));
    EXPECT_EQ(temp.count("remote_perf"), 2);
    EXPECT_EQ(ledger.rows().digests(kHost),
              (std::map<std::string, std::string>{{"2026-11-02/codex", "0123456789abcdef"}, {"2026-11-03/codex", "aaaaaaaaaaaaaaa3"}}));
    const auto days = ledger.rows().usage(&sources);
    const auto second = std::find_if(days.begin(), days.end(), [](const auto& d) { return d.key.day == "2026-11-02"; });
    ASSERT_NE(second, days.end());
    EXPECT_EQ(second->totals.output, 999);
}

TEST(RemoteHistoryStore, RecordsOnlyFollowTheirOwnDay) {
    TempStore temp;
    auto ledger = loaded(*temp);
    receive(ledger,
            {event(perfLine("2026-11-01")), event(day("2026-11-02")), event(perfLine("2026-11-01")), event(perfLine("2026-11-02", SourceKind::ClaudeCode)),
             event(done())},
            *temp);
    EXPECT_TRUE(ledger.rows().perfRecords().empty());
}

TEST(RemoteHistoryStore, APerformanceOnlyDayTravels) {
    TempStore temp;
    auto ledger = loaded(*temp);
    receive(ledger, {event(day("2026-11-03", SourceKind::Codex, kEmptyAggregate)), event(perfLine("2026-11-03")), event(done())}, *temp);
    EXPECT_EQ(ledger.rows().perfRecords().size(), 1u);
    const auto days = ledger.rows().usage();
    ASSERT_EQ(days.size(), 1u);
    EXPECT_EQ(days[0].totals.requests, 0);
    EXPECT_EQ(days[0].totals.output, 0);
}

TEST(RemoteHistoryStore, HistoryDueSendsTheStoredDigests) {
    TempStore temp;
    auto ledger = loaded(*temp);
    EXPECT_EQ(ledger.receive(due(), *temp), request(kHost, {}));
    receive(ledger,
            {event(day("2026-11-02")), event(day("2026-11-03", SourceKind::ClaudeCode)), event(day("2026-11-03"), kOtherHost), event(done()),
             event(done(), kOtherHost)},
            *temp);
    std::map<std::string, std::string> expected;
    for (const auto& row : temp.store->remoteDayRows()) {
        if (row.host == kHost) expected[row.day + "/" + row.source] = row.digest;
    }
    EXPECT_EQ(expected.size(), 2u);
    EXPECT_EQ(ledger.receive(due(), *temp), request(kHost, expected));
    EXPECT_EQ(ledger.receive(due(kOtherHost), *temp), request(kOtherHost, {{"2026-11-03/codex", "aaaaaaaaaaaaaaa3"}}));
}

TEST(RemoteHistoryStore, ADayTheRelayDidNotSendIsNeverDeleted) {
    TempStore temp;
    auto ledger = loaded(*temp);
    receive(ledger, {event(day("2026-11-01")), event(day("2026-11-02")), event(done())}, *temp);
    // The relay's cache was deleted: it now only knows the second day, which changed.
    receive(ledger, {event(day("2026-11-02", SourceKind::Codex, aggregate(3, 5), "bbbbbbbbbbbbbbbb")), event(done())}, *temp);
    // An answer without days changes nothing either.
    EXPECT_EQ(ledger.receive(event(done()), *temp), HistoryEffect::none());
    EXPECT_EQ(ledger.rows().digests(kHost),
              (std::map<std::string, std::string>{{"2026-11-01/codex", "aaaaaaaaaaaaaaa1"}, {"2026-11-02/codex", "bbbbbbbbbbbbbbbb"}}));
    EXPECT_EQ(temp.count("remote_days"), 2);
}

TEST(RemoteHistoryStore, DeletingAHostRemovesOnlyItsRows) {
    TempStore temp;
    auto ledger = loaded(*temp);
    ledger.setHosts({{kHost, "devbox"}, {kOtherHost, "gpu"}}, &*temp);
    receive(ledger,
            {event(day("2026-11-02")), event(perfLine("2026-11-02")), event(day("2026-11-02"), kOtherHost), event(done()), event(done(), kOtherHost)},
            *temp);
    EXPECT_EQ(ledger.rows().names(), (std::map<std::string, std::string>{{kHost, "devbox"}, {kOtherHost, "gpu"}}));

    EXPECT_EQ(ledger.deleteHost(kHost, *temp), HistoryEffect::changed());
    EXPECT_EQ(ledger.rows().hosts(), std::set<std::string>{kOtherHost});
    EXPECT_EQ(ledger.rows().names(), (std::map<std::string, std::string>{{kOtherHost, "gpu"}}));
    const auto rows = temp.store->remoteDayRows();
    ASSERT_EQ(rows.size(), 1u);
    EXPECT_EQ(rows[0].host, kOtherHost);
    EXPECT_EQ(temp.count("remote_perf"), 0);
    const auto names = temp.store->remoteHostNames();
    ASSERT_EQ(names.size(), 1u);
    EXPECT_EQ(names.begin()->first, kOtherHost);
    EXPECT_EQ(ledger.deleteHost(kHost, *temp), HistoryEffect::none());
}

TEST(RemoteHistoryStore, HostsAreRenamedAndRemovedOnesKeepTheirName) {
    TempStore temp;
    auto ledger = loaded(*temp);
    ledger.setHosts({{kHost, "devbox"}}, &*temp);
    receive(ledger, {event(day("2026-11-02")), event(done())}, *temp);
    EXPECT_EQ(temp.store->remoteHostNames()[kHost], std::optional<std::string>("devbox"));
    EXPECT_EQ(ledger.setHosts({{kHost, "build server"}}, &*temp), HistoryEffect::changed());
    EXPECT_EQ(temp.store->remoteHostNames()[kHost], std::optional<std::string>("build server"));
    // Removed from the settings: the rows and the last name stay.
    EXPECT_EQ(ledger.setHosts({}, &*temp), HistoryEffect::none());
    EXPECT_EQ(ledger.rows().names(), (std::map<std::string, std::string>{{kHost, "build server"}}));
    EXPECT_EQ(ledger.rows().digests(kHost).size(), 1u);
    // A reload gives the stored name until the settings name the host again.
    auto reloaded = loaded(*temp);
    EXPECT_EQ(reloaded.rows().names(), (std::map<std::string, std::string>{{kHost, "build server"}}));
}

TEST(RemoteHistoryStore, ClearHistoryEmptiesTheSideTables) {
    TempStore temp;
    auto ledger = loaded(*temp);
    receive(ledger, {event(day("2026-11-02")), event(perfLine("2026-11-02")), event(done())}, *temp);
    EXPECT_EQ(temp.count("remote_days"), 1);
    temp.store->clear();
    ledger.reset();
    EXPECT_EQ(temp.count("remote_days"), 0);
    EXPECT_EQ(temp.count("remote_perf"), 0);
    EXPECT_EQ(temp.count("remote_hosts"), 0);
    EXPECT_TRUE(ledger.rows().empty());
    // A store without the side tables (the relay's) clears as before.
    TempStore plain;
    plain.store->connection().execute("DROP TABLE remote_hosts; DROP TABLE remote_days; DROP TABLE remote_perf;");
    EXPECT_NO_THROW(plain.store->clear());
}

TEST(RemoteHistoryStore, ConnectedHostsFollowTheirStatus) {
    TempStore temp;
    auto ledger = loaded(*temp);
    receive(ledger, {status(StatusCode::Connected), status(StatusCode::Connected, kOtherHost)}, *temp);
    EXPECT_EQ(ledger.connectedHosts(), (std::vector<std::string>{kOtherHost, kHost}));
    receive(ledger, {status(StatusCode::Seeding, kOtherHost)}, *temp);
    EXPECT_EQ(ledger.connectedHosts(), std::vector<std::string>{kHost});
}

TEST(RemoteHistoryBatch, ALargeBatchStoresItsCompleteDaysEarly) {
    HistoryBatch batch(kHost, 1);
    for (const auto* d : {"2026-11-01", "2026-11-02", "2026-11-03"}) batch.add(std::get<HistoryDay>(day(d)));
    batch.add(std::get<HistoryPerf>(perfLine("2026-11-03")));
    const auto early = batch.takeCompleted();
    ASSERT_EQ(early.size(), 2u);
    EXPECT_EQ(early[0].day, "2026-11-01");
    EXPECT_EQ(early[1].day, "2026-11-02");
    ASSERT_EQ(batch.days().size(), 1u);
    EXPECT_EQ(batch.days()[0].day, "2026-11-03");
    EXPECT_EQ(batch.days()[0].perf.size(), 1u);
    EXPECT_EQ(batch.byteCount(), batch.days()[0].byteCount());
}

// REMOTE 5.4: a stored aggregate reads back as the local analysis holds it, null costs included.
TEST(RemoteHistoryMerge, StoredAggregatesReadBackExactly) {
    history::DayAggregate day;
    day.requests = 5;
    day.input = 1000;
    day.cachedInput = 300;
    day.cacheWriteInput = 40;
    day.output = 700;
    day.reasoning = 90;
    day.measuredOutput = 600;
    day.measuredMs = 4000;
    day.unfinalized = 1;
    day.agents = 2;
    day.costMicroUsd = 1500;
    day.unpricedRequests = 1;
    day.models["gpt-5.5"] = {3, 500, 450, 3000, 1500, 0};
    day.models["mystery (beta)"] = {2, 200, 150, 1000, std::nullopt, std::nullopt};
    day.unpriced[{"mystery (beta)", std::string("acme"), true, false}] = 2;
    day.unpriced[{"", std::nullopt, false, true}] = 1;
    day.modes[{"gpt-5.5", ModeKind::Effort, "high", "recorded", std::nullopt}] = {3, 500, 450, 3000};
    day.modes[{"gpt-5.5", ModeKind::ServiceTier, "fast", "served", std::string("openai")}] = {1, 100, 90, 500};
    const auto text = history::encode(day);
    const auto decoded = decodeAggregateText(text);
    ASSERT_TRUE(decoded);
    EXPECT_EQ(history::encode(*decoded), text);

    const auto nulls = decodeAggregateText(aggregate(3, 340, "gpt-5.5", std::nullopt));
    ASSERT_TRUE(nulls);
    EXPECT_FALSE(nulls->costMicroUsd.has_value());
    EXPECT_FALSE(nulls->unpricedRequests.has_value());
    EXPECT_FALSE(nulls->models.at("gpt-5.5").costMicroUsd.has_value());
    ASSERT_EQ(nulls->modes.size(), 1u);
    EXPECT_EQ(nulls->modes.begin()->first.mode, "fast");
}

TEST(RemoteHistoryMerge, DaysAreSortedByDaySourceAndHost) {
    const auto row = [](const std::string& d, SourceKind source, const std::string& host = "local") {
        history::DailyUsage usage;
        usage.key = {d, source};
        usage.totals.requests = 1;
        usage.totals.output = 1;
        usage.host = host;
        return usage;
    };
    const std::vector<history::DailyUsage> local = {row("2026-11-02", SourceKind::ClaudeCode), row("2026-11-02", SourceKind::Codex),
                                                    row("2026-11-03", SourceKind::Codex)};
    const auto same = mergeDays(local, {});
    ASSERT_EQ(same.size(), 3u);
    const auto merged = mergeDays(local, {row("2026-11-03", SourceKind::Codex, kOtherHost), row("2026-11-01", SourceKind::Codex, kHost),
                                          row("2026-11-03", SourceKind::Codex, kHost), row("2026-11-02", SourceKind::ClaudeCode, kHost)});
    std::vector<std::string> ids;
    for (const auto& d : merged) ids.push_back(d.key.day + "/" + std::string(sourceId(d.key.source)) + (d.host == "local" ? "" : "/" + d.host));
    // Remote hosts of one day and source follow this computer in id order (`1F2E…` before `8C0D…`).
    EXPECT_EQ(ids, (std::vector<std::string>{"2026-11-01/codex/" + kHost, "2026-11-02/claudeCode", "2026-11-02/claudeCode/" + kHost,
                                             "2026-11-02/codex", "2026-11-03/codex", "2026-11-03/codex/" + kOtherHost,
                                             "2026-11-03/codex/" + kHost}));
}

TEST(RemoteHistoryMerge, PerformanceRecordsArePooledAcrossHosts) {
    perf::Record a;
    a.n = 3;
    a.toolLnSum = 1.5;
    perf::Record b;
    b.n = 4;
    b.toolLnSum = 2.25;
    const std::vector<perf::DayRecord> local = {{"2026-11-02", SourceKind::Codex, "gpt-5.5", a}};
    EXPECT_EQ(mergePerf(local, {}).size(), 1u);
    const auto pooled = mergePerf(local, {{"2026-11-02", SourceKind::Codex, "gpt-5.5", b}, {"2026-11-01", SourceKind::Codex, "gpt-5.5", b}});
    ASSERT_EQ(pooled.size(), 2u);
    EXPECT_EQ(pooled[0].day, "2026-11-01");
    EXPECT_EQ(pooled[1].day, "2026-11-02");
    EXPECT_EQ(pooled[1].record.n, 7);
    EXPECT_DOUBLE_EQ(pooled[1].record.toolLnSum, 3.75);
}
