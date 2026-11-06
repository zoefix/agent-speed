#include <gtest/gtest.h>
#include <sys/utsname.h>
#include <unistd.h>

#include <chrono>
#include <condition_variable>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <future>
#include <thread>

#include "agentspeed/remote_coordinator.hpp"
#include "agentspeed/remote_transport.hpp"

// The posix_spawn transport for real, and the whole connection over a stand-in for ssh that runs the remote command
// with /bin/sh, against the real relay executable (REMOTE 10, end to end without a network).
namespace {

namespace fs = std::filesystem;
using namespace agentspeed;
using namespace agentspeed::remote;
using namespace std::chrono_literals;

// A loop on its own thread, as the coordinator runs it.
class LoopThread {
public:
    LoopThread() : thread_([this] { loop.run(); }) {}
    ~LoopThread() {
        loop.stop();
        thread_.join();
    }
    EventLoop loop;

private:
    std::thread thread_;
};

struct Collected {
    std::mutex mutex;
    std::condition_variable changed;
    std::string out, err;
    std::optional<int> status;
    bool outBeforeExit = false;

    ProcessHandlers handlers() {
        ProcessHandlers h;
        h.stdoutData = [this](std::string_view d) {
            std::lock_guard lock(mutex);
            out.append(d);
        };
        h.stderrData = [this](std::string_view d) {
            std::lock_guard lock(mutex);
            err.append(d);
        };
        h.exit = [this](int s) {
            std::lock_guard lock(mutex);
            status = s;
            outBeforeExit = !out.empty();
            changed.notify_all();
        };
        return h;
    }
    bool wait(std::chrono::milliseconds timeout = 10s) {
        std::unique_lock lock(mutex);
        return changed.wait_for(lock, timeout, [this] { return status.has_value(); });
    }
};

std::shared_ptr<Process> launch(LoopThread& t, PipeTransport& transport, ProcessRequest request, Collected& c) {
    std::shared_ptr<Process> process;
    t.loop.sync([&] { process = transport.launch(request, c.handlers()); });
    return process;
}

TEST(RemoteProcess, DeliversOutputBeforeExitAndSurvivesAClosedPipe) {
    LoopThread t;
    PipeTransport transport(t.loop);
    EXPECT_TRUE(transport.available("/bin/sh"));
    EXPECT_FALSE(transport.available("/nonexistent/ssh"));
    Collected c;
    launch(t, transport, {"/bin/sh", {"-c", "echo out; echo err >&2; exit 3"}, {"PATH=/usr/bin:/bin"}}, c);
    ASSERT_TRUE(c.wait());
    EXPECT_EQ(*c.status, 3);
    EXPECT_EQ(c.out, "out\n");
    EXPECT_EQ(c.err, "err\n");
    EXPECT_TRUE(c.outBeforeExit);

    Collected gone;
    auto process = launch(t, transport, {"/bin/true", {}, {}}, gone);
    ASSERT_TRUE(gone.wait());
    std::promise<bool> written;
    t.loop.sync([&] { process->write(std::string(1 << 20, 'x'), [&](bool ok) { written.set_value(ok); }); });
    EXPECT_FALSE(written.get_future().get());
}

TEST(RemoteProcess, StdinRoundTripsAndClosesAfterTheQueuedWrites) {
    LoopThread t;
    PipeTransport transport(t.loop);
    Collected c;
    auto process = launch(t, transport, {"/bin/cat", {}, {}}, c);
    const std::string big(3 << 20, 'b');
    t.loop.sync([&] {
        process->write("hello\n");
        process->write(big);
        process->closeInput();
        process->write("dropped");
    });
    ASSERT_TRUE(c.wait(20s));
    EXPECT_EQ(*c.status, 0);
    EXPECT_EQ(c.out.size(), 6 + big.size());
    EXPECT_EQ(c.out.substr(0, 6), "hello\n");
}

TEST(RemoteProcess, ArgumentsAndEnvironmentArePassedExactly) {
    LoopThread t;
    PipeTransport transport(t.loop);
    Collected c;
    launch(t, transport, {"/bin/sh", {"-c", "printf '%s|' \"$@\"; echo \"$LC_ALL\"", "sh", "a b", "$(id)", ";"}, {"LC_ALL=C"}}, c);
    ASSERT_TRUE(c.wait());
    EXPECT_EQ(c.out, "a b|$(id)|;|C\n");
    Collected missing;
    std::shared_ptr<Process> none;
    t.loop.sync([&] { none = transport.launch({"/nonexistent/ssh", {}, {}}, missing.handlers()); });
    EXPECT_EQ(none, nullptr);
}

TEST(RemoteProcess, TerminateAndKillEndALongRunningProcess) {
    LoopThread t;
    PipeTransport transport(t.loop);
    Collected c;
    auto process = launch(t, transport, {"/bin/sleep", {"30"}, {}}, c);
    t.loop.sync([&] { process->terminate(); });
    ASSERT_TRUE(c.wait());
    EXPECT_EQ(*c.status, 128 + SIGTERM);
    Collected stubborn;
    auto ignoring = launch(t, transport, {"/bin/sh", {"-c", "trap '' TERM; sleep 30"}, {}}, stubborn);
    std::this_thread::sleep_for(200ms);
    t.loop.sync([&] {
        ignoring->terminate();
        ignoring->kill();
    });
    ASSERT_TRUE(stubborn.wait());
    EXPECT_EQ(*stubborn.status, 128 + SIGKILL);
}

#ifdef AGENTSPEED_RELAY_BINARY

std::string iso(std::int64_t ms) {
    const auto seconds = static_cast<std::time_t>(ms / 1000);
    std::tm tm{};
    ::gmtime_r(&seconds, &tm);
    char buffer[64];
    std::snprintf(buffer, sizeof buffer, "%04d-%02d-%02dT%02d:%02d:%02d.%03dZ", tm.tm_year + 1900, tm.tm_mon + 1, tm.tm_mday, tm.tm_hour, tm.tm_min,
                  tm.tm_sec, static_cast<int>(ms % 1000));
    return buffer;
}

// One Codex session with one response that ended `agoMs` before now.
std::string codexSession(std::int64_t nowMs, std::int64_t agoMs, const std::string& responseId, int output) {
    const auto t = [&](std::int64_t offset) { return iso(nowMs - agoMs + offset); };
    return R"({"timestamp":")" + t(-5000) +
           R"(","type":"session_meta","payload":{"id":"thread-e2e","session_id":"sess-e2e","timestamp":")" + t(-5000) +
           R"(","cwd":"/home/dev/rocket","originator":"codex_cli_rs","model_provider":"openai","cli_version":"0.163.0","source":"cli"}})" "\n" +
           R"({"timestamp":")" + t(-4900) + R"(","type":"turn_context","payload":{"cwd":"/home/dev/rocket","model":"gpt-5.1","effort":"high"}})" "\n" +
           R"({"timestamp":")" + t(-4000) + R"(","type":"event_msg","payload":{"type":"task_started","turn_id":"turn-1"}})" "\n" +
           R"({"timestamp":")" + t(0) + R"(","type":"token_usage_record","payload":{"thread_id":"thread-e2e","session_id":"sess-e2e","response_id":")" +
           responseId + R"(","usage":{"input_tokens":1000,"cached_input_tokens":0,"output_tokens":)" + std::to_string(output) +
           R"(,"reasoning_output_tokens":0,"total_tokens":)" + std::to_string(1000 + output) + R"(,"cache_write_input_tokens":0}}})" "\n";
}

