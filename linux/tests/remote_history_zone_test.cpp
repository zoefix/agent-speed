#include <gtest/gtest.h>

#include <memory>

#include "agentspeed/history_store.hpp"
#include "agentspeed/remote_history.hpp"
#include "remote_history_support.hpp"

// REMOTE 8.3 Time zone and Bounds, and the staging of a large batch.
using namespace remote_history_lines;

namespace {

struct Clock {
    std::shared_ptr<std::string> zone = std::make_shared<std::string>("Asia/Taipei");
};

HistoryLedger ledgerOf(history::Store& store, const Clock& clock, history::RemoteLimits limits = {},
                       std::size_t flushBytes = HistorySyncState::kFlushBytes) {
    HistoryLedger ledger(limits, flushBytes, [zone = clock.zone] { return *zone; });
    ledger.load(store);
    return ledger;
}

HistoryEffect sync(HistoryLedger& ledger, const std::vector<RelayMessage>& messages, history::Store& store, const std::string& host = kHost) {
    ledger.receive(due(host), store);
    std::vector<HostEvent> events;
    for (const auto& m : messages) events.push_back(event(m, host));
    events.push_back(event(done(), host));
    return receive(ledger, events, store);
}

std::vector<std::string> daysOf(const history::Store& store, const std::string& host) {
    std::vector<std::string> out;
    for (const auto& row : store.remoteDayRows()) {
        if (row.host == host) out.push_back(row.day);
    }
    return out;
}

}  // namespace

TEST(RemoteHistoryZone, TheZoneColumnIsAddedToAnEarlierTable) {
    TempStore temp;
    temp.store->connection().execute(
        "DROP TABLE remote_hosts; CREATE TABLE remote_hosts (id TEXT PRIMARY KEY, name TEXT);"
        "INSERT INTO remote_hosts VALUES ('" + kHost + "', 'devbox');");
    temp.store.reset();
    auto reopened = history::Store::open(temp.path());
    reopened->ensureRemoteTables();
    EXPECT_EQ(reopened->remoteHostNames(), (std::map<std::string, std::optional<std::string>>{{kHost, "devbox"}}));
    EXPECT_TRUE(reopened->remoteHostZones().empty());
    reopened->connection().execute("UPDATE remote_hosts SET zone = 'UTC'");
    EXPECT_EQ(reopened->remoteHostZones(), (std::map<std::string, std::string>{{kHost, "UTC"}}));
}

TEST(RemoteHistoryZone, RowsKeepTheZoneTheyWereComputedIn) {
    TempStore temp;
    Clock clock;
    auto ledger = ledgerOf(*temp, clock);
    sync(ledger, {day("2026-11-01"), day("2026-11-02")}, *temp);
    EXPECT_EQ(temp.store->remoteHostZones(), (std::map<std::string, std::string>{{kHost, "Asia/Taipei"}}));
    EXPECT_EQ(ledgerOf(*temp, clock).rows().zones(), (std::map<std::string, std::string>{{kHost, "Asia/Taipei"}}));
    EXPECT_EQ(ledger.receive(due(), *temp),
              request(kHost, {{"2026-11-01/codex", "aaaaaaaaaaaaaaa1"}, {"2026-11-02/codex", "aaaaaaaaaaaaaaa2"}}));
}

TEST(RemoteHistoryZone, ASyncInAnotherZoneSendsNoDigestsAndReplacesEveryRow) {
    TempStore temp;
    Clock clock;
    auto ledger = ledgerOf(*temp, clock);
    sync(ledger, {day("2026-11-01"), perfLine("2026-11-01"), day("2026-11-02")}, *temp);
    sync(ledger, {day("2026-11-02")}, *temp, kOtherHost);

    *clock.zone = "America/Los_Angeles";
    EXPECT_EQ(ledger.receive(due(), *temp), request(kHost, {}));
    // The relay relabelled its days: the second one moved, the first one is gone with its logs.
    EXPECT_EQ(receive(ledger, {event(day("2026-11-03", SourceKind::Codex, aggregate(), "cccccccccccccccc")), event(done())}, *temp),
              HistoryEffect::changed());
    EXPECT_EQ(ledger.rows().digests(kHost), (std::map<std::string, std::string>{{"2026-11-03/codex", "cccccccccccccccc"}}));
    EXPECT_EQ(daysOf(*temp, kHost), std::vector<std::string>{"2026-11-03"});
    EXPECT_EQ(temp.count("remote_perf"), 0);
    EXPECT_EQ(temp.store->remoteHostZones()[kHost], "America/Los_Angeles");
    // Another host is only replaced at its own sync.
    EXPECT_EQ(ledger.rows().digests(kOtherHost).size(), 1u);
    EXPECT_EQ(temp.store->remoteHostZones()[kOtherHost], "Asia/Taipei");

    // Back in step: digests again, and the next batch merges.
    EXPECT_EQ(ledger.receive(due(), *temp), request(kHost, {{"2026-11-03/codex", "cccccccccccccccc"}}));
    receive(ledger, {event(day("2026-11-04")), event(done())}, *temp);
    EXPECT_EQ(ledger.rows().digests(kHost).size(), 2u);
}

