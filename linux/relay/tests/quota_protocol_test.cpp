#include <gtest/gtest.h>

#include <cmath>
#include <limits>

#include "agentspeed/remote_protocol.hpp"
#include "protocol.hpp"
#include "quota_lines.hpp"

// REMOTE 11.1 on the relay's side: the app's quota config decoded, and every quota line the relay writes accepted by
// the app's own decoder (5.6) with the values it was given.
using namespace agentspeed;
using namespace agentspeed::relay;

namespace {

constexpr std::int64_t kNow = 1794338773000;
const std::string kHost = "8C0D6B0A-1111-4222-8333-444455556666";
const std::string kStart =
    R"({"t":"start","protocol":1,"todayStartMs":1794268800000,"sources":["codex"],"zone":{"id":"UTC","tzif":""},"prices":{})";

std::optional<QuotaConfig> configOf(const std::string& line) {
    const auto command = decodeCommand(line);
    if (!command) return std::nullopt;
    if (const auto* quota = std::get_if<QuotaCommand>(&*command)) return quota->config;
    return std::nullopt;
}

remote::RelayMessage decoded(const std::string& line) {
    const remote::LineDecoder decoder(kHost);
    auto outcome = decoder.decode(line);
    if (const auto* error = std::get_if<remote::LineError>(&outcome)) ADD_FAILURE() << "the app refuses " << error->field << ": " << line;
    if (auto* message = std::get_if<remote::RelayMessage>(&outcome)) return *message;
    ADD_FAILURE() << "not decoded: " << line;
    return remote::Ready{};
}

remote::HostQuota hostReport(std::vector<quota::Window> windows, SourceKind source = SourceKind::Kimi) {
    remote::HostQuota r;
    r.snapshot = {source, kNow, std::move(windows)};
    r.running = true;
    r.signedIn = true;
    r.status = remote::HostQuota::Status::Ok;
    return r;
}

std::vector<quota::Window> roundTrip(std::vector<quota::Window> windows) {
    const auto line = quotaLine(hostReport(std::move(windows)));
    const auto message = decoded(line);
    const auto* q = std::get_if<remote::HostQuota>(&message);
    return q ? q->snapshot.windows : std::vector<quota::Window>{};
}

}  // namespace

TEST(RelayQuotaProtocol, DecodesTheConfigOfStartAndOfAQuotaLine) {
    auto command = decodeCommand(kStart + R"(,"quota":{"sources":["codex","kimi"],"network":true,"grokLive":false,"claudeBridge":true}})");
    ASSERT_TRUE(command);
    const auto& start = std::get<StartCommand>(*command);
    ASSERT_TRUE(start.quota);
    EXPECT_EQ(start.quota->sources, (std::set<SourceKind>{SourceKind::Codex, SourceKind::Kimi}));
    EXPECT_TRUE(start.quota->network);
    EXPECT_FALSE(start.quota->grokLive);
    EXPECT_TRUE(start.quota->claudeBridge);

    // An older app sends none; a config the relay cannot read leaves the start valid and the quotas idle.
    command = decodeCommand(kStart + "}");
    ASSERT_TRUE(command);
    EXPECT_FALSE(std::get<StartCommand>(*command).quota);
    command = decodeCommand(kStart + R"(,"quota":{"sources":["codex"],"network":"yes","grokLive":false,"claudeBridge":false}})");
    ASSERT_TRUE(command);
    EXPECT_FALSE(std::get<StartCommand>(*command).quota);

    const auto config = configOf(R"({"t":"quota","config":{"sources":["grokBuild","gemini","droid","antigravity","claudeCode"],)"
                                 R"("network":false,"grokLive":true,"claudeBridge":false,"later":1}})");
    ASSERT_TRUE(config);
    EXPECT_EQ(config->sources, (std::set<SourceKind>{SourceKind::ClaudeCode, SourceKind::Gemini, SourceKind::Antigravity,
                                                     SourceKind::GrokBuild, SourceKind::Droid}));
    EXPECT_TRUE(config->grokLive);
}

