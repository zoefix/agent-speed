#include <gtest/gtest.h>
#include <unistd.h>

#include <filesystem>
#include <fstream>

#include "agentspeed/json.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/remote_clock.hpp"
#include "agentspeed/remote_connection.hpp"
#include "agentspeed/remote_protocol.hpp"
#include "agentspeed/remote_relay_bundle.hpp"
#include "agentspeed/tz.hpp"
#include "remote_test_support.hpp"

namespace {

namespace fs = std::filesystem;
using namespace agentspeed;
using namespace agentspeed::remote;
using remote_test::kNow;

Zone utc() { return {"UTC", utcTzif()}; }

TEST(RemoteEncoder, PriceTablesAreCompactWithLiteralsKept) {
    const auto compact = compactPriceTable("{ \"a\" : [ 1.50 , 2e3 ],\n \"b\" : \"x y\\\" z\" }");
    EXPECT_EQ(compact, R"({"a":[1.50,2e3],"b":"x y\" z"})");
    EXPECT_FALSE(compactPriceTable("[1]"));
    EXPECT_FALSE(compactPriceTable("{oops"));
    const auto bundled = compactPriceTable(pricing::bundledPricesJson());
    ASSERT_TRUE(bundled);
    EXPECT_EQ(bundled->find('\n'), std::string::npos);
}

TEST(RemoteEncoder, StartCarriesSourcesZoneAndPrices) {
    const auto line = encode::start(kNow, {SourceKind::ClaudeCode, SourceKind::Codex}, utc(), R"({"version":1})");
    ASSERT_TRUE(line);
    EXPECT_EQ(line->back(), '\n');
    const auto text = line->substr(0, line->size() - 1);
    json::Document doc;
    ASSERT_TRUE(doc.parse(text));
    const auto root = doc.root();
    EXPECT_TRUE(root["t"].equals("start"));
    EXPECT_EQ(root["protocol"].truncated(), 1);
    EXPECT_EQ(root["todayStartMs"].truncated(), kNow);
    ASSERT_EQ(root["sources"].size(), 2u);
    EXPECT_TRUE(root["sources"].at(0).equals("codex"));
    EXPECT_TRUE(root["sources"].at(1).equals("claudeCode"));
    EXPECT_TRUE(root["zone"]["id"].equals("UTC"));
    EXPECT_TRUE(root["zone"]["tzif"].equals(encode::base64(utcTzif())));
    EXPECT_EQ(root["prices"].raw(), R"({"version":1})");
}

TEST(RemoteEncoder, SmallLines) {
    EXPECT_EQ(encode::ping(3), "{\"t\":\"ping\",\"id\":3}\n");
    EXPECT_EQ(encode::today(5), "{\"t\":\"today\",\"todayStartMs\":5}\n");
    EXPECT_EQ(encode::watch(true), "{\"t\":\"watch\",\"on\":true}\n");
    EXPECT_EQ(encode::prices("{}"), "{\"t\":\"prices\",\"table\":{}}\n");
    EXPECT_EQ(encode::history({{"2026-05-02/codex", "bb"}, {"2026-05-01/codex", "aa"}}),
              "{\"t\":\"history\",\"digests\":{\"2026-05-01/codex\":\"aa\",\"2026-05-02/codex\":\"bb\"}}\n");
    EXPECT_EQ(encode::history({}), "{\"t\":\"history\",\"digests\":{}}\n");
    EXPECT_EQ(encode::zone({"Asia/Taipei", "TZ"}), "{\"t\":\"zone\",\"id\":\"Asia/Taipei\",\"tzif\":\"VFo=\"}\n");
    EXPECT_EQ(encode::base64(""), "");
    EXPECT_EQ(encode::base64("f"), "Zg==");
    EXPECT_EQ(encode::base64("fo"), "Zm8=");
    EXPECT_EQ(encode::base64("foo"), "Zm9v");
}

TEST(RemoteEncoder, LinesBeyond4MiBAreRefused) {
    EXPECT_FALSE(encode::prices("{\"x\":\"" + std::string(limits::kAppLineBytes, 'x') + "\"}"));
    EXPECT_TRUE(encode::prices("{\"x\":\"" + std::string(limits::kAppLineBytes - 40, 'x') + "\"}"));
}