class RemoteEndToEnd : public ::testing::Test {
protected:
    void SetUp() override {
        root_ = fs::temp_directory_path() / ("as-e2e-" + std::to_string(::getpid()));
        fs::remove_all(root_);
        fs::create_directories(root_ / "home/.codex/sessions/2026/01/01");
        fs::create_directories(root_ / "relays");
        utsname name{};
        ::uname(&name);
        arch_ = *archOfMachine(name.machine);
        fs::copy_file(AGENTSPEED_RELAY_BINARY, root_ / "relays" / relay_name::bundled(arch_));
        // The stand-in for ssh: everything after `--` is the destination and the remote command, run by /bin/sh.
        std::ofstream(root_ / "ssh") << "#!/bin/sh\nwhile [ \"$1\" != \"--\" ]; do shift; done\nshift 2\nexec /bin/sh -c \"$1\"\n";
        fs::permissions(root_ / "ssh", fs::perms::owner_all);
        now_ = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
        session_ = root_ / "home/.codex/sessions/2026/01/01/rollout-e2e.jsonl";
        std::ofstream(session_) << codexSession(now_, 20'000, "resp-1", 300);
    }
    void TearDown() override { fs::remove_all(root_); }

    // ssh as the stand-in: the transport runs the request with another executable.
    class StandIn : public Transport {
    public:
        StandIn(EventLoop& loop, std::string ssh) : real_(loop), ssh_(std::move(ssh)) {}
        bool available(const std::string&) const override { return true; }
        std::shared_ptr<Process> launch(const ProcessRequest& request, ProcessHandlers handlers) override {
            auto copy = request;
            copy.executable = ssh_;
            return real_.launch(copy, std::move(handlers));
        }

    private:
        PipeTransport real_;
        std::string ssh_;
    };

    std::unique_ptr<Coordinator> coordinator(std::shared_ptr<Feed> feed) {
        Coordinator::Options options;
        options.feed = std::move(feed);
        options.relays = std::make_shared<RelayBundle>((root_ / "relays").string(), "9.9.9");
        options.zone = [] { return Zone{"UTC", utcTzif()}; };
        options.processEnvironment = {"HOME=" + (root_ / "home").string(), "PATH=/usr/bin:/bin"};
        const auto ssh = (root_ / "ssh").string();
        options.transport = [ssh](EventLoop& loop) { return std::make_unique<StandIn>(loop, ssh); };
        return std::make_unique<Coordinator>(std::move(options));
    }

    template <typename Predicate>
    static bool eventually(Predicate predicate, std::chrono::seconds timeout = 60s) {
        const auto deadline = std::chrono::steady_clock::now() + timeout;
        while (std::chrono::steady_clock::now() < deadline) {
            if (predicate()) return true;
            std::this_thread::sleep_for(50ms);
        }
        return predicate();
    }

    fs::path root_, session_;
    Arch arch_ = Arch::X86_64;
    std::int64_t now_ = 0;
};

TEST_F(RemoteEndToEnd, InstallsRunsSyncsHistoryAndStopsTheRelay) {
    const auto host = Host::make("8C0D6B0A-1111-4222-8333-444455556666", "dev@devbox", std::nullopt, true, std::string("~/.cache/agent-speed"));
    auto feed = std::make_shared<Feed>();
    auto hub = coordinator(feed);
    std::mutex mutex;
    std::vector<HostEvent> history;
    hub->setHistorySink([&](const HostEvent& e) {
        std::lock_guard lock(mutex);
        history.push_back(e);
        if (std::holds_alternative<HostEvent::HistoryDue>(e.kind)) hub->requestHistory(e.hostId, {});
    });
    hub->update(HostsDocument({host}), true, {SourceKind::Codex}, "");
    std::vector<FeedChange> changes;
    const auto connected = [&] {
        auto taken = feed->take();
        changes.insert(changes.end(), taken.begin(), taken.end());
        const auto statuses = hub->statuses();
        return statuses.contains(host.id) && statuses.at(host.id).status.code == StatusCode::Connected;
    };
    ASSERT_TRUE(eventually(connected)) << hub->statuses()[host.id].status.name();
    EXPECT_TRUE(fs::exists(root_ / "home/.cache/agent-speed" / relay_name::installed("9.9.9", arch_, *fileSha256(AGENTSPEED_RELAY_BINARY))));
    const auto status = hub->statuses().at(host.id);
    EXPECT_EQ(status.relayArch, std::string(archName(arch_)));
    ASSERT_TRUE(status.clockOffsetMs);
    EXPECT_LT(std::abs(*status.clockOffsetMs), 1000);

    bool sawAgent = false, sawSeeded = false;
    for (const auto& change : changes) {
        if (const auto* agent = std::get_if<feed::Agent>(&change)) sawAgent = agent->agent.id == agentPrefix(host.id) + session_.string();
        if (const auto* response = std::get_if<feed::Response>(&change)) sawSeeded = !response->live && response->response.record.tokens.output == 300;
    }
    EXPECT_TRUE(sawAgent);
    EXPECT_TRUE(sawSeeded);

    // A response appended on the host arrives live.
    std::ofstream(session_, std::ios::app) << codexSession(now_, 0, "resp-2", 45).substr(codexSession(now_, 0, "resp-2", 45).rfind("{\"timestamp\""));
    bool live = false;
    ASSERT_TRUE(eventually([&] {
        for (const auto& change : feed->take()) {
            if (const auto* response = std::get_if<feed::Response>(&change)) live = live || (response->live && response->response.record.tokens.output == 45);
        }
        return live;
    }));

    ASSERT_TRUE(eventually([&] {
        std::lock_guard lock(mutex);
        return std::any_of(history.begin(), history.end(), [](const HostEvent& e) {
            const auto* m = std::get_if<HostEvent::Message>(&e.kind);
            return m && std::holds_alternative<HistoryDone>(m->message);
        });
    }));
    {
        std::lock_guard lock(mutex);
        const auto day = std::find_if(history.begin(), history.end(), [](const HostEvent& e) {
            const auto* m = std::get_if<HostEvent::Message>(&e.kind);
            return m && std::holds_alternative<HistoryDay>(m->message);
        });
        ASSERT_NE(day, history.end());
        EXPECT_EQ(std::get<HistoryDay>(std::get<HostEvent::Message>(day->kind).message).source, SourceKind::Codex);
    }
    hub->stop();
    EXPECT_FALSE(hub->statuses().empty());
}

TEST_F(RemoteEndToEnd, AnInstalledRelayIsNotUploadedAgainAndTheTestProbeReadsTheSystem) {
    const auto host = Host::make("9D1E7C1B-2222-4333-9444-555566667777", "devbox");
    for (int round = 0; round < 2; ++round) {
        auto feed = std::make_shared<Feed>();
        auto hub = coordinator(feed);
        std::vector<StatusCode> seen;
        hub->update(HostsDocument({host}), true, {SourceKind::Codex}, "");
        ASSERT_TRUE(eventually([&] {
            for (const auto& change : feed->take()) {
                if (const auto* s = std::get_if<feed::Status>(&change)) seen.push_back(s->status.status.code);
            }
            return std::find(seen.begin(), seen.end(), StatusCode::Connected) != seen.end();
        }));
        const bool installed = std::find(seen.begin(), seen.end(), StatusCode::Installing) != seen.end();
        EXPECT_EQ(installed, round == 0);
        std::promise<TestOutcome> tested;
        hub->testConnection(host, [&](TestOutcome outcome) { tested.set_value(outcome); });
        auto future = tested.get_future();
        ASSERT_EQ(future.wait_for(20s), std::future_status::ready);
        const auto outcome = future.get();
        EXPECT_TRUE(outcome.supported);
        EXPECT_EQ(outcome.arch, arch_);
        EXPECT_TRUE(outcome.system.starts_with("Linux "));
        hub->stop();
    }
}

#endif

}  // namespace