TEST(RemoteHistoryZone, AnEmptyAnswerInAnotherZoneLeavesTheHostWithoutRows) {
    TempStore temp;
    Clock clock;
    auto ledger = ledgerOf(*temp, clock);
    sync(ledger, {day("2026-11-01")}, *temp);
    *clock.zone = "Europe/Berlin";
    EXPECT_EQ(sync(ledger, {}, *temp), HistoryEffect::changed());
    EXPECT_TRUE(ledger.rows().empty());
    EXPECT_TRUE(ledger.rows().zones().empty());
    EXPECT_TRUE(temp.store->remoteHostNames().empty());
}

TEST(RemoteHistoryZone, RowsStoredWithoutAZoneTakeTheNextSyncsZone) {
    TempStore temp;
    Clock clock;
    auto ledger = ledgerOf(*temp, clock);
    sync(ledger, {day("2026-11-01")}, *temp);
    temp.store->connection().execute("UPDATE remote_hosts SET zone = NULL");
    auto reloaded = ledgerOf(*temp, clock);
    EXPECT_EQ(reloaded.receive(due(), *temp), request(kHost, {{"2026-11-01/codex", "aaaaaaaaaaaaaaa1"}}));
    EXPECT_EQ(reloaded.receive(event(done()), *temp), HistoryEffect::changed());
    EXPECT_EQ(temp.store->remoteHostZones(), (std::map<std::string, std::string>{{kHost, "Asia/Taipei"}}));
    EXPECT_EQ(reloaded.rows().digests(kHost).size(), 1u);
}

TEST(RemoteHistoryZone, ABatchBeyondTheBoundsIsDiscardedUntilASyncSucceeds) {
    TempStore temp;
    Clock clock;
    auto ledger = ledgerOf(*temp, clock, {3, 2});
    sync(ledger, {day("2026-11-01"), perfLine("2026-11-01"), day("2026-11-02")}, *temp);
    const auto stored = temp.store->remoteDayRows();

    // Two new days would leave four rows.
    EXPECT_EQ(sync(ledger, {day("2026-11-03"), day("2026-11-04")}, *temp), HistoryEffect::tooLargeChanged());
    EXPECT_EQ(ledger.tooLarge(), std::set<std::string>{kHost});
    EXPECT_EQ(temp.store->remoteDayRows(), stored);
    EXPECT_EQ(ledger.rows().digests(kHost).size(), 2u);
    // Records count too; still too large changes nothing more.
    EXPECT_EQ(sync(ledger, {day("2026-11-02"), perfLine("2026-11-02", SourceKind::Codex, "a"), perfLine("2026-11-02", SourceKind::Codex, "b")}, *temp),
              HistoryEffect::none());
    EXPECT_EQ(temp.count("remote_perf"), 1);

    EXPECT_EQ(sync(ledger, {day("2026-11-03")}, *temp), HistoryEffect::changed());
    EXPECT_TRUE(ledger.tooLarge().empty());
    EXPECT_EQ(ledger.rows().digests(kHost).size(), 3u);
    EXPECT_EQ(sync(ledger, {day("2026-11-04")}, *temp), HistoryEffect::tooLargeChanged());
    EXPECT_EQ(sync(ledger, {}, *temp), HistoryEffect::tooLargeChanged());
    EXPECT_TRUE(ledger.tooLarge().empty());
}

TEST(RemoteHistoryZone, ABatchAloneBeyondTheBoundsStopsBeingKept) {
    TempStore temp;
    auto ledger = ledgerOf(*temp, Clock(), {2, 10});
    ledger.receive(due(), *temp);
    receive(ledger, {event(day("2026-11-01")), event(day("2026-11-02")), event(day("2026-11-03")), event(day("2026-11-04"))}, *temp);
    EXPECT_EQ(ledger.receive(event(done()), *temp), HistoryEffect::tooLargeChanged());
    EXPECT_TRUE(ledger.rows().empty());
    EXPECT_EQ(temp.count("remote_days"), 0);

    // Deleting the host's history forgets it.
    EXPECT_EQ(ledger.deleteHost(kHost, *temp), HistoryEffect::tooLargeChanged());
    EXPECT_TRUE(ledger.tooLarge().empty());
}

