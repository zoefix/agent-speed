#include <fcntl.h>
#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <chrono>
#include <functional>
#include <future>
#include <thread>

#include "agentspeed/remote_transport.hpp"

namespace agentspeed::remote {

namespace {

constexpr int kMaxWaitMs = 1000;

void wake(int fd) {
    if (fd < 0) return;
    [[maybe_unused]] const auto written = ::write(fd, "x", 1);
}

std::uint64_t currentThread() { return static_cast<std::uint64_t>(std::hash<std::thread::id>{}(std::this_thread::get_id())) | 1u; }

}  // namespace

EventLoop::EventLoop() {
    int fds[2];
    if (::pipe2(fds, O_CLOEXEC | O_NONBLOCK) == 0) {
        wakeRead_ = fds[0];
        wakeWrite_ = fds[1];
    }
}

EventLoop::~EventLoop() {
    if (wakeRead_ >= 0) ::close(wakeRead_);
    if (wakeWrite_ >= 0) ::close(wakeWrite_);
}

bool EventLoop::onLoopThread() const { return threadId_.load() == currentThread(); }

std::int64_t EventLoop::nowMs() const {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}

void EventLoop::stop() {
    stopped_ = true;
    wake(wakeWrite_);
}

void EventLoop::post(std::function<void()> work) {
    {
        std::lock_guard lock(mutex_);
        posted_.push_back(std::move(work));
    }
    wake(wakeWrite_);
}

void EventLoop::sync(const std::function<void()>& work) {
    if (onLoopThread() || stopped_) {
        if (!stopped_) work();
        return;
    }
    auto done = std::make_shared<std::promise<void>>();
    auto future = done->get_future();
    post([work, done] {
        work();
        done->set_value();
    });
    // A loop that stops first never runs it.
    while (future.wait_for(std::chrono::milliseconds(50)) != std::future_status::ready) {
        if (stopped_) return;
    }
}

Scheduler::TimerId EventLoop::schedule(std::int64_t atMs, std::function<void()> work) {
    TimerId id = 0;
    {
        std::lock_guard lock(mutex_);
        id = nextTimer_++;
        timers_[id] = {atMs, std::move(work)};
        deadlines_.emplace(atMs, id);
    }
    if (!onLoopThread()) wake(wakeWrite_);
    return id;
}

void EventLoop::cancel(TimerId id) {
    std::lock_guard lock(mutex_);
    const auto it = timers_.find(id);
    if (it == timers_.end()) return;
    const auto range = deadlines_.equal_range(it->second.first);
    for (auto d = range.first; d != range.second; ++d) {
        if (d->second == id) {
            deadlines_.erase(d);
            break;
        }
    }
    timers_.erase(it);
}

void EventLoop::watch(int fd, short events, std::function<void(short)> handler) { watches_[fd] = {events, std::move(handler)}; }

void EventLoop::unwatch(int fd) { watches_.erase(fd); }

void EventLoop::addTicker(std::function<void()> ticker) { tickers_.push_back(std::move(ticker)); }

void EventLoop::wakeWithin(int milliseconds) { nextWakeMs_ = std::min(nextWakeMs_, std::max(0, milliseconds)); }

void EventLoop::drainPosted() {
    std::vector<std::function<void()>> work;
    {
        std::lock_guard lock(mutex_);
        work.swap(posted_);
    }
    for (auto& w : work) w();
}

int EventLoop::timeoutMs() {
    int wait = std::min(kMaxWaitMs, nextWakeMs_);
    nextWakeMs_ = kMaxWaitMs;
    std::lock_guard lock(mutex_);
    if (!posted_.empty()) return 0;
    if (!deadlines_.empty()) {
        const auto left = deadlines_.begin()->first - nowMs();
        wait = static_cast<int>(std::clamp<std::int64_t>(left, 0, wait));
    }
    return wait;
}

void EventLoop::fireTimers() {
    std::vector<std::function<void()>> due;
    {
        std::lock_guard lock(mutex_);
        const auto now = nowMs();
        while (!deadlines_.empty() && deadlines_.begin()->first <= now) {
            const auto id = deadlines_.begin()->second;
            deadlines_.erase(deadlines_.begin());
            if (const auto it = timers_.find(id); it != timers_.end()) {
                due.push_back(std::move(it->second.second));
                timers_.erase(it);
            }
        }
    }
    for (auto& work : due) work();
}

void EventLoop::run() {
    threadId_ = currentThread();
    // A relay that went away must not end the app: writes fail with EPIPE instead.
    sigset_t pipe;
    sigemptyset(&pipe);
    sigaddset(&pipe, SIGPIPE);
    pthread_sigmask(SIG_BLOCK, &pipe, nullptr);
    std::vector<pollfd> fds;
    while (!stopped_) {
        fds.clear();
        fds.push_back({wakeRead_, POLLIN, 0});
        for (const auto& [fd, w] : watches_) fds.push_back({fd, w.events, 0});
        const int ready = ::poll(fds.data(), fds.size(), timeoutMs());
        if (stopped_) break;
        if (ready > 0) {
            if (fds[0].revents) {
                char buffer[256];
                while (::read(wakeRead_, buffer, sizeof buffer) > 0) {
                }
            }
            for (std::size_t i = 1; i < fds.size(); ++i) {
                if (!fds[i].revents) continue;
                const auto it = watches_.find(fds[i].fd);
                if (it == watches_.end()) continue;
                auto handler = it->second.handler;
                handler(fds[i].revents);
            }
        }
        drainPosted();
        fireTimers();
        for (std::size_t i = 0; i < tickers_.size(); ++i) {
            auto ticker = tickers_[i];
            ticker();
        }
    }
    threadId_ = 0;
}

}  // namespace agentspeed::remote