TEST(RelayQuotaProtocol, KeepsOnlySourcesAHostReads) {
    const auto config = configOf(R"({"t":"quota","config":{"sources":["claudeDesktop","devin","zcode","qoder","qoderCli",)"
                                 R"("workbuddy","someFutureSource","kimi"],"network":true,"grokLive":false,"claudeBridge":false}})");
    ASSERT_TRUE(config);
    EXPECT_EQ(config->sources, std::set<SourceKind>{SourceKind::Kimi});
}

TEST(RelayQuotaProtocol, RefusesAMalformedQuotaLine) {
    for (const auto* line : {R"({"t":"quota"})", R"({"t":"quota","config":[]})",
                             R"({"t":"quota","config":{"network":true,"grokLive":false,"claudeBridge":false}})",
                             R"({"t":"quota","config":{"sources":["kimi"],"grokLive":false,"claudeBridge":false}})",
                             R"({"t":"quota","config":{"sources":[7],"network":true,"grokLive":false,"claudeBridge":false}})",
                             R"({"t":"quota","config":{"sources":"kimi","network":true,"grokLive":false,"claudeBridge":false}})",
                             R"({"t":"quota","config":{"sources":[],"network":true,"grokLive":0,"claudeBridge":false}})"}) {
        EXPECT_FALSE(decodeCommand(line)) << line;
    }
}

TEST(RelayQuotaProtocol, TheAppReadsAQuotaLineAsWritten) {
    auto r = hostReport({quota::makeWindow(quota::Kind::FiveHour, 23.5, 1794348000000),
                     quota::makeWindow(quota::Kind::SevenDay, 41.25, std::nullopt, std::string("Claude/GPT")),
                     quota::makeWindow(quota::Kind::Other, 7, 1794400000000, std::nullopt, 1440),
                     quota::makeWindow(quota::Kind::Credits, 3, std::nullopt)},
                    SourceKind::Antigravity);
    r.running = false;
    r.status = remote::HostQuota::Status::Denied;
    const auto line = quotaLine(r);
    EXPECT_EQ(line.rfind(R"({"t":"quota","q":{"source":"antigravity","observedMs":1794338773000,"windows":[)", 0), 0u) << line;
    const auto message = decoded(line);
    const auto* q = std::get_if<remote::HostQuota>(&message);
    ASSERT_TRUE(q);
    EXPECT_EQ(q->hostId, kHost);
    EXPECT_EQ(q->snapshot, r.snapshot);
    EXPECT_FALSE(q->running);
    EXPECT_TRUE(q->signedIn);
    EXPECT_EQ(q->status, remote::HostQuota::Status::Denied);

    // Every status of 11.1.
    for (const auto status : {remote::HostQuota::Status::Ok, remote::HostQuota::Status::NoCredential, remote::HostQuota::Status::Expired,
                              remote::HostQuota::Status::Failed, remote::HostQuota::Status::NetworkOff,
                              remote::HostQuota::Status::NoCertificates, remote::HostQuota::Status::NotRunning}) {
        auto s = hostReport({});
        s.status = status;
        const auto m = decoded(quotaLine(s));
        ASSERT_TRUE(std::holds_alternative<remote::HostQuota>(m));
        EXPECT_EQ(std::get<remote::HostQuota>(m).status, status);
    }
}

