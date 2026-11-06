#pragma once

#include <functional>
#include <memory>
#include <optional>
#include <string>

#include "agentspeed/remote_connection.hpp"
#include "agentspeed/remote_ssh.hpp"

// Test Connection (DESIGN 13, Add / Edit sheet): the probe of REMOTE 4.2 step 1 for a host that may not be saved yet.
// Nothing is uploaded, started or written on the host. Loop thread only.
namespace agentspeed::remote {

struct TestOutcome {
    // Supported: `uname -s -m` as printed and the relay it would run; otherwise the status the connection would show.
    bool supported = false;
    std::string system;
    std::optional<Arch> arch;
    Status failure;
    bool operator==(const TestOutcome&) const = default;
};

class ConnectionCheck : public std::enable_shared_from_this<ConnectionCheck> {
public:
    // Longer than ConnectTimeout plus a slow login shell; a host that never answers reads as unreachable.
    static constexpr std::int64_t kTimeLimitMs = 45'000;

    // The result arrives once, through `done`.
    static std::shared_ptr<ConnectionCheck> start(const Host& host, ConnectionEnvironment environment, std::function<void(TestOutcome)> done);
    // Ends the probe; its result is never delivered.
    void cancel();
    // How a finished probe reads: its exit and stderr as a connection classifies them, else the probe's verdict.
    static TestOutcome outcome(int exitStatus, std::string_view stdoutText, std::string_view stderrText);

private:
    ConnectionCheck(ConnectionEnvironment environment, std::function<void(TestOutcome)> done)
        : environment_(std::move(environment)), done_(std::move(done)) {}
    void launch(const Host& host);
    void exited(int status);
    void end();
    void finish(const TestOutcome& outcome);

    ConnectionEnvironment environment_;
    std::function<void(TestOutcome)> done_;
    std::shared_ptr<Process> process_;
    std::optional<Scheduler::TimerId> timer_;
    std::string stdout_, stderr_;
    bool timedOut_ = false;
};

}  // namespace agentspeed::remote
