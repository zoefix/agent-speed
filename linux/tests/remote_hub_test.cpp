#include <gtest/gtest.h>
#include <unistd.h>

#include <filesystem>
#include <fstream>

#include "agentspeed/remote_hub.hpp"
#include "remote_test_support.hpp"

// REMOTE 2.3, 4.4, 7 Sources: one connection per enabled host, kept in step with the settings.
namespace {

namespace fs = std::filesystem;
using namespace agentspeed;
using namespace agentspeed::remote;
using namespace remote_test;

const std::string kOther = "9D1E7C1B-2222-4333-9444-555566667777";

class RemoteHub : public ::testing::Test {
protected:
    void SetUp() override {
        dir_ = fs::temp_directory_path() / ("as-hub-" + std::to_string(::getpid()));
        fs::create_directories(dir_);
        std::ofstream(dir_ / "agent-speed-relay-linux-x86_64") << "relay";
        bundle_ = std::make_unique<RelayBundle>(dir_.string(), "1");
        ConnectionEnvironment environment;
        environment.transport = &transport_;
        environment.scheduler = &scheduler_;
        environment.relays = bundle_.get();
        environment.zone = [] { return Zone{"UTC", utcTzif()}; };
        hub_ = std::make_unique<Hub>(environment, [this](const HostEvent& e) { events_.push_back(e); });
    }
    void TearDown() override {
        hub_.reset();
        fs::remove_all(dir_);
    }

    void update(const std::vector<Host>& hosts, bool network = true, std::set<SourceKind> sources = {SourceKind::Codex},
                const std::string& prices = R"({"v":1})") {
        hub_->update(hosts, network, sources, prices);
    }
    std::optional<StatusCode> lastStatus(const std::string& id) const {
        for (auto it = events_.rbegin(); it != events_.rend(); ++it) {
            if (it->hostId != id) continue;
            if (const auto* s = std::get_if<HostStatus>(&it->kind)) return s->status.code;
        }
        return std::nullopt;
    }
    // Answers the last probe with an installed relay, so the run starts.
    FakeProcess& run() {
        const auto relay = *bundle_->executable(Arch::X86_64);
        transport_.last().out("system Linux x86_64\ntool sha256sum\nhash " + relay.sha256 + " " + relay.installedName + "\nend\n");
        transport_.last().exit(0);
        return transport_.last();
    }

