#pragma once

#include <sys/types.h>

#include <atomic>
#include <cstdint>
#include <deque>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

// How the connections run `ssh` (REMOTE 4.1): an argument vector and an environment, never a shell, with the
// process's three pipes read and written without blocking; the wall clock and timers they use. Tests replace the
// transport with a scripted relay and the scheduler with a clock they advance by hand.
namespace agentspeed::remote {

class Scheduler {
public:
    using TimerId = std::uint64_t;
    virtual ~Scheduler() = default;
    // Unix milliseconds of the wall clock.
    virtual std::int64_t nowMs() const = 0;
    // Runs `work` once at `atMs` (wall clock, so a deadline that passed during a suspend fires on wake).
    virtual TimerId schedule(std::int64_t atMs, std::function<void()> work) = 0;
    virtual void cancel(TimerId id) = 0;
};

struct ProcessRequest {
    std::string executable;
    std::vector<std::string> arguments;
    std::vector<std::string> environment;  // NAME=value
};

// Called on the loop's thread. `exit` comes last: after stdout and stderr ended and the process was reaped; its
// value is the exit status, or 128 + the signal number.
struct ProcessHandlers {
    std::function<void(std::string_view)> stdoutData, stderrData;
    std::function<void(int)> exit;
};

class Process {
public:
    virtual ~Process() = default;
    // Queues bytes for stdin, written in order; `done` says whether all of them were written. Ignored after closeInput.
    virtual void write(std::string data, std::function<void(bool)> done = {}) = 0;
    // Closes stdin after the queued writes (end of file for the remote side).
    virtual void closeInput() = 0;
    virtual void terminate() = 0;  // SIGTERM
    virtual void kill() = 0;       // SIGKILL
};

class Transport {
public:
    virtual ~Transport() = default;
    // The executable exists (`sshMissing` otherwise).
    virtual bool available(const std::string& executable) const = 0;
    // nullptr when it could not start.
    virtual std::shared_ptr<Process> launch(const ProcessRequest& request, ProcessHandlers handlers) = 0;
    // No launched process is still running or waiting to report its exit.
    virtual bool idle() const { return true; }
};

// A poll(2) loop on one thread: file descriptors, wall-clock timers and work posted from other threads.
class EventLoop : public Scheduler {
public:
    EventLoop();
    ~EventLoop() override;
    EventLoop(const EventLoop&) = delete;
    EventLoop& operator=(const EventLoop&) = delete;

    // Runs until stop(); the calling thread becomes the loop's thread (SIGPIPE is blocked on it).
    void run();
    // Any thread.
    void stop();
    void post(std::function<void()> work);
    // Runs `work` on the loop's thread and waits for it (any thread but the loop's).
    void sync(const std::function<void()>& work);
    bool onLoopThread() const;

    std::int64_t nowMs() const override;
    TimerId schedule(std::int64_t atMs, std::function<void()> work) override;
    void cancel(TimerId id) override;

    // Loop thread only. `events` for poll(2); the handler hears the returned events.
    void watch(int fd, short events, std::function<void(short)> handler);
    void unwatch(int fd);
    // Called after every iteration (at least once a second).
    void addTicker(std::function<void()> ticker);
    // The next iteration waits at most this long (a process waiting to be reaped).
    void wakeWithin(int milliseconds);

private:
    void drainPosted();
    int timeoutMs();
    void fireTimers();

    int wakeRead_ = -1, wakeWrite_ = -1;
    std::atomic<bool> stopped_{false};
    std::atomic<std::uint64_t> threadId_{0};
    std::mutex mutex_;
    std::vector<std::function<void()>> posted_;
    struct Watch {
        short events;
        std::function<void(short)> handler;
    };
    std::map<int, Watch> watches_;
    std::multimap<std::int64_t, TimerId> deadlines_;
    std::map<TimerId, std::pair<std::int64_t, std::function<void()>>> timers_;
    TimerId nextTimer_ = 1;
    std::vector<std::function<void()>> tickers_;
    int nextWakeMs_ = 1000;
};

// `posix_spawn` with three non-blocking pipes, driven by an EventLoop.
class PipeTransport : public Transport {
public:
    explicit PipeTransport(EventLoop& loop);
    bool available(const std::string& executable) const override;
    // Loop thread only.
    std::shared_ptr<Process> launch(const ProcessRequest& request, ProcessHandlers handlers) override;
    // Loop thread only.
    bool idle() const override;

    struct Reaper;

private:
    EventLoop& loop_;
    std::shared_ptr<Reaper> reaper_;
};

}  // namespace agentspeed::remote
