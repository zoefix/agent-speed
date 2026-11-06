#include <gtest/gtest.h>

#include "agentspeed/history_store.hpp"
#include "agentspeed/json.hpp"
#include "history_sync.hpp"

using namespace agentspeed;
using namespace agentspeed::relay;

namespace {

history::DailyUsage usage(std::string day, SourceKind source, std::int64_t requests) {
    history::DailyUsage u;
    u.key = {std::move(day), source};
    u.totals.requests = requests;
    u.totals.output = requests * 10;
    u.totals.costMicroUsd = requests * 7;
    u.totals.unpricedRequests = 0;
    u.totals.models["gpt-5.5"] = {requests, requests * 10, 0, 0, requests * 7, 0};
    return u;
}

perf::DayRecord record(std::string day, SourceKind source, std::string model, std::int64_t n) {
    perf::DayRecord r;
    r.day = std::move(day);
    r.source = source;
    r.model = std::move(model);
    r.record.n = n;
    r.record.outputSum = n * 100;
    r.record.latencyBins[3] = n;
    r.record.toolN = 1;
    r.record.toolLnSum = 6.5;
    return r;
}

}  // namespace

TEST(RelayHistory, HashesTheFirstSixteenHexDigitsOfSha256) {
    EXPECT_EQ(sha256Hex16("abc"), "ba7816bf8f01cfea");
    EXPECT_EQ(sha256Hex16(""), "e3b0c44298fc1c14");
    EXPECT_EQ(digestOf("ab", {{"m", "c"}}), sha256Hex16("abc"));
}

TEST(RelayHistory, EncodesARecordWithEveryField) {
    perf::Record r;
    r.n = 2;
    r.outputSum = 300;
    r.durationMsSum = 4000;
    r.latencyBins[0] = 1;
    r.latencyBins[63] = 1;
    r.reasonN = 1;
    r.reasonOutput = 40;
    r.reasonDurationMsSum = 2000;
    r.cacheN = 2;
    r.cacheInput = 900;
    r.cacheCached = 800;
    r.toolN = 3;
    r.toolLnSum = 9.22029070282935;
    r.toolFailed = 1;
    r.activeMs = 7000;
    const auto text = encodeRecord(r);
    std::string bins = "[1";
    for (int k = 1; k < 63; ++k) bins += ",0";
    bins += ",1]";
    EXPECT_EQ(text, R"({"n":2,"outputSum":300,"durationMsSum":4000,"latencyBins":)" + bins +
                        R"(,"reasonN":1,"reasonOutput":40,"reasonDurationMsSum":2000,"cacheN":2,"cacheInput":900,"cacheCached":800,)"
                        R"("toolN":3,"toolLnSum":9.22029070282935,"toolFailed":1,"activeMs":7000})");
}

TEST(RelayHistory, SumsRecordsPerModelAndDigestsEachDay) {
    const std::vector<history::DailyUsage> days{usage("2026-10-20", SourceKind::Codex, 3), usage("2026-10-20", SourceKind::ClaudeCode, 1),
                                                usage("2026-10-21", SourceKind::Codex, 2)};
    const std::vector<perf::DayRecord> records{record("2026-10-20", SourceKind::Codex, "gpt-5.5", 1),
                                               record("2026-10-20", SourceKind::Codex, "gpt-5.5", 2),
                                               record("2026-10-20", SourceKind::Codex, "", 1),
                                               // Sent as U+FFFD, so after every ASCII model of the day.
                                               record("2026-10-20", SourceKind::Codex, "\xFF", 1),
                                               // A tool result after midnight on a day without responses.
                                               record("2026-10-22", SourceKind::Codex, "gpt-5.5", 0)};
    const auto sync = syncDays(days, records);
    ASSERT_EQ(sync.size(), 4u);
    EXPECT_EQ(sync[0].key(), "2026-10-20/claudeCode");
    EXPECT_EQ(sync[1].key(), "2026-10-20/codex");
    EXPECT_EQ(sync[2].key(), "2026-10-21/codex");
    EXPECT_EQ(sync[3].key(), "2026-10-22/codex");
    EXPECT_EQ(sync[1].aggregate, history::encode(days[0].totals));
    ASSERT_EQ(sync[1].perf.size(), 3u);
    EXPECT_EQ(sync[1].perf[0].first, "");
    EXPECT_EQ(sync[1].perf[1].first, "gpt-5.5");
    EXPECT_EQ(sync[1].perf[2].first, "\xEF\xBF\xBD");
    json::Document summed;
    ASSERT_TRUE(summed.parse(sync[1].perf[1].second));
    EXPECT_EQ(summed.root()["n"].truncated(), 3);
    EXPECT_EQ(summed.root()["latencyBins"].at(3).truncated(), 3);
    EXPECT_EQ(sync[1].digest, digestOf(sync[1].aggregate, sync[1].perf));
    EXPECT_EQ(sync[3].aggregate, history::encode(history::DayAggregate{}));
    EXPECT_EQ(sync[3].perf.size(), 1u);
    EXPECT_TRUE(sync[0].perf.empty());
    EXPECT_EQ(sync[0].digest, sha256Hex16(sync[0].aggregate));

    std::map<std::string, std::string> held{{"2026-10-20/codex", sync[1].digest}, {"2026-10-21/codex", "ffffffffffffffff"},
                                            {"2019-01-01/codex", "0000000000000000"}};
    const auto changed = changedDays(sync, held);
    ASSERT_EQ(changed.size(), 3u);
    EXPECT_EQ(changed[0]->key(), "2026-10-20/claudeCode");
    EXPECT_EQ(changed[1]->key(), "2026-10-21/codex");
    EXPECT_EQ(changed[2]->key(), "2026-10-22/codex");
    for (const auto& d : sync) held[d.key()] = d.digest;
    EXPECT_TRUE(changedDays(sync, held).empty());
}
