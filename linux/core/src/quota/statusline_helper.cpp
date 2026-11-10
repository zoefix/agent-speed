#include "agentspeed/quota/statusline_helper.hpp"

#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <spawn.h>
#include <sys/file.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <vector>

#include "agentspeed/quota/cli_quota.hpp"
#include "agentspeed/quota/files.hpp"
#include "agentspeed/quota/paths.hpp"

extern char** environ;

namespace agentspeed::quota::helper {

namespace {

constexpr int kLockAttempts = 25;
constexpr int kLockWaitMs = 10;
// Without a pidfd the exit is noticed by polling this often.
constexpr int kExitPollMs = 10;

std::int64_t clockMs(clockid_t clock) {
    timespec now{};
    ::clock_gettime(clock, &now);
    return static_cast<std::int64_t>(now.tv_sec) * 1000 + now.tv_nsec / 1'000'000;
}

void writeAll(int fd, std::string_view bytes) {
    std::size_t done = 0;
    while (done < bytes.size()) {
        const auto n = ::write(fd, bytes.data() + done, bytes.size() - done);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) return;
        done += static_cast<std::size_t>(n);
    }
}

void sleepMs(int ms) {
    timespec pause{0, static_cast<long>(ms) * 1'000'000};
    while (::nanosleep(&pause, &pause) != 0 && errno == EINTR) {
    }
}

// An exclusive advisory lock on the quota folder itself, so two sessions' helpers never lose each other's values. After
// ~250 ms without it the merge goes ahead anyway: the status line must not stall.
class FolderLock {
public:
    explicit FolderLock(const std::string& folder) : fd_(::open(folder.c_str(), O_RDONLY | O_DIRECTORY | O_CLOEXEC)) {
        if (fd_ < 0) return;
        for (int attempt = 0; attempt < kLockAttempts; ++attempt) {
            if (::flock(fd_, LOCK_EX | LOCK_NB) == 0) return;
            if (errno != EWOULDBLOCK && errno != EINTR) return;
            sleepMs(kLockWaitMs);
        }
    }
    ~FolderLock() {
        if (fd_ >= 0) ::close(fd_);
    }
    FolderLock(const FolderLock&) = delete;
    FolderLock& operator=(const FolderLock&) = delete;

private:
    int fd_;
};

// A descriptor of 3 or more, so dup2 onto standard input always clears close-on-exec.
int highDescriptor(int fd) {
    if (fd >= 3) return fd;
    const int moved = ::fcntl(fd, F_DUPFD_CLOEXEC, 3);
    ::close(fd);
    return moved;
}

int openPidFd(pid_t pid) {
#ifdef SYS_pidfd_open
    const auto fd = ::syscall(SYS_pidfd_open, pid, 0);
    return fd >= 0 ? static_cast<int>(fd) : -1;
#else
    (void)pid;
    return -1;
#endif
}

int exitCodeOf(int status) { return WIFEXITED(status) ? WEXITSTATUS(status) : 0; }

int spawnAndWait(const std::string& command, std::string_view input, int timeoutMs, const std::string& shell);

}  // namespace

std::string quotaFolderBeside(std::string_view executable) {
    const auto data = files::parentOf(files::parentOf(executable));
    return quotaFolderOf(data.empty() ? std::string(".") : data);
}

std::optional<std::string> ownExecutable() { return files::resolvedPath("/proc/self/exe"); }

std::string readInput(int fd, std::size_t limit) {
    std::string out;
    char chunk[64 * 1024];
    while (out.size() < limit) {
        const auto n = ::read(fd, chunk, std::min(sizeof chunk, limit - out.size()));
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) break;
        out.append(chunk, static_cast<std::size_t>(n));
    }
    return out;
}

bool store(std::string_view input, const std::string& quotaFolder, std::int64_t nowMs) {
    if (input.size() > cli::kInputLimit) return false;
    json::Document doc;
    if (!doc.parse(input) || !doc.root().isObject() || !doc.root()["rate_limits"].isObject()) return false;
    if (!files::makeFolders(quotaFolder, 0700)) return false;
    const FolderLock lock(quotaFolder);
    const auto file = quotaFolder + "/" + std::string(kQuotaFileName);
    const auto current = files::readFile(file, cli::kInputLimit, false);
    auto stored = current.status == files::ReadStatus::Ok ? cli::parseFile(current.bytes) : cli::Stored{};
    if (!cli::merge(stored, doc.root()["rate_limits"], nowMs)) return false;
    return files::replaceFile(file, cli::fileText(stored), 0600);
}

int runPrevious(const std::string& command, std::string_view input, int timeoutMs, const std::string& shell) {
    // A command that stops reading its input must not end this process; the child gets the default back.
    struct sigaction ignore {};
    struct sigaction previous {};
    ignore.sa_handler = SIG_IGN;
    sigemptyset(&ignore.sa_mask);
    ::sigaction(SIGPIPE, &ignore, &previous);
    const int code = spawnAndWait(command, input, timeoutMs, shell);
    ::sigaction(SIGPIPE, &previous, nullptr);
    return code;
}

