#include "quota_transport.hpp"

#include <fcntl.h>
#include <unistd.h>

#include "agentspeed/quota/routes/fingerprint.hpp"

namespace agentspeed::relay {

RelayTransport::RelayTransport(net::ClientOptions options) : options_(std::move(options)) {
    if (::pipe2(cancel_, O_CLOEXEC) != 0) cancel_[0] = cancel_[1] = -1;
    worker_ = std::thread([this] { run(); });
}

RelayTransport::~RelayTransport() {
    {
        std::lock_guard lock(mutex_);
        stopping_ = true;
        for (auto& [request, done] : queue_) {
            for (auto& header : request.headers) quota::wipe(header.value);
        }
        queue_.clear();
    }
    if (cancel_[1] >= 0) {
        const char byte = 0;
        [[maybe_unused]] const auto written = ::write(cancel_[1], &byte, 1);
    }
    changed_.notify_all();
    if (worker_.joinable()) worker_.join();
    for (const int fd : cancel_) {
        if (fd >= 0) ::close(fd);
    }
}

void RelayTransport::send(quota::http::Request request, quota::http::Completion done) {
    {
        std::lock_guard lock(mutex_);
        if (stopping_) return;
        queue_.emplace_back(std::move(request), std::move(done));
    }
    changed_.notify_all();
}

void RelayTransport::run() {
    while (true) {
        Job job;
        {
            std::unique_lock lock(mutex_);
            changed_.wait(lock, [this] { return stopping_ || !queue_.empty(); });
            if (stopping_) return;
            job = std::move(queue_.front());
            queue_.pop_front();
            busy_ = true;
        }
        auto response = net::fetch(job.first, options_, cancel_[0]);
        // The credential lived in the request's headers for this request only (QUOTAS 9.1).
        for (auto& header : job.first.headers) quota::wipe(header.value);
        std::lock_guard lock(mutex_);
        busy_ = false;
        if (stopping_) return;
        finished_.emplace_back(std::move(job.second), std::move(response));
    }
}

void RelayTransport::deliver() {
    std::vector<Finished> finished;
    {
        std::lock_guard lock(mutex_);
        finished.swap(finished_);
    }
    for (auto& [done, response] : finished) done(std::move(response));
}

bool RelayTransport::idle() const {
    std::lock_guard lock(mutex_);
    return queue_.empty() && finished_.empty() && !busy_;
}

}  // namespace agentspeed::relay