TEST(RemoteEncoder, ZonesAreTzif) {
    EXPECT_TRUE(Zone::isTzif(utcTzif()));
    EXPECT_TRUE(TimeZone::fromData(utcTzif()));
    EXPECT_FALSE(Zone::isTzif("TZif"));
    EXPECT_TRUE(Zone::validId("America/Argentina/Buenos_Aires"));
    EXPECT_TRUE(Zone::validId("Etc/GMT+5"));
    EXPECT_FALSE(Zone::validId("../etc/passwd"));
    EXPECT_FALSE(Zone::validId("/etc/localtime"));
    EXPECT_FALSE(Zone::validId("a b"));
    const auto zone = currentZone();
    EXPECT_TRUE(Zone::validId(zone.id)) << zone.id;
    EXPECT_TRUE(Zone::isTzif(zone.tzif));
    EXPECT_EQ(currentZone(std::string("UTC")).id, fs::exists("/usr/share/zoneinfo/UTC") ? "UTC" : zone.id);
}

TEST(RemoteClock, HelloGivesTheOffsetUntilTheFirstSample) {
    ClockEstimator clock;
    EXPECT_EQ(clock.offsetMs(), std::nullopt);
    clock.noteHello(kNow + 3000, kNow);
    EXPECT_EQ(clock.offsetMs(), 3000);
    clock.addSample(kNow, kNow + 100, kNow + 1050);
    EXPECT_EQ(clock.offsetMs(), 1000);
}

TEST(RemoteClock, SmallestRoundTripOfTheLastEightWins) {
    ClockEstimator clock;
    clock.addSample(kNow, kNow + 10, kNow + 505);  // 500, round trip 10
    for (int i = 1; i <= 7; ++i) clock.addSample(kNow, kNow + 100, kNow + 50 + 200);
    EXPECT_EQ(clock.offsetMs(), 500);
    clock.addSample(kNow, kNow + 100, kNow + 50 + 200);
    EXPECT_EQ(clock.samples().size(), 8u);
    EXPECT_EQ(clock.offsetMs(), 200);
    clock.addSample(kNow + 10, kNow, kNow);
    EXPECT_EQ(clock.samples().size(), 8u);
}

TEST(RemoteClock, HalfMillisecondsRound) {
    ClockEstimator clock;
    clock.addSample(kNow, kNow + 1, kNow + 1);  // 0.5
    EXPECT_EQ(clock.offsetMs(), 1);
    ClockEstimator negative;
    negative.addSample(kNow, kNow + 1, kNow);  // -0.5
    EXPECT_EQ(negative.offsetMs(), -1);
}

TEST(RemoteClock, ConversionShiftsEveryTimeAndKeepsDurations) {
    RemoteAgent agent;
    agent.inFlightStartMs = kNow - 500;
    agent.lastLineMs = kNow;
    work::Action action;
    action.callMs = kNow - 100;
    action.resultMs = kNow - 50;
    agent.actions.push_back(action);
    const auto a = std::get<RemoteAgent>(*converted(agent, 2000));
    EXPECT_EQ(a.inFlightStartMs, kNow - 2500);
    EXPECT_EQ(a.lastLineMs, kNow - 2000);
    EXPECT_EQ(a.actions[0].callMs, kNow - 2100);
    EXPECT_EQ(a.actions[0].resultMs, kNow - 2050);
    RemoteResponse response;
    response.record.startMs = kNow - 1000;
    response.record.endMs = kNow;
    const auto r = std::get<RemoteResponse>(*converted(response, -300));
    EXPECT_EQ(r.record.endMs - *r.record.startMs, 1000);
    EXPECT_EQ(r.record.endMs, kNow + 300);
    EXPECT_EQ(r.record.firstMs, std::nullopt);
    RemoteTool tool;
    tool.action.callMs = kNow;
    tool.action.resultMs = kNow + 5;
    EXPECT_EQ(std::get<RemoteTool>(*converted(tool, 5)).action.resultMs, kNow);
    EXPECT_TRUE(std::holds_alternative<Ready>(*converted(Ready{}, 99)));
}