    fs::path dir_;
    std::unique_ptr<RelayBundle> bundle_;
    FakeTransport transport_;
    FakeScheduler scheduler_;
    std::vector<HostEvent> events_;
    std::unique_ptr<Hub> hub_;
};

TEST_F(RemoteHub, StartsEnabledHostsAndKeepsThemOnAnIdenticalUpdate) {
    update({Host::make(kHost, "a"), Host::make(kOther, "b")});
    EXPECT_EQ(hub_->connectedHostIds(), (std::set<std::string>{kHost, kOther}));
    EXPECT_EQ(transport_.launched.size(), 2u);
    update({Host::make(kHost, "a"), Host::make(kOther, "b")});
    EXPECT_EQ(transport_.launched.size(), 2u);
    update({Host::make(kHost, "a", std::string("Renamed")), Host::make(kOther, "b")});
    EXPECT_EQ(transport_.launched.size(), 2u);
}

TEST_F(RemoteHub, DisablingAndRemovingStopTheConnection) {
    update({Host::make(kHost, "a"), Host::make(kOther, "b")});
    auto& first = *transport_.launched[0];
    update({Host::make(kHost, "a", std::nullopt, false), Host::make(kOther, "b")});
    EXPECT_TRUE(first.terminated);
    EXPECT_EQ(lastStatus(kHost), StatusCode::Disabled);
    EXPECT_EQ(hub_->connectedHostIds(), std::set<std::string>{kOther});
    auto& second = *transport_.launched[1];
    update({Host::make(kHost, "a", std::nullopt, false)});
    EXPECT_TRUE(second.terminated);
    EXPECT_TRUE(hub_->connectedHostIds().empty());
    scheduler_.advance(2'000);
    EXPECT_TRUE(second.killed);
}

TEST_F(RemoteHub, EditingTheDestinationFolderOrSourcesReconnects) {
    update({Host::make(kHost, "a")});
    update({Host::make(kHost, "b")});
    EXPECT_EQ(transport_.launched.size(), 2u);
    EXPECT_TRUE(transport_.launched[0]->terminated);
    update({Host::make(kHost, "b", std::nullopt, true, std::string("/srv/as"))});
    EXPECT_EQ(transport_.launched.size(), 3u);
    update({Host::make(kHost, "b", std::nullopt, true, std::string("/srv/as"))}, true, {SourceKind::Codex, SourceKind::ClaudeCode});
    EXPECT_EQ(transport_.launched.size(), 4u);
}

TEST_F(RemoteHub, TheMasterSwitchOffMeansNoSsh) {
    update({Host::make(kHost, "a"), Host::make(kOther, "b", std::nullopt, false)}, false);
    EXPECT_TRUE(transport_.launched.empty());
    EXPECT_EQ(lastStatus(kHost), StatusCode::NetworkOff);
    EXPECT_EQ(lastStatus(kOther), StatusCode::NetworkOff);
    update({Host::make(kHost, "a"), Host::make(kOther, "b", std::nullopt, false)}, true);
    EXPECT_EQ(transport_.launched.size(), 1u);
    EXPECT_EQ(lastStatus(kOther), StatusCode::Disabled);
    update({Host::make(kHost, "a"), Host::make(kOther, "b", std::nullopt, false)}, false);
    EXPECT_TRUE(transport_.launched[0]->terminated);
    EXPECT_EQ(lastStatus(kHost), StatusCode::NetworkOff);
}

TEST_F(RemoteHub, PricesGoToRunningRelaysWithoutReconnecting) {
    update({Host::make(kHost, "a")});
    auto& relay = run();
    EXPECT_NE(relay.input().find(R"("prices":{"v":1})"), std::string::npos);
    update({Host::make(kHost, "a")}, true, {SourceKind::Codex}, "{\n \"v\": 2\n}");
    EXPECT_EQ(transport_.launched.size(), 2u);
    EXPECT_NE(relay.input().find(R"({"t":"prices","table":{"v":2}})"), std::string::npos);
    update({Host::make(kHost, "a")}, true, {SourceKind::Codex}, "not json");
    EXPECT_EQ(relay.input().find("not json"), std::string::npos);
}

TEST_F(RemoteHub, InvalidHostsGetAStatusAndNoConnection) {
    update({Host::make(kHost, "bad;host"), Host::make("not-a-uuid", "b")});
    EXPECT_TRUE(transport_.launched.empty());
    EXPECT_EQ(lastStatus(kHost), StatusCode::InvalidDestination);
    EXPECT_EQ(lastStatus("not-a-uuid"), std::nullopt);
    const auto count = events_.size();
    update({Host::make(kHost, "bad;host")});
    EXPECT_EQ(events_.size(), count);
}

TEST_F(RemoteHub, WakeRetriesWaitingHostsAndHistoryAndWatchRouteToTheHost) {
    update({Host::make(kHost, "a"), Host::make(kOther, "b")});
    transport_.launched[0]->err("Connection timed out");
    transport_.launched[0]->exit(255);
    EXPECT_EQ(lastStatus(kHost), StatusCode::Unreachable);
    hub_->noteWake();
    EXPECT_EQ(transport_.launched.size(), 3u);
    auto& relay = run();
    relay.line(helloLine());
    relay.line(R"({"t":"ready"})");
    hub_->requestHistory(kHost, {{"2026-05-01/codex", "0123456789abcdef"}});
    hub_->requestHistory(kOther, {});
    hub_->setWatch(true);
    EXPECT_NE(relay.input().find(R"({"t":"history")"), std::string::npos);
    EXPECT_NE(relay.input().find(R"({"t":"watch","on":true})"), std::string::npos);
    hub_->stopAll();
    EXPECT_TRUE(relay.terminated);
    EXPECT_TRUE(hub_->connectedHostIds().empty());
}

}  // namespace
