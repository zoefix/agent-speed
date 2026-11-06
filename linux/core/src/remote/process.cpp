#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <spawn.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#include <cerrno>
#include <chrono>

#include "agentspeed/remote_transport.hpp"

// REMOTE 4.1: `ssh` started with posix_spawn, an argument vector and its own environment; its pipes never block the
// loop, a closed pipe never raises SIGPIPE in the app, and its exit is reported after both outputs ended.
namespace agentspeed::remote {

namespace {

constexpr std::int64_t kOutputGraceMs = 2000;
constexpr int kReapPollMs = 100;
constexpr std::size_t kReadChunk = 64 * 1024;

std::int64_t steadyMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

void closeFd(int& fd) {
    if (fd >= 0) ::close(fd);
    fd = -1;
}

// The SIGPIPE a failed write left pending on this thread (it is blocked there).
void consumeSigpipe() {
    sigset_t pipe;
    sigemptyset(&pipe);
    sigaddset(&pipe, SIGPIPE);
    timespec zero{0, 0};
    while (::sigtimedwait(&pipe, nullptr, &zero) == SIGPIPE) {
    }
}

std::vector<char*> pointers(const std::vector<std::string>& strings) {
    std::vector<char*> out;
    out.reserve(strings.size() + 1);
    for (const auto& s : strings) out.push_back(const_cast<char*>(s.c_str()));
    out.push_back(nullptr);
    return out;
}

class PipeProcess : public Process, public std::enable_shared_from_this<PipeProcess> {
public:
    PipeProcess(EventLoop& loop, pid_t pid, int in, int out, int err, ProcessHandlers handlers)
        : loop_(loop), pid_(pid), in_(in), out_(out), err_(err), handlers_(std::move(handlers)) {}

    ~PipeProcess() override {
        closeAll();
        if (!reaped_ && pid_ > 0) {
            ::kill(pid_, SIGKILL);
            int status = 0;
            ::waitpid(pid_, &status, 0);
        }
    }

    void begin() {
        std::weak_ptr<PipeProcess> self = shared_from_this();
        loop_.watch(out_, POLLIN, [self](short) {
            if (auto p = self.lock()) p->readFrom(p->out_, true);
        });
        loop_.watch(err_, POLLIN, [self](short) {
            if (auto p = self.lock()) p->readFrom(p->err_, false);
        });
    }

    void write(std::string data, std::function<void(bool)> done) override {
        if (!accepting_ || in_ < 0) {
            if (done) loop_.post([done] { done(false); });
            return;
        }
        queue_.push_back({std::move(data), 0, std::move(done)});
        flush();
    }

    void closeInput() override {
        accepting_ = false;
        if (queue_.empty()) closeStdin();
    }

    void terminate() override { signal(SIGTERM); }
    void kill() override { signal(SIGKILL); }

    // True once the exit was reported.
    bool poll() {
        if (finished_) return true;
        if (!reaped_) {
            int status = 0;
            const auto done = ::waitpid(pid_, &status, WNOHANG);
            if (done == pid_) {
                reaped_ = true;
                status_ = WIFEXITED(status) ? WEXITSTATUS(status) : WIFSIGNALED(status) ? 128 + WTERMSIG(status) : 255;
                reapedAtMs_ = steadyMs();
            } else if (done < 0 && errno == ECHILD) {
                reaped_ = true;
                status_ = 255;
                reapedAtMs_ = steadyMs();
            }
        }
        if (!reaped_) {
            loop_.wakeWithin(kReapPollMs);
            return false;
        }
        // A grandchild that kept a pipe open does not hold the exit back for long.
        if ((out_ >= 0 || err_ >= 0) && steadyMs() - reapedAtMs_ < kOutputGraceMs) {
            loop_.wakeWithin(kReapPollMs);
            return false;
        }
        if (out_ >= 0) readFrom(out_, true);
        if (err_ >= 0) readFrom(err_, false);
        closeAll();
        finished_ = true;
        failQueue();
        auto exit = std::move(handlers_.exit);
        handlers_ = {};
        if (exit) exit(status_);
        return true;
    }

private:
    struct Pending {
        std::string data;
        std::size_t offset = 0;
        std::function<void(bool)> done;
    };

    void signal(int sig) {
        if (!reaped_ && pid_ > 0) ::kill(pid_, sig);
    }

    void readFrom(int& fd, bool isStdout) {
        char buffer[kReadChunk];
        while (fd >= 0) {
            const auto n = ::read(fd, buffer, sizeof buffer);
            if (n > 0) {
                const std::string_view data(buffer, static_cast<std::size_t>(n));
                const auto& handler = isStdout ? handlers_.stdoutData : handlers_.stderrData;
                if (handler) handler(data);
                continue;
            }
            if (n < 0 && errno == EINTR) continue;
            if (n < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) return;
            loop_.unwatch(fd);
            closeFd(fd);
        }
    }

    void flush() {
        while (!queue_.empty() && in_ >= 0) {
            auto& front = queue_.front();
            const auto left = front.data.size() - front.offset;
            if (left == 0) {
                complete(true);
                continue;
            }
            const auto n = ::write(in_, front.data.data() + front.offset, left);
            if (n > 0) {
                front.offset += static_cast<std::size_t>(n);
                continue;
            }
            if (n < 0 && errno == EINTR) continue;
            if (n < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
                std::weak_ptr<PipeProcess> self = shared_from_this();
                loop_.watch(in_, POLLOUT, [self](short) {
                    if (auto p = self.lock()) p->flush();
                });
                return;
            }
            consumeSigpipe();
            failQueue();
            closeStdin();
            return;
        }
        if (in_ >= 0) loop_.unwatch(in_);
        if (queue_.empty() && !accepting_) closeStdin();
    }