TEST(RemoteClock, ConversionThatLeavesTheRangeIsInvalid) {
    RemoteResponse response;
    response.record.endMs = kEarliestMs + 10;
    EXPECT_FALSE(converted(response, 100));
    RemoteAgent agent;
    agent.lastLineMs = kLatestMs;
    EXPECT_FALSE(converted(agent, -1));
    EXPECT_TRUE(converted(agent, 1));
}

TEST(RemoteClock, MidnightsFollowTheZone) {
    ConnectionEnvironment environment;
    environment.zone = utc;
    EXPECT_EQ(environment.todayStartMs(kNow), kNow / 86'400'000 * 86'400'000);
    EXPECT_EQ(environment.nextMidnightMs(kNow), (kNow / 86'400'000 + 1) * 86'400'000);
    if (!fs::exists("/usr/share/zoneinfo/Asia/Taipei")) GTEST_SKIP() << "no zoneinfo";
    environment.zone = [] { return currentZone(std::string("Asia/Taipei")); };
    const auto start = environment.todayStartMs(kNow);
    EXPECT_EQ((start + 8 * 3'600'000) % 86'400'000, 0);
    EXPECT_LE(start, kNow);
    EXPECT_GT(start + 86'400'000, kNow);
    EXPECT_EQ(environment.nextMidnightMs(kNow), start + 86'400'000);
}

class RemoteRelayBundle : public ::testing::Test {
protected:
    void SetUp() override {
        dir_ = fs::temp_directory_path() / ("as-bundle-" + std::to_string(::getpid()));
        fs::remove_all(dir_);
        fs::create_directories(dir_);
    }
    void TearDown() override { fs::remove_all(dir_); }
    void write(const std::string& name, const std::string& text) const { std::ofstream(dir_ / name, std::ios::binary) << text; }
    fs::path dir_;
};

TEST_F(RemoteRelayBundle, FindsBothArchitecturesWithTheirDigests) {
    write("agent-speed-relay-linux-x86_64", "abc");
    write("agent-speed-relay-linux-aarch64", "");
    RelayBundle bundle(dir_.string(), "1.6.1");
    const auto x86 = bundle.executable(Arch::X86_64);
    ASSERT_TRUE(x86);
    EXPECT_EQ(x86->sha256, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    EXPECT_EQ(x86->size, 3);
    EXPECT_EQ(x86->installedName, "agent-speed-relay-1.6.1-x86_64-ba7816bf8f01");
    EXPECT_FALSE(bundle.executable(Arch::Aarch64));
    EXPECT_FALSE(RelayBundle(std::nullopt, "1").executable(Arch::X86_64));
}

TEST_F(RemoteRelayBundle, DigestFollowsAChangedFile) {
    write("agent-speed-relay-linux-x86_64", "abc");
    RelayBundle bundle(dir_.string(), "1");
    const auto first = bundle.executable(Arch::X86_64)->sha256;
    write("agent-speed-relay-linux-x86_64", "abcd");
    EXPECT_NE(bundle.executable(Arch::X86_64)->sha256, first);
}

TEST_F(RemoteRelayBundle, LocatesTheInstallLayoutsAndTheOverride) {
    EXPECT_EQ(RelayBundle::locate({"AGENT_SPEED_RELAY_DIR=/dev/relays"}, "/usr/bin/agent-speed"), "/dev/relays");
    fs::create_directories(dir_ / "usr/bin");
    fs::create_directories(dir_ / "usr/share/agent-speed/relay");
    std::ofstream(dir_ / "usr/share/agent-speed/relay/agent-speed-relay-linux-aarch64") << "x";
    EXPECT_EQ(RelayBundle::locate({}, (dir_ / "usr/bin/agent-speed").string()), (dir_ / "usr/share/agent-speed/relay").string());
    fs::create_directories(dir_ / "build/relay");
    std::ofstream(dir_ / "build/relay/agent-speed-relay-linux-x86_64") << "x";
    EXPECT_EQ(RelayBundle::locate({}, (dir_ / "build/agent-speed").string()), (dir_ / "build/relay").string());
    EXPECT_EQ(RelayBundle::locate({}, (dir_ / "nowhere/bin/agent-speed").string()), std::nullopt);
}

}  // namespace
