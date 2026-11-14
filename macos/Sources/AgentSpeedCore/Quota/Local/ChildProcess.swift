import Darwin
import Foundation

/// Runs one short-lived command for a quota route (QUOTAS 9.4 *CLI usage report*): standard input from `/dev/null`,
/// standard error discarded unread, every other descriptor closed, its own process group, a working folder and an
/// environment of the caller's. Standard output is read up to a cap; the group is killed once the command ends, runs
/// past its time or writes past the cap, so that no descendant stays.
enum ChildProcess {
    struct Command {
        var executable: String
        var arguments: [String]
        var environment: [String: String]
        var directory: String
        var timeoutMs: Int64
        var outputCap: Int
    }

    enum Outcome: Equatable {
        /// The command ended (whatever its exit status) within its time, with this much output.
        case finished(Data)
        case tooMuchOutput
        case timedOut
        case notStarted
    }

    /// Output still arriving after the command itself ended (a descendant holding the pipe) is read this long.
    static let drainMs: Int64 = 1_000

    static func run(_ command: Command) -> Outcome {
        var pipe: [Int32] = [-1, -1]
        guard Darwin.pipe(&pipe) == 0 else { return .notStarted }
        let (readEnd, writeEnd) = (pipe[0], pipe[1])
        defer { close(readEnd) }
        var actions: posix_spawn_file_actions_t?
        var attributes: posix_spawnattr_t?
        posix_spawn_file_actions_init(&actions)
        posix_spawnattr_init(&attributes)
        defer {
            posix_spawn_file_actions_destroy(&actions)
            posix_spawnattr_destroy(&attributes)
        }
        posix_spawn_file_actions_addopen(&actions, 0, "/dev/null", O_RDONLY, 0)
        posix_spawn_file_actions_adddup2(&actions, writeEnd, 1)
        posix_spawn_file_actions_addopen(&actions, 2, "/dev/null", O_WRONLY, 0)
        posix_spawn_file_actions_addchdir_np(&actions, command.directory)
        posix_spawnattr_setflags(&attributes, Int16(POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_CLOEXEC_DEFAULT | POSIX_SPAWN_SETSIGDEF
                                                    | POSIX_SPAWN_SETSIGMASK))
        posix_spawnattr_setpgroup(&attributes, 0)
        var defaults = sigset_t()
        sigemptyset(&defaults)
        for signal in [SIGPIPE, SIGINT, SIGTERM, SIGHUP, SIGCHLD] { sigaddset(&defaults, signal) }
        posix_spawnattr_setsigdefault(&attributes, &defaults)
        var empty = sigset_t()
        sigemptyset(&empty)
        posix_spawnattr_setsigmask(&attributes, &empty)

        let argv = ([command.executable] + command.arguments).map { strdup($0) } + [nil]
        let envp = command.environment.map { strdup("\($0.key)=\($0.value)") } + [nil]
        defer {
            argv.forEach { free($0) }
            envp.forEach { free($0) }
        }
        var pid: pid_t = 0
        let spawned = posix_spawn(&pid, command.executable, &actions, &attributes, argv, envp)
        close(writeEnd)
        guard spawned == 0, pid > 0 else { return .notStarted }

        let started = Clock.monotonicMs()
        var output = Data()
        var outcome: Outcome?
        var endedMs: Int64?
        var buffer = [UInt8](repeating: 0, count: 64 << 10)
        while outcome == nil {
            let now = Clock.monotonicMs()
            if endedMs == nil, exited(pid) { endedMs = now }
            if let ended = endedMs, now - ended >= drainMs { break }
            if now - started >= command.timeoutMs {
                outcome = .timedOut
                break
            }
            var poller = pollfd(fd: readEnd, events: Int16(POLLIN), revents: 0)
            let ready = poll(&poller, 1, 50)
            guard ready > 0 else { continue }
            let count = buffer.withUnsafeMutableBytes { read(readEnd, $0.baseAddress, $0.count) }
            if count > 0 {
                output.append(contentsOf: buffer[0..<count])
                if output.count > command.outputCap { outcome = .tooMuchOutput }
            } else if count == 0 {
                if endedMs == nil {
                    // Output closed: the command ends, or keeps running without it until its time.
                    while !exited(pid), Clock.monotonicMs() - started < command.timeoutMs { usleep(20_000) }
                    if !exited(pid) { outcome = .timedOut }
                }
                break
            } else if errno != EINTR, errno != EAGAIN {
                break
            }
        }
        // The command itself is not reaped yet, so its group id cannot have been reused.
        kill(-pid, SIGKILL)
        kill(pid, SIGKILL)
        var status: Int32 = 0
        while waitpid(pid, &status, 0) < 0, errno == EINTR {}
        return outcome ?? .finished(output)
    }

    /// The command has ended; it stays unreaped (`WNOWAIT`).
    private static func exited(_ pid: pid_t) -> Bool {
        var info = siginfo_t()
        guard waitid(P_PID, id_t(pid), &info, WEXITED | WNOHANG | WNOWAIT) == 0 else { return false }
        return info.si_pid == pid
    }
}