TEST(RemoteHistoryZone, ALargeBatchWaitsInTheStagingTablesUntilItCommits) {
    TempStore temp;
    Clock clock;
    auto ledger = ledgerOf(*temp, clock, {}, 1);
    ledger.receive(due(), *temp);
    receive(ledger, {event(day("2026-11-01")), event(perfLine("2026-11-01")), event(day("2026-11-02")), event(day("2026-11-03"))}, *temp);
    EXPECT_EQ(temp.count("temp.remote_stage_days"), 2);
    EXPECT_EQ(temp.count("temp.remote_stage_perf"), 1);
    EXPECT_EQ(temp.count("remote_days"), 0);

    EXPECT_EQ(ledger.receive(event(done()), *temp), HistoryEffect::changed());
    EXPECT_EQ(temp.count("remote_days"), 3);
    EXPECT_EQ(temp.count("remote_perf"), 1);
    EXPECT_EQ(temp.count("temp.remote_stage_days"), 0);
    EXPECT_EQ(ledger.rows().digests(kHost).size(), 3u);
    EXPECT_EQ(ledger.rows().perfRecords().size(), 1u);
    EXPECT_EQ(ledger.rows().zones(), (std::map<std::string, std::string>{{kHost, "Asia/Taipei"}}));

    // A staged day sent again replaces its row and its records; a broken connection leaves the store as it was.
    const auto changed = day("2026-11-01", SourceKind::Codex, aggregate(3, 7), "dddddddddddddddd");
    receive(ledger, {event(changed), event(day("2026-11-05")), event(day("2026-11-06")), status(StatusCode::Unreachable), event(done())}, *temp);
    EXPECT_EQ(temp.count("remote_days"), 3);
    receive(ledger,
            {status(StatusCode::Connected), event(day("2026-11-07")), event(changed), event(day("2026-11-08")), event(done())}, *temp);
    EXPECT_EQ(temp.count("remote_days"), 5);
    EXPECT_EQ(temp.count("remote_perf"), 0);
    EXPECT_EQ(ledger.rows().digests(kHost)["2026-11-01/codex"], "dddddddddddddddd");
    EXPECT_EQ(temp.count("temp.remote_stage_days"), 0);
}

TEST(RemoteHistoryZone, AStagedBatchBeyondTheBoundsChangesNothing) {
    TempStore temp;
    auto ledger = ledgerOf(*temp, Clock(), {3, 10}, 1);
    sync(ledger, {day("2026-11-01"), day("2026-11-02")}, *temp);
    EXPECT_EQ(sync(ledger, {day("2026-11-03"), day("2026-11-04"), day("2026-11-05")}, *temp), HistoryEffect::tooLargeChanged());
    EXPECT_EQ(temp.count("remote_days"), 2);
    EXPECT_EQ(temp.count("temp.remote_stage_days"), 0);
    EXPECT_EQ(ledger.rows().digests(kHost).size(), 2u);
}

TEST(RemoteHistoryZone, ABatchThatCouldNotBeStagedCommitsNothing) {
    TempStore temp;
    auto ledger = ledgerOf(*temp, Clock(), {}, 1);
    temp.store->connection().execute("DROP TABLE temp.remote_stage_perf; DROP TABLE temp.remote_stage_days;");
    EXPECT_EQ(sync(ledger, {day("2026-11-01"), day("2026-11-02"), day("2026-11-03")}, *temp), HistoryEffect::none());
    EXPECT_EQ(temp.count("remote_days"), 0);
    EXPECT_TRUE(ledger.rows().empty());
}

TEST(RemoteHistoryZone, BatchCountsFollowRepeatedDaysAndRecords) {
    HistoryBatch batch(kHost, 1);
    const auto add = [&](const RelayMessage& message) {
        if (const auto* d = std::get_if<HistoryDay>(&message)) batch.add(*d);
        if (const auto* p = std::get_if<HistoryPerf>(&message)) batch.add(*p);
    };
    add(day("2026-11-01"));
    add(perfLine("2026-11-01", SourceKind::Codex, "a"));
    add(perfLine("2026-11-01", SourceKind::Codex, "b"));
    add(day("2026-11-02"));
    batch.takeCompleted();
    add(perfLine("2026-11-02", SourceKind::Codex, "a"));
    add(perfLine("2026-11-02", SourceKind::Codex, "a"));
    EXPECT_EQ(batch.dayCount(), 2);
    EXPECT_EQ(batch.perfCount(), 3);
    add(day("2026-11-01"));
    EXPECT_EQ(batch.dayCount(), 2);
    EXPECT_EQ(batch.perfCount(), 1);
    add(day("2026-11-02"));
    EXPECT_EQ(batch.dayCount(), 2);
    EXPECT_EQ(batch.perfCount(), 0);
    EXPECT_TRUE(batch.exceeds({1, 10}));
    EXPECT_FALSE(batch.exceeds({2, 0}));
}
