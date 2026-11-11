#pragma once

#include <condition_variable>
#include <deque>
#include <mutex>
#include <thread>
#include <utility>
#include <vector>

#include "agentspeed/quota/http.hpp"
#include "net/https_client.hpp"

// The vendor service's transport on a host (REMOTE 11.4): requests run one at a time on a worker thread, so the relay's
// loop never waits for the network; their completions run on the owner's thread in `deliver`, as the service expects.
namespace agentspeed::relay {

class RelayTransport final : public quota::http::Transport {
public:
    explicit RelayTransport(net::ClientOptions options);
    // Ends a request in flight at once and drops the queued ones without their completions.
    ~RelayTransport() override;
    RelayTransport(const RelayTransport&) = delete;
    RelayTransport& operator=(const RelayTransport&) = delete;

    void send(quota::http::Request request, quota::http::Completion done) override;
    // Runs the completions of the finished requests (the owner's thread).
    void deliver();
    // Nothing queued, running or waiting for `deliver`.
    bool idle() const;

private:
    using Job = std::pair<quota::http::Request, quota::http::Completion>;
    using Finished = std::pair<quota::http::Completion, quota::http::Response>;
    void run();

    net::ClientOptions options_;
    mutable std::mutex mutex_;
    std::condition_variable changed_;
    std::deque<Job> queue_;
    std::vector<Finished> finished_;
    bool busy_ = false, stopping_ = false;
    int cancel_[2] = {-1, -1};
    std::thread worker_;
};

}  // namespace agentspeed::relay
