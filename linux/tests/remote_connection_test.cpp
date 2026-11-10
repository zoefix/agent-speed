#include <gtest/gtest.h>
#include <unistd.h>

#include <filesystem>
#include <fstream>

#include "agentspeed/remote_connection.hpp"
#include "agentspeed/remote_ssh.hpp"
#include "remote_test_support.hpp"

namespace {

namespace fs = std::filesystem;
using namespace agentspeed;
using namespace agentspeed::remote;
using namespace remote_test;

class RemoteConnection : public ::testing::Test {
protected:
    void SetUp() override {
        dir_ = fs::temp_directory_path() / ("as-connection-" + std::to_string(::getpid()));
        fs::create_directories(dir_);
        std::ofstream(dir_ / "agent-speed-relay-linux-x86_64", std::ios::binary) << std::string(600'000, 'R');
        bundle_ = std::make_unique<RelayBundle>(dir_.string(), "1.6.1");
        relay_ = *bundle_->executable(Arch::X86_64);
    }
    void TearDown() override { fs::remove_all(dir_); }

    std::shared_ptr<Connection> connect(Host host = Host::make(kHost, "dev@devbox")) {
        ConnectionEnvironment environment;
        environment.transport = &transport_;
        environment.scheduler = &scheduler_;
        environment.relays = bundle_.get();
        environment.zone = [] { return Zone{"UTC", utcTzif()}; };
        connection_ = Connection::make(std::move(host), SessionSettings{{SourceKind::Codex}, "{}", false, {}}, environment,
                                       [this](const HostEvent& e) { events_.push_back(e); });
        connection_->start();
        return connection_;
    }

    std::string installedProbe() const { return "system Linux x86_64\ntool sha256sum\nhash " + relay_.sha256 + " " + relay_.installedName + "\nend\n"; }

    // Probe answered with the relay installed; the run process is the last one.
    FakeProcess& running() {
        connect();
        transport_.last().out(installedProbe());
        transport_.last().exit(0);
        EXPECT_TRUE(transport_.last().isRun());
        return transport_.last();
    }

    FakeProcess& helloed() {
        auto& run = running();
        run.line(helloLine());
        return run;
    }

    std::vector<Status> statuses() const {
        std::vector<Status> out;
        for (const auto& e : events_) {
            if (const auto* s = std::get_if<HostStatus>(&e.kind)) out.push_back(s->status);
        }
        return out;
    }
    HostStatus lastStatus() const {
        for (auto it = events_.rbegin(); it != events_.rend(); ++it) {
            if (const auto* s = std::get_if<HostStatus>(&it->kind)) return *s;
        }
        return {};
    }
    std::vector<HostEvent::Message> messages() const {
        std::vector<HostEvent::Message> out;
        for (const auto& e : events_) {
            if (const auto* m = std::get_if<HostEvent::Message>(&e.kind)) out.push_back(*m);
        }
        return out;
    }
    // Advances the clock with a line every minute, so the silence rule never ends the relay.
    void keepAlive(FakeProcess& run, std::int64_t ms) {
        const auto target = scheduler_.now + ms;
        while (scheduler_.now < target) {
            scheduler_.advance(std::min<std::int64_t>(60'000, target - scheduler_.now));
            run.line(clockLine(999, scheduler_.now));
        }
    }
    int historyDues() const {
        return static_cast<int>(std::count_if(events_.begin(), events_.end(), [](const HostEvent& e) {
            return std::holds_alternative<HostEvent::HistoryDue>(e.kind);
        }));
    }

    fs::path dir_;
    std::unique_ptr<RelayBundle> bundle_;
    RelayExecutable relay_;
    FakeTransport transport_;
    FakeScheduler scheduler_;
    std::vector<HostEvent> events_;
    std::shared_ptr<Connection> connection_;
};

TEST_F(RemoteConnection, ProbeRunHelloSeedingReadyConnected) {
    connect();
    ASSERT_EQ(transport_.launched.size(), 1u);
    auto& probe = transport_.last();
    EXPECT_TRUE(probe.isProbe());
    EXPECT_EQ(probe.request.executable, "/usr/bin/ssh");
    EXPECT_EQ(probe.request.arguments[probe.request.arguments.size() - 2], "dev@devbox");
    EXPECT_NE(std::find(probe.request.environment.begin(), probe.request.environment.end(), "LC_ALL=C"), probe.request.environment.end());
    EXPECT_EQ(lastStatus().status.code, StatusCode::Connecting);
    probe.out(installedProbe());
    probe.exit(0);
    ASSERT_EQ(transport_.launched.size(), 2u);
    auto& run = transport_.last();
    EXPECT_TRUE(run.isRun());
    EXPECT_NE(run.command().find(relay_.installedName), std::string::npos);
    EXPECT_EQ(run.input().find("{\"t\":\"start\",\"protocol\":1,"), 0u);
    run.line(helloLine(kNow + 3000));
    EXPECT_EQ(lastStatus().status, Status::seeding(0));
    EXPECT_EQ(lastStatus().clockOffsetMs, 3000);
    EXPECT_EQ(lastStatus().relayVersion, "1.6.1");
    EXPECT_NE(run.input().find("{\"t\":\"ping\",\"id\":1}"), std::string::npos);
    run.line(R"({"t":"progress","value":0.5})");
    EXPECT_EQ(lastStatus().status, Status::seeding(0.5));
    run.line(agentLine("/a.jsonl", "idle", kNow + 2000));
    run.line(R"({"t":"ready"})");
    EXPECT_EQ(lastStatus().status.code, StatusCode::Connected);
    EXPECT_EQ(historyDues(), 1);
    run.line(responseLine("k1", "/a.jsonl", kNow + 3000));
    const auto all = messages();
    ASSERT_EQ(all.size(), 4u);
    EXPECT_TRUE(std::holds_alternative<Hello>(all[0].message));
    const auto& agent = std::get<RemoteAgent>(all[1].message);
    EXPECT_TRUE(all[1].seeded);
    EXPECT_EQ(agent.lastLineMs, kNow - 1000);
    EXPECT_TRUE(std::holds_alternative<Ready>(all[2].message));
    EXPECT_FALSE(all[3].seeded);
    EXPECT_EQ(std::get<RemoteResponse>(all[3].message).record.endMs, kNow);
}

TEST_F(RemoteConnection, UploadsWhenTheRelayIsMissing) {
    connect();
    transport_.last().out("system Linux x86_64\ntool sha256sum\nhash " + std::string(64, 'f') + " agent-speed-relay-old\nend\n");
    transport_.last().exit(0);
    auto& upload = transport_.last();
    ASSERT_TRUE(upload.isUpload());
    EXPECT_NE(upload.command().find(relay_.sha256), std::string::npos);
    EXPECT_EQ(upload.input(), std::string(600'000, 'R'));
    EXPECT_TRUE(upload.inputClosed);
    const auto seen = statuses();
    EXPECT_NE(std::find(seen.begin(), seen.end(), Status::installing(0)), seen.end());
    EXPECT_EQ(seen.back(), Status::installing(1));
    upload.out("hash " + relay_.sha256 + "\n");
    upload.exit(0);
    EXPECT_TRUE(transport_.last().isRun());
}

TEST_F(RemoteConnection, UploadMismatchIsUploadFailed) {
    connect();
    transport_.last().out(kProbeOutput);
    transport_.last().exit(0);
    transport_.last().out("hash " + std::string(64, '0') + "\n");
    transport_.last().exit(0);
    EXPECT_EQ(lastStatus().status.code, StatusCode::UploadFailed);
    EXPECT_EQ(lastStatus().retryAtMs, kNow + 300'000);
    const auto count = transport_.launched.size();
    connect();
    transport_.last().out(kProbeOutput);
    transport_.last().exit(0);
    transport_.last().exit(3);
    EXPECT_GT(transport_.launched.size(), count);
    EXPECT_EQ(lastStatus().status.code, StatusCode::UploadFailed);
}

TEST_F(RemoteConnection, ProbeVerdictsBecomeStatuses) {
    const auto probeEnds = [&](const std::string& out, int status, const std::string& err = "") {
        events_.clear();
        connect();
        transport_.last().out(out);
        transport_.last().err(err);
        transport_.last().exit(status);
        return lastStatus().status;
    };
    EXPECT_EQ(probeEnds("system Darwin arm64\ntool shasum -a 256\nend\n", 0), Status::unsupportedSystem("Darwin arm64"));
    EXPECT_EQ(probeEnds("system Linux x86_64\ntool none\nend\n", 0).code, StatusCode::CannotVerify);
    EXPECT_EQ(probeEnds("banner only\n", 0), Status::exited(0));
    EXPECT_EQ(probeEnds("", 255, "Permission denied (publickey).").code, StatusCode::AuthFailed);
    EXPECT_EQ(probeEnds("", 255, "Host key verification failed.").code, StatusCode::HostKeyUnknown);
    EXPECT_EQ(probeEnds("system Linux aarch64\ntool sha256sum\nend\n", 0).code, StatusCode::UploadFailed);
}

TEST_F(RemoteConnection, InvalidDestinationAndMissingSshLaunchNothing) {
    connect(Host::make(kHost, "bad host;id"));
    EXPECT_TRUE(transport_.launched.empty());
    EXPECT_EQ(lastStatus().status.code, StatusCode::InvalidDestination);
    EXPECT_EQ(lastStatus().retryAtMs, std::nullopt);
    EXPECT_EQ(scheduler_.pending(), 0u);
    connect(Host::make(kHost, "devbox", std::nullopt, true, std::string("/tmp/a b")));
    EXPECT_TRUE(transport_.launched.empty());
    transport_.sshAvailable = false;
    connect();
    EXPECT_TRUE(transport_.launched.empty());
    EXPECT_EQ(lastStatus().status.code, StatusCode::SshMissing);
    EXPECT_EQ(lastStatus().retryAtMs, kNow + 300'000);
}

TEST_F(RemoteConnection, SilenceEndsTheConnectionAsUnreachable) {
    auto& run = helloed();
    scheduler_.advance(149'000);
    EXPECT_FALSE(run.terminated);
    scheduler_.advance(1'000);
    EXPECT_TRUE(run.terminated);
    EXPECT_TRUE(run.inputClosed);
    scheduler_.advance(2'000);
    EXPECT_TRUE(run.killed);
    run.exit(143);
    EXPECT_EQ(lastStatus().status.code, StatusCode::Unreachable);
    EXPECT_EQ(lastStatus().retryAtMs, scheduler_.now + 5'000);
}

TEST_F(RemoteConnection, AnsweredPingsKeepItAlive) {
    auto& run = helloed();
    for (int i = 0; i < 10; ++i) {
        scheduler_.advance(30'000);
        run.line(clockLine(i + 2, scheduler_.now));
    }
    EXPECT_FALSE(run.terminated);
    EXPECT_NE(run.input().find("{\"t\":\"ping\",\"id\":11}"), std::string::npos);
}

TEST_F(RemoteConnection, BackoffSequenceAndReset) {
    connect();
    std::vector<std::int64_t> delays;
    for (int i = 0; i < 7; ++i) {
        transport_.last().err("ssh: connect to host devbox port 22: Connection refused");
        transport_.last().exit(255);
        delays.push_back(*lastStatus().retryAtMs - scheduler_.now);
        scheduler_.advance(delays.back());
    }
    EXPECT_EQ(delays, (std::vector<std::int64_t>{5'000, 10'000, 20'000, 40'000, 60'000, 60'000, 60'000}));
    EXPECT_EQ(transport_.launched.size(), 8u);
    connection_->retryNow();
    EXPECT_EQ(transport_.launched.size(), 8u);
}

TEST_F(RemoteConnection, BackoffResetsAfterSixtySecondsUp) {
    connect();
    for (int i = 0; i < 3; ++i) {
        transport_.last().exit(255);
        scheduler_.advance(*lastStatus().retryAtMs - scheduler_.now);
    }
    transport_.last().out(installedProbe());
    transport_.last().exit(0);
    transport_.last().line(helloLine(scheduler_.now));
    scheduler_.advance(61'000);
    transport_.last().exit(255);
    EXPECT_EQ(*lastStatus().retryAtMs - scheduler_.now, 5'000);
}

TEST_F(RemoteConnection, UserActionableErrorsRetryEveryFiveMinutesOrOnRetry) {
    connect();
    transport_.last().err("Permission denied (publickey).");
    transport_.last().exit(255);
    EXPECT_EQ(lastStatus().retryAtMs, kNow + 300'000);
    connection_->noteWake();
    EXPECT_EQ(transport_.launched.size(), 1u);
    connection_->retryNow();
    EXPECT_EQ(transport_.launched.size(), 2u);
    transport_.last().err("Permission denied (publickey).");
    transport_.last().exit(255);
    scheduler_.advance(300'000);
    EXPECT_EQ(transport_.launched.size(), 3u);
}

TEST_F(RemoteConnection, WakeRetriesAWaitingHostAtOnce) {
    connect();
    transport_.last().err("Network is unreachable");
    transport_.last().exit(255);
    EXPECT_EQ(lastStatus().status.code, StatusCode::Unreachable);
    connection_->noteWake();
    EXPECT_EQ(transport_.launched.size(), 2u);
    EXPECT_TRUE(transport_.last().isProbe());
}

TEST_F(RemoteConnection, StopClosesInputTerminatesAndKillsAfterTwoSeconds) {
    auto& run = helloed();
    connection_->stop(Status::of(StatusCode::Disabled));
    EXPECT_TRUE(run.inputClosed);
    EXPECT_TRUE(run.terminated);
    EXPECT_FALSE(run.killed);
    EXPECT_EQ(lastStatus().status.code, StatusCode::Disabled);
    scheduler_.advance(2'000);
    EXPECT_TRUE(run.killed);
    const auto count = events_.size();
    run.exit(143);
    run.line(R"({"t":"ready"})");
    connection_->retryNow();
    EXPECT_EQ(events_.size(), count);
    EXPECT_EQ(transport_.launched.size(), 2u);
}

TEST_F(RemoteConnection, MoreThan100InvalidLinesIsAProtocolError) {
    auto& run = helloed();
    for (int i = 0; i < 100; ++i) run.line("garbage");
    EXPECT_FALSE(run.terminated);
    run.line(R"({"t":"agent","a":{}})");
    EXPECT_TRUE(run.terminated);
    run.exit(143);
    EXPECT_EQ(lastStatus().status.code, StatusCode::ProtocolError);
    EXPECT_EQ(*lastStatus().retryAtMs - scheduler_.now, 5'000);
}

TEST_F(RemoteConnection, LinesBeforeHelloAreInvalidAndAWrongProtocolIsAMismatch) {
    auto& run = running();
    for (int i = 0; i < 101; ++i) run.line(R"({"t":"ready"})");
    EXPECT_TRUE(run.terminated);
    events_.clear();
    connection_ = nullptr;
    auto& other = running();
    other.line(helloLine(kNow, 2));
    EXPECT_TRUE(other.terminated);
    other.exit(0);
    EXPECT_EQ(lastStatus().status, Status::versionMismatch(2));
    EXPECT_EQ(lastStatus().retryAtMs, scheduler_.now + 300'000);
}

TEST_F(RemoteConnection, TimesThatLeaveTheRangeAfterConversionAreInvalid) {
    auto& run = running();
    run.line(helloLine(kNow + 10'000));
    run.line(responseLine("k", "/a", kEarliestMs + 5'000, 1, true, kEarliestMs + 1'000));
    run.line(responseLine("ok", "/a", kNow + 10'000));
    const auto all = messages();
    ASSERT_EQ(all.size(), 2u);
    EXPECT_EQ(std::get<RemoteResponse>(all[1].message).record.key, "ok");
}

TEST_F(RemoteConnection, AtMost4096AgentsPerHost) {
    auto& run = helloed();
    for (int i = 0; i < 4097; ++i) run.line(agentLine("/a" + std::to_string(i)));
    run.line(agentLine("/a0", "generating"));
    run.line(R"({"t":"agentRemoved","id":"/a1"})");
    run.line(agentLine("/new"));
    std::size_t agents = 0;
    for (const auto& m : messages()) agents += std::holds_alternative<RemoteAgent>(m.message) ? 1 : 0;
    EXPECT_EQ(agents, 4096u + 2u);
    EXPECT_FALSE(run.terminated);
}

TEST_F(RemoteConnection, HistoryScheduleAfterReadyEveryTenMinutesAndAtMidnight) {
    auto& run = helloed();
    EXPECT_EQ(historyDues(), 0);
    run.line(R"({"t":"ready"})");
    EXPECT_EQ(historyDues(), 1);
    keepAlive(run, 600'000);
    EXPECT_EQ(historyDues(), 2);
    const auto midnight = (scheduler_.now / 86'400'000 + 1) * 86'400'000;
    const auto before = historyDues();
    keepAlive(run, midnight - scheduler_.now);
    EXPECT_GE(historyDues(), before + 1);
    EXPECT_NE(run.input().find("{\"t\":\"today\",\"todayStartMs\":" + std::to_string(midnight) + "}"), std::string::npos);
}

TEST_F(RemoteConnection, AFailedHistoryRunLetsTheNextSyncGo) {
    auto& run = helloed();
    run.line(R"({"t":"ready"})");
    connection_->requestHistory({{"2026-05-01/codex", "0123456789abcdef"}});
    EXPECT_NE(run.input().find(R"({"t":"history","digests":{"2026-05-01/codex":"0123456789abcdef"}})"), std::string::npos);
    keepAlive(run, 600'000);
    EXPECT_EQ(historyDues(), 1);
    run.line(R"({"t":"error","code":"historyFailed","detail":"x"})");
    EXPECT_EQ(historyDues(), 2);
    connection_->requestHistory({});
    run.line(R"({"t":"historyDone","days":0,"analyzed":0})");
    EXPECT_EQ(historyDues(), 2);
}

TEST_F(RemoteConnection, HistoryRequestsBeforeReadyAreIgnored) {
    auto& run = helloed();
    connection_->requestHistory({});
    EXPECT_EQ(run.input().find("\"history\""), std::string::npos);
}

TEST_F(RemoteConnection, PricesWatchAndZoneGoToTheRunningRelay) {
    auto& run = helloed();
    connection_->updatePrices(R"({"v":2})");
    connection_->updatePrices(R"({"v":2})");
    connection_->setWatch(true);
    connection_->noteTimeZoneChange();
    const auto input = run.input();
    EXPECT_NE(input.find(R"({"t":"prices","table":{"v":2}})"), std::string::npos);
    EXPECT_EQ(input.find(R"("table":{"v":2})"), input.rfind(R"("table":{"v":2})"));
    EXPECT_NE(input.find(R"({"t":"watch","on":true})"), std::string::npos);
    EXPECT_NE(input.find(R"({"t":"zone","id":"UTC")"), std::string::npos);
    EXPECT_NE(input.find(R"({"t":"today","todayStartMs":)"), std::string::npos);
}

TEST_F(RemoteConnection, ReconnectStartsAFreshConnectionNumber) {
    auto& run = helloed();
    const auto first = events_.back().connection;
    run.err("Connection closed by remote host");
    run.exit(255);
    scheduler_.advance(5'000);
    EXPECT_TRUE(transport_.last().isProbe());
    EXPECT_GT(events_.back().connection, first);
}

}  // namespace
