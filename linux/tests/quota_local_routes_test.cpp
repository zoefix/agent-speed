#include <gtest/gtest.h>

#include "agentspeed/quota/routes/local.hpp"
#include "agentspeed/quota/vendors/values.hpp"
#include "agentspeed/sqlite.hpp"
#include "fixture_support.hpp"
#include "quota_vendor_support.hpp"

// QUOTAS 9.2 (log) and 9.3: the local routes against files in a temporary home.
namespace {

namespace sqlite = agentspeed::sqlite;
using agentspeed::quota::DevinPlanReader;
using agentspeed::quota::GrokLogReader;
using agentspeed::quota::Kind;
using agentspeed::quota::VendorEnvironment;
using quota2_test::TempHome;

std::string configLine(const std::string& ts, double used) {
    return R"({"ts": ")" + ts + R"(", "msg": "billing: fetched credits config", "ctx": {"config": {"creditUsagePercent": )" +
           std::to_string(used) + R"(, "currentPeriod": {"type": "PERIOD_TYPE_WEEKLY", "end": "2026-11-15T00:00:00Z"}}}})";
}

TEST(QuotaLocalRoutes, TailLinesDropTheCutFirstLine) {
    EXPECT_EQ(agentspeed::quota::tailLines("cut\nwhole\n\nlast", false), (std::vector<std::string_view>{"whole", "last"}));
    EXPECT_EQ(agentspeed::quota::tailLines("first\nsecond\n", true), (std::vector<std::string_view>{"first", "second"}));
}

TEST(QuotaLocalRoutes, GrokLogReadsTheLastTwoMegabytesWhenItChanges) {
    TempHome home;
    // The newest config is the one with the greatest `ts`, not the last line; an older one lies before the tail.
    std::string log = configLine("2026-11-10T09:00:00Z", 99) + "\n";
    log += std::string(GrokLogReader::kTailBytes, 'x') + "\n";
    log += configLine("2026-11-10T12:00:00Z", 40) + "\n";
    log += configLine("2026-11-10T11:00:00Z", 30) + "\n";
    log += R"({"ts": "2026-11-10T13:00:00Z", "msg": "other"})" "\n";
    home.write("grok/logs/unified.jsonl", log);
    auto env = VendorEnvironment::fixed(home.path.string(), {{"GROK_HOME", home.file("grok")}});
    GrokLogReader reader;
    ASSERT_TRUE(reader.refresh(env));
    ASSERT_TRUE(reader.snapshot().has_value());
    EXPECT_EQ(reader.snapshot()->source, agentspeed::SourceKind::GrokBuild);
    EXPECT_EQ(reader.snapshot()->observedMs, *agentspeed::quota::vendors::isoMs("2026-11-10T12:00:00Z"));
    ASSERT_EQ(reader.snapshot()->windows.size(), 1u);
    EXPECT_EQ(reader.snapshot()->windows[0].kind, Kind::SevenDay);
    EXPECT_DOUBLE_EQ(reader.snapshot()->windows[0].usedPercent, 40);
    EXPECT_FALSE(reader.refresh(env)) << "unchanged";

    std::ofstream(home.file("grok/logs/unified.jsonl"), std::ios::app) << configLine("2026-11-10T14:00:00Z", 55) << "\n";
    ASSERT_TRUE(reader.refresh(env));
    EXPECT_DOUBLE_EQ(reader.snapshot()->windows[0].usedPercent, 55);
}

TEST(QuotaLocalRoutes, GrokLogDefaultsToTheHomeFolder) {
    TempHome home;
    home.write(".grok/logs/unified.jsonl", configLine("2026-11-10T12:00:00Z", 12) + "\n");
    auto env = VendorEnvironment::fixed(home.path.string(), {});
    EXPECT_EQ(GrokLogReader::defaultFile(env), home.file(".grok/logs/unified.jsonl"));
    GrokLogReader reader;
    ASSERT_TRUE(reader.refresh(env));
    EXPECT_DOUBLE_EQ(reader.snapshot()->windows[0].usedPercent, 12);
    auto tilde = VendorEnvironment::fixed(home.path.string(), {{"GROK_HOME", "~/elsewhere"}});
    EXPECT_EQ(GrokLogReader::defaultFile(tilde), home.file("elsewhere/logs/unified.jsonl"));
}

void devinDatabase(const std::string& path, const std::vector<std::pair<std::string, std::string>>& rows) {
    std::filesystem::create_directories(std::filesystem::path(path).parent_path());
    auto db = sqlite::Connection::open(path, sqlite::OpenMode::ReadWrite);
    db.execute("CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)");
    for (const auto& [key, value] : rows) {
        auto insert = db.prepare("INSERT INTO ItemTable VALUES(?1, ?2)");
        insert.bind(1, std::string_view(key));
        insert.bindBlob(2, std::string_view(value));
        insert.step();
    }
}

TEST(QuotaLocalRoutes, DevinReadsTheNewestFolderAtItsLaunchTime) {
    TempHome home;
    const auto config = home.file("cfg");
    devinDatabase(home.file("cfg/Devin/User/globalStorage/state.vscdb"),
                  {{"windsurf.reactSettings.cachedPlanInfoData:old", R"({"endTimestamp": 1, "dailyRemainingPercent": 1})"}});
    std::filesystem::create_directories(home.file("cfg/Devin/logs/20261101T080000"));
    home.touch("cfg/Devin/User/globalStorage/state.vscdb", -3600);
    devinDatabase(home.file("cfg/Windsurf/User/globalStorage/state.vscdb"),
                  {{"windsurf.settings.cachedPlanInfo", R"({"endTimestamp": 5, "dailyRemainingPercent": 70, "weeklyRemainingPercent": 20,)"
                                                        R"( "weeklyResetAtUnix": 1794800000, "accountIdentityText": "me@example.com"})"}});
    for (const auto* launch : {"20261110T183000", "20261109T090000", "not-a-launch", "20261131T000000"}) {
        std::filesystem::create_directories(home.file(std::string("cfg/Windsurf/logs/") + launch));
    }
    auto env = VendorEnvironment::fixed(home.path.string(), {{"XDG_CONFIG_HOME", config}});
    auto taipei = agentspeed::TimeZone::load("Asia/Taipei");
    ASSERT_TRUE(taipei) << "tzdata";
    env.timeZone = taipei;
    const auto before = fixtures::listFiles(home.path);
    DevinPlanReader reader;
    ASSERT_TRUE(reader.refresh(env));
    ASSERT_TRUE(reader.snapshot().has_value());
    // 2026-11-10 18:30:00 in Taipei (UTC+8).
    EXPECT_EQ(reader.snapshot()->observedMs, *agentspeed::quota::vendors::isoMs("2026-11-10T10:30:00Z"));
    ASSERT_EQ(reader.snapshot()->windows.size(), 2u);
    EXPECT_EQ(reader.snapshot()->windows[0].kind, Kind::Other);
    EXPECT_EQ(reader.snapshot()->windows[0].minutes, 1440);
    EXPECT_DOUBLE_EQ(reader.snapshot()->windows[0].usedPercent, 30);
    EXPECT_EQ(reader.snapshot()->windows[1].kind, Kind::SevenDay);
    EXPECT_EQ(reader.snapshot()->windows[1].resetsAtMs, 1'794'800'000'000);
    EXPECT_FALSE(reader.refresh(env));
    EXPECT_EQ(fixtures::listFiles(home.path), before) << "read-only and immutable: no -wal, -shm or journal";
}

TEST(QuotaLocalRoutes, DevinWithoutALaunchFolderHasNoSnapshot) {
    TempHome home;
    devinDatabase(home.file(".config/Devin/User/globalStorage/state.vscdb"),
                  {{"windsurf.reactSettings.cachedPlanInfoData", R"({"dailyRemainingPercent": 50})"}});
    auto env = VendorEnvironment::fixed(home.path.string(), {});
    env.timeZone = agentspeed::TimeZone::utc();
    DevinPlanReader reader;
    EXPECT_FALSE(reader.refresh(env));
    EXPECT_FALSE(reader.snapshot().has_value());
    std::filesystem::create_directories(home.file(".config/Devin/logs/20261110T120000"));
    ASSERT_TRUE(reader.refresh(env));
    EXPECT_EQ(reader.snapshot()->observedMs, *agentspeed::quota::vendors::isoMs("2026-11-10T12:00:00Z"));
}

}  // namespace