    void complete(bool ok) {
        auto done = std::move(queue_.front().done);
        queue_.pop_front();
        if (done) loop_.post([done, ok] { done(ok); });
    }

    void failQueue() {
        accepting_ = false;
        while (!queue_.empty()) complete(false);
    }

    void closeStdin() {
        if (in_ < 0) return;
        loop_.unwatch(in_);
        closeFd(in_);
    }

    void closeAll() {
        closeStdin();
        for (int* fd : {&out_, &err_}) {
            if (*fd >= 0) loop_.unwatch(*fd);
            closeFd(*fd);
        }
    }

    EventLoop& loop_;
    pid_t pid_;
    int in_, out_, err_;
    ProcessHandlers handlers_;
    std::deque<Pending> queue_;
    bool accepting_ = true;
    bool reaped_ = false, finished_ = false;
    int status_ = 0;
    std::int64_t reapedAtMs_ = 0;
};

struct Pipes {
    int in[2] = {-1, -1}, out[2] = {-1, -1}, err[2] = {-1, -1};
    ~Pipes() {
        for (int* fd : {&in[0], &in[1], &out[0], &out[1], &err[0], &err[1]}) closeFd(*fd);
    }
    bool open() { return ::pipe2(in, O_CLOEXEC) == 0 && ::pipe2(out, O_CLOEXEC) == 0 && ::pipe2(err, O_CLOEXEC) == 0; }
};

}  // namespace

// The transport's live processes, polled for their exit after every loop iteration; kept by the loop's ticker.
struct PipeTransport::Reaper {
    std::vector<std::shared_ptr<PipeProcess>> live;
    bool registered = false;
};

PipeTransport::PipeTransport(EventLoop& loop) : loop_(loop), reaper_(std::make_shared<Reaper>()) {}

bool PipeTransport::idle() const { return reaper_->live.empty(); }

bool PipeTransport::available(const std::string& executable) const {
    struct stat info {};
    return ::stat(executable.c_str(), &info) == 0 && S_ISREG(info.st_mode) && ::access(executable.c_str(), X_OK) == 0;
}

std::shared_ptr<Process> PipeTransport::launch(const ProcessRequest& request, ProcessHandlers handlers) {
    Pipes pipes;
    if (!pipes.open()) return nullptr;
    posix_spawn_file_actions_t actions;
    posix_spawn_file_actions_init(&actions);
    posix_spawn_file_actions_adddup2(&actions, pipes.in[0], STDIN_FILENO);
    posix_spawn_file_actions_adddup2(&actions, pipes.out[1], STDOUT_FILENO);
    posix_spawn_file_actions_adddup2(&actions, pipes.err[1], STDERR_FILENO);
#if defined(__GLIBC__) && (__GLIBC__ > 2 || (__GLIBC__ == 2 && __GLIBC_MINOR__ >= 34))
    posix_spawn_file_actions_addclosefrom_np(&actions, 3);
#endif
    posix_spawnattr_t attributes;
    posix_spawnattr_init(&attributes);
    sigset_t none, defaults;
    sigemptyset(&none);
    sigemptyset(&defaults);
    for (const int sig : {SIGPIPE, SIGHUP, SIGINT, SIGQUIT, SIGTERM, SIGTSTP, SIGTTIN, SIGTTOU}) sigaddset(&defaults, sig);
    posix_spawnattr_setsigmask(&attributes, &none);
    posix_spawnattr_setsigdefault(&attributes, &defaults);
    posix_spawnattr_setpgroup(&attributes, 0);
    posix_spawnattr_setflags(&attributes, POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETPGROUP);
    std::vector<std::string> argv{request.executable};
    argv.insert(argv.end(), request.arguments.begin(), request.arguments.end());
    auto argp = pointers(argv);
    auto envp = pointers(request.environment);
    pid_t pid = -1;
    const int failed = ::posix_spawn(&pid, request.executable.c_str(), &actions, &attributes, argp.data(), envp.data());
    posix_spawn_file_actions_destroy(&actions);
    posix_spawnattr_destroy(&attributes);
    if (failed != 0) return nullptr;
    closeFd(pipes.in[0]);
    closeFd(pipes.out[1]);
    closeFd(pipes.err[1]);
    for (const int fd : {pipes.in[1], pipes.out[0], pipes.err[0]}) ::fcntl(fd, F_SETFL, ::fcntl(fd, F_GETFL) | O_NONBLOCK);
    auto process = std::make_shared<PipeProcess>(loop_, pid, pipes.in[1], pipes.out[0], pipes.err[0], std::move(handlers));
    pipes.in[1] = pipes.out[0] = pipes.err[0] = -1;
    process->begin();
    reaper_->live.push_back(process);
    if (!reaper_->registered) {
        reaper_->registered = true;
        loop_.addTicker([reaper = reaper_] {
            auto live = reaper->live;
            for (const auto& p : live) {
                if (p->poll()) std::erase(reaper->live, p);
            }
        });
    }
    loop_.wakeWithin(0);
    return process;
}

}  // namespace agentspeed::remote