TEST(RelayQuotaProtocol, BoundsWindowsAsTheAppValidatesThem) {
    const auto nan = std::numeric_limits<double>::quiet_NaN();
    const auto windows = roundTrip({
        quota::makeWindow(quota::Kind::FiveHour, 103, 1794348000000),
        quota::makeWindow(quota::Kind::SevenDay, 1e6, 99'999'999'999'999),     // a reset past 2100 is unknown
        quota::makeWindow(quota::Kind::Month, -4, std::nullopt, std::nullopt, 0),  // a length of 0 is unknown
        quota::makeWindow(quota::Kind::Other, 5, std::nullopt),                    // `other` without a length: left out
        quota::makeWindow(quota::Kind::FiveHour, nan, std::nullopt),               // no number: left out
        quota::makeWindow(quota::Kind::SevenDay, 1, std::nullopt, std::string(70, 'n')),
        quota::makeWindow(quota::Kind::SevenDay, 2, std::nullopt, std::string("Ge\xE2\x80\xAEmi\x01ni\xC2\x85")),
        quota::makeWindow(quota::Kind::SevenDay, 3, std::nullopt, std::string("\xE2\x80\x8F")),
    });
    ASSERT_EQ(windows.size(), 6u);
    EXPECT_DOUBLE_EQ(windows[0].usedPercent, 103);
    EXPECT_DOUBLE_EQ(windows[1].usedPercent, 1000);
    EXPECT_FALSE(windows[1].resetsAtMs);
    EXPECT_DOUBLE_EQ(windows[2].usedPercent, 0);
    EXPECT_FALSE(windows[2].minutes);
    EXPECT_EQ(windows[3].name, std::string(64, 'n'));
    EXPECT_EQ(windows[4].name, "Gemini");
    EXPECT_FALSE(windows[5].name);

    std::vector<quota::Window> many;
    for (int k = 0; k < 20; ++k) many.push_back(quota::makeWindow(quota::Kind::SevenDay, k, std::nullopt, "Model " + std::to_string(k)));
    many.push_back(quota::makeWindow(quota::Kind::FiveHour, 50, std::nullopt));
    const auto kept = roundTrip(many);
    ASSERT_EQ(kept.size(), 16u);
    EXPECT_EQ(kept[0].kind, quota::Kind::FiveHour);
}

TEST(RelayQuotaProtocol, SendsNothingTheAppWouldRefuse) {
    auto r = hostReport({});
    r.snapshot.observedMs = 900'000'000'000;  // 1998
    EXPECT_EQ(quotaLine(r), "");
    r = hostReport({}, SourceKind::ClaudeDesktop);
    EXPECT_EQ(quotaLine(r), "");
}

TEST(RelayQuotaProtocol, TheAppReadsTheRemovalAndTheBridgeLines) {
    const auto removed = decoded(quotaRemovedLine(SourceKind::Codex));
    ASSERT_TRUE(std::holds_alternative<remote::HostQuotaRemoved>(removed));
    EXPECT_EQ(std::get<remote::HostQuotaRemoved>(removed).source, SourceKind::Codex);

    using State = remote::HostQuotaBridge::State;
    for (const auto state : {State::Off, State::Waiting, State::Ok, State::Replaced, State::Unreadable, State::Failed}) {
        const auto line = quotaBridgeLine(state, "~/.claude/settings.json");
        const auto message = decoded(line);
        ASSERT_TRUE(std::holds_alternative<remote::HostQuotaBridge>(message)) << line;
        EXPECT_EQ(std::get<remote::HostQuotaBridge>(message).state, state);
        EXPECT_EQ(std::get<remote::HostQuotaBridge>(message).settingsPath, "~/.claude/settings.json");
    }
    EXPECT_EQ(quotaBridgeLine(State::Ok, "/srv/\xE2\x80\xAEsettings.json"), R"({"t":"quotaBridge","state":"ok","settingsPath":""})");
    EXPECT_EQ(std::get<remote::HostQuotaBridge>(decoded(quotaBridgeLine(State::Ok, std::string(5000, 'p')))).settingsPath, "");
}

TEST(RelayQuotaProtocol, DisplayTextKeepsOnlyWhatTheAppShows) {
    EXPECT_EQ(displayText("Gemini 3.1 Pro (High)", 64), "Gemini 3.1 Pro (High)");
    EXPECT_EQ(displayText("\xE6\x9C\x88\xE5\x88\x8A", 1), "\xE6\x9C\x88");
    EXPECT_EQ(displayText("a\xFF" "b", 64), "a\xEF\xBF\xBD" "b");
    EXPECT_EQ(displayText("a\xE2\x80\xA8" "b\xE2\x81\xA6", 64), "ab");
    EXPECT_FALSE(displayText("\x07", 64));
}