namespace {

int spawnAndWait(const std::string& command, std::string_view input, int timeoutMs, const std::string& shell) {
    int ends[2] = {-1, -1};
    if (::pipe2(ends, O_CLOEXEC) != 0) return 0;
    const int reading = highDescriptor(ends[0]);
    const int writing = highDescriptor(ends[1]);
    if (reading < 0 || writing < 0) {
        if (reading >= 0) ::close(reading);
        if (writing >= 0) ::close(writing);
        return 0;
    }

    posix_spawn_file_actions_t actions;
    posix_spawnattr_t attributes;
    ::posix_spawn_file_actions_init(&actions);
    ::posix_spawnattr_init(&attributes);
    ::posix_spawn_file_actions_adddup2(&actions, reading, 0);
    sigset_t none;
    sigset_t defaults;
    sigemptyset(&none);
    sigemptyset(&defaults);
    sigaddset(&defaults, SIGPIPE);
    ::posix_spawnattr_setsigmask(&attributes, &none);
    ::posix_spawnattr_setsigdefault(&attributes, &defaults);
    ::posix_spawnattr_setpgroup(&attributes, 0);
    ::posix_spawnattr_setflags(&attributes, static_cast<short>(POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSIGDEF));
    std::string dashC = "-c";
    std::string shellCopy = shell;
    std::string commandCopy = command;
    char* argv[] = {shellCopy.data(), dashC.data(), commandCopy.data(), nullptr};
    pid_t pid = 0;
    const int spawned = ::posix_spawn(&pid, shell.c_str(), &actions, &attributes, argv, environ);
    ::posix_spawn_file_actions_destroy(&actions);
    ::posix_spawnattr_destroy(&attributes);
    ::close(reading);
    if (spawned != 0) {
        ::close(writing);
        return 0;
    }

    int feed = writing;
    ::fcntl(feed, F_SETFL, ::fcntl(feed, F_GETFL) | O_NONBLOCK);
    std::size_t fed = 0;
    if (input.empty()) {
        ::close(feed);
        feed = -1;
    }
    const int exitFd = openPidFd(pid);
    const auto deadline = clockMs(CLOCK_MONOTONIC) + timeoutMs;
    int status = 0;
    int code = 0;
    bool exited = false;
    while (true) {
        const auto waited = ::waitpid(pid, &status, WNOHANG);
        if (waited == pid || (waited < 0 && errno != EINTR)) {
            exited = waited == pid;
            break;
        }
        const auto remaining = deadline - clockMs(CLOCK_MONOTONIC);
        if (remaining <= 0) break;
        pollfd watch[2];
        nfds_t count = 0;
        if (feed >= 0) watch[count++] = {feed, POLLOUT, 0};
        if (exitFd >= 0) watch[count++] = {exitFd, POLLIN, 0};
        const int wait = static_cast<int>(exitFd >= 0 ? remaining : std::min<std::int64_t>(remaining, kExitPollMs));
        if (::poll(watch, count, wait) < 0 && errno != EINTR) break;
        if (feed >= 0 && (watch[0].revents & (POLLOUT | POLLERR | POLLHUP)) != 0) {
            const auto n = ::write(feed, input.data() + fed, input.size() - fed);
            if (n > 0) fed += static_cast<std::size_t>(n);
            if ((n < 0 && errno != EAGAIN && errno != EINTR) || fed >= input.size()) {
                ::close(feed);
                feed = -1;
            }
        }
    }
    if (feed >= 0) ::close(feed);
    if (exited) {
        code = exitCodeOf(status);
    } else {
        ::kill(-pid, SIGKILL);
        ::kill(pid, SIGKILL);
        while (::waitpid(pid, &status, 0) < 0 && errno == EINTR) {
        }
    }
    if (exitFd >= 0) ::close(exitFd);
    return code;
}

}  // namespace

int run(std::string_view input, const Options& options, int outFd) {
    try {
        store(input, options.quotaFolder, options.nowMs);
    } catch (...) {
        // The quota is lost for this response only; the status line still shows.
    }
    const auto sidecar = files::readFile(options.quotaFolder + "/" + std::string(kSidecarFileName), cli::kInputLimit, false);
    if (sidecar.status == files::ReadStatus::Ok) {
        if (auto command = cli::previousCommand(sidecar.bytes)) {
            return runPrevious(*command, input, options.commandTimeoutMs, options.shell);
        }
    }
    const auto file = files::readFile(options.quotaFolder + "/" + std::string(kQuotaFileName), cli::kInputLimit, false);
    if (file.status != files::ReadStatus::Ok) return 0;
    const auto text = cli::line(cli::parseFile(file.bytes), options.nowMs);
    if (!text.empty()) writeAll(outFd, text + "\n");
    return 0;
}

int main(int argc, char** argv) {
    try {
        ::signal(SIGPIPE, SIG_IGN);
        // An ignored SIGCHLD inherited from the caller would reap the previous command before its exit code is read.
        ::signal(SIGCHLD, SIG_DFL);
        Options options;
        for (int i = 1; i + 1 < argc; ++i) {
            if (std::string_view(argv[i]) == "--folder") options.quotaFolder = quotaFolderOf(argv[i + 1]);
        }
        if (options.quotaFolder.empty()) {
            const auto executable = ownExecutable();
            if (!executable) return 0;
            options.quotaFolder = quotaFolderBeside(*executable);
        }
        const auto input = readInput(0);
        options.nowMs = clockMs(CLOCK_REALTIME);
        return run(input, options, 1);
    } catch (...) {
        return 0;
    }
}

}  // namespace agentspeed::quota::helper
