#include "agentspeed/quota/routes/child_process.hpp"

#include <dirent.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <spawn.h>
#include <sys/wait.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <chrono>
#include <cstdlib>

namespace agentspeed::quota::child {

namespace {

constexpr int kTickMs = 50;
// Once the program ended and its group was killed, how long what is left in the pipe may still be read.
constexpr std::int64_t kDrainMs = 1'000;

std::int64_t steadyMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

std::int64_t wallMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}

std::vector<char*> pointers(std::vector<std::string>& items) {
    std::vector<char*> out;
    out.reserve(items.size() + 1);
    for (auto& item : items) out.push_back(item.data());
    out.push_back(nullptr);
    return out;
}

#if !(defined(__GLIBC__) && (__GLIBC__ > 2 || (__GLIBC__ == 2 && __GLIBC_MINOR__ >= 34)))
// Without closefrom: the descriptors above standard error this process holds now (one closed meanwhile only fails its
// own close, which the child ignores).
std::vector<int> openDescriptors() {
    std::vector<int> out;
    DIR* folder = ::opendir("/proc/self/fd");
    if (!folder) return out;
    const int own = ::dirfd(folder);
    while (const dirent* entry = ::readdir(folder)) {
        char* end = nullptr;
        const long fd = std::strtol(entry->d_name, &end, 10);
        if (end == entry->d_name || *end != '\0' || fd <= STDERR_FILENO || fd == own) continue;
        out.push_back(static_cast<int>(fd));
    }
    ::closedir(folder);
    return out;
}
#endif

// The program ended. It is left a zombie, so its pid, which is its group's id, cannot be reused before the group is
// killed.
bool ended(pid_t pid) {
    siginfo_t info{};
    if (::waitid(P_PID, static_cast<id_t>(pid), &info, WEXITED | WNOHANG | WNOWAIT) != 0) return errno != EINTR;
    return info.si_pid == pid;
}

}  // namespace

Result run(const Command& command, const std::function<bool()>& stop) {
    Result result;
    int out[2] = {-1, -1};
    if (::pipe2(out, O_CLOEXEC) != 0) return result;
    posix_spawn_file_actions_t actions;
    ::posix_spawn_file_actions_init(&actions);
    ::posix_spawn_file_actions_addopen(&actions, STDIN_FILENO, "/dev/null", O_RDONLY, 0);
    ::posix_spawn_file_actions_adddup2(&actions, out[1], STDOUT_FILENO);
    ::posix_spawn_file_actions_addopen(&actions, STDERR_FILENO, "/dev/null", O_WRONLY, 0);
    ::posix_spawn_file_actions_addchdir_np(&actions, command.folder.c_str());
#if defined(__GLIBC__) && (__GLIBC__ > 2 || (__GLIBC__ == 2 && __GLIBC_MINOR__ >= 34))
    ::posix_spawn_file_actions_addclosefrom_np(&actions, STDERR_FILENO + 1);
#else
    for (const int fd : openDescriptors()) ::posix_spawn_file_actions_addclose(&actions, fd);
#endif
    posix_spawnattr_t attributes;
    ::posix_spawnattr_init(&attributes);
    sigset_t none;
    sigset_t defaults;
    sigemptyset(&none);
    sigemptyset(&defaults);
    for (const int sig : {SIGPIPE, SIGHUP, SIGINT, SIGQUIT, SIGTERM, SIGTSTP, SIGTTIN, SIGTTOU, SIGCHLD}) sigaddset(&defaults, sig);
    ::posix_spawnattr_setsigmask(&attributes, &none);
    ::posix_spawnattr_setsigdefault(&attributes, &defaults);
    ::posix_spawnattr_setpgroup(&attributes, 0);
    ::posix_spawnattr_setflags(&attributes, static_cast<short>(POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSIGDEF));
    std::vector<std::string> argv{command.executable};
    argv.insert(argv.end(), command.arguments.begin(), command.arguments.end());
    auto environment = command.environment;
    auto argp = pointers(argv);
    auto envp = pointers(environment);
    pid_t pid = -1;
    const int failed = ::posix_spawn(&pid, command.executable.c_str(), &actions, &attributes, argp.data(), envp.data());
    ::posix_spawn_file_actions_destroy(&actions);
    ::posix_spawnattr_destroy(&attributes);
    ::close(out[1]);
    const int reading = out[0];
    if (failed != 0) {
        ::close(reading);
        return result;
    }
    ::fcntl(reading, F_SETFL, ::fcntl(reading, F_GETFL) | O_NONBLOCK);

    const auto deadline = steadyMs() + command.timeoutMs;
    std::int64_t drainUntil = 0;
    bool open = true;
    bool exited = false;
    char buffer[16384];
    while (result.end == End::NotStarted) {
        if (!exited && ended(pid)) {
            exited = true;
            result.endedMs = wallMs();
            ::kill(-pid, SIGKILL);
            drainUntil = std::min(deadline, steadyMs() + kDrainMs);
        }
        if (exited && !open) break;
        const auto now = steadyMs();
        const auto limit = exited ? drainUntil : deadline;
        if (now >= limit) {
            if (!exited) result.end = End::TimedOut;
            break;
        }
        if (!exited && stop && stop()) {
            result.end = End::Stopped;
            break;
        }
        pollfd watch{reading, POLLIN, 0};
        const auto wait = static_cast<int>(std::clamp<std::int64_t>(limit - now, 0, kTickMs));
        ::poll(open ? &watch : nullptr, open ? 1 : 0, wait);
        while (open) {
            const auto n = ::read(reading, buffer, sizeof buffer);
            if (n > 0) {
                const auto room = command.outputCap + 1 - result.output.size();
                result.output.append(buffer, std::min(static_cast<std::size_t>(n), room));
                if (result.output.size() > command.outputCap) {
                    result.end = End::TooLarge;
                    break;
                }
            } else if (n == 0 || (errno != EINTR && errno != EAGAIN && errno != EWOULDBLOCK)) {
                open = false;
            } else if (errno != EINTR) {
                break;
            }
        }
    }
    if (result.endedMs == 0) result.endedMs = wallMs();
    ::kill(-pid, SIGKILL);
    if (!exited) ::kill(pid, SIGKILL);
    int status = 0;
    pid_t reaped = -1;
    while ((reaped = ::waitpid(pid, &status, 0)) < 0 && errno == EINTR) {
    }
    if (result.end == End::NotStarted) {
        if (reaped == pid && WIFEXITED(status)) {
            result.end = End::Exited;
            result.code = WEXITSTATUS(status);
        } else {
            result.end = End::Signaled;
        }
    }
    ::close(reading);
    return result;
}

}  // namespace agentspeed::quota::child
