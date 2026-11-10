import Darwin
import Foundation

/// `agent-speed-statusline` (QUOTAS 5.5): Claude Code runs it as the status line command. It stores the `rate_limits`
/// of its input in the quota file, then runs the user's previous status line with the same input, or prints the
/// quota line. It never prints anything else, never logs the input and exits 0 on any error of its own.
public enum StatusLineHelper {
    public static let inputLimit = 1 << 20
    public static let commandTimeout: TimeInterval = 10
    public static let quotaFileName = "claude-cli.json"
    public static let previousFileName = "claude-statusline-previous.json"

    /// The data folder holds `bin/` (this executable's copy) and `quota/`.
    public struct Folders: Sendable {
        public var quota: URL

        public init(quota: URL) {
            self.quota = quota
        }

        /// `<data>/quota`, `<data>` being the folder above the executable's `bin`.
        public static func beside(executable: URL) -> Folders {
            Folders(quota: executable.deletingLastPathComponent().deletingLastPathComponent()
                .appendingPathComponent("quota", isDirectory: true))
        }

        public var quotaFile: URL { quota.appendingPathComponent(StatusLineHelper.quotaFileName) }
        public var previousFile: URL { quota.appendingPathComponent(StatusLineHelper.previousFileName) }
    }

    /// The whole run: returns the exit code.
    public static func run(input: Data, folders: Folders, nowMs: Int64, output: (Data) -> Void) -> Int32 {
        store(input: input, folders: folders, nowMs: nowMs)
        if let command = previousCommand(folders) {
            return runPrevious(command, input: input)
        }
        let stored = (try? Data(contentsOf: folders.quotaFile)).flatMap(StatusLineQuota.init(data:))
        let line = stored?.line(nowMs: nowMs) ?? ""
        if !line.isEmpty { output(Data((line + "\n").utf8)) }
        return 0
    }

    /// Merges the input's `rate_limits` into the quota file; written only when a value changed, 0600.
    static func store(input: Data, folders: Folders, nowMs: Int64) {
        guard let object = try? JSONSerialization.jsonObject(with: input) as? [String: Any],
              let limits = object["rate_limits"] as? [String: Any] else { return }
        var stored = (try? Data(contentsOf: folders.quotaFile)).flatMap(StatusLineQuota.init(data:)) ?? StatusLineQuota()
        guard stored.merge(rateLimits: limits, nowMs: nowMs) else { return }
        try? FileManager.default.createDirectory(at: folders.quota, withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])
        _ = writePrivate(stored.data(), to: folders.quotaFile)
    }

    /// The sidecar's previous `statusLine` command, when it is a command object with a command.
    static func previousCommand(_ folders: Folders) -> String? {
        guard let data = try? Data(contentsOf: folders.previousFile),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let line = object["statusLine"] as? [String: Any], line["type"] as? String == "command",
              let command = line["command"] as? String, !command.isEmpty else { return nil }
        return command
    }

    /// A temporary file beside the target, then a rename; permissions 0600.
    @discardableResult
    public static func writePrivate(_ data: Data, to url: URL) -> Bool {
        let temporary = url.deletingLastPathComponent().appendingPathComponent(".\(url.lastPathComponent).\(getpid()).tmp")
        unlink(temporary.path)
        let descriptor = open(temporary.path, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW, 0o600)
        guard descriptor >= 0 else { return false }
        let written = data.withUnsafeBytes { buffer -> Bool in
            var offset = 0
            while offset < buffer.count {
                let count = write(descriptor, buffer.baseAddress! + offset, buffer.count - offset)
                if count <= 0 { return false }
                offset += count
            }
            return true
        }
        fchmod(descriptor, 0o600)
        guard close(descriptor) == 0, written, rename(temporary.path, url.path) == 0 else {
            unlink(temporary.path)
            return false
        }
        return true
    }

    /// Runs `/bin/sh -c <command>` with the same input, environment and working folder, its standard output and error
    /// passed through; killed (with everything it started) after 10 s. Returns its exit code.
    static func runPrevious(_ command: String, input: Data) -> Int32 {
        signal(SIGPIPE, SIG_IGN)
        var pipeEnds: [Int32] = [0, 0]
        guard pipe(&pipeEnds) == 0 else { return 0 }
        let (reading, writing) = (pipeEnds[0], pipeEnds[1])
        _ = fcntl(writing, F_SETFD, FD_CLOEXEC)
        var actions: posix_spawn_file_actions_t?
        var attributes: posix_spawnattr_t?
        posix_spawn_file_actions_init(&actions)
        posix_spawnattr_init(&attributes)
        defer {
            posix_spawn_file_actions_destroy(&actions)
            posix_spawnattr_destroy(&attributes)
        }
        posix_spawn_file_actions_adddup2(&actions, reading, 0)
        posix_spawn_file_actions_addclose(&actions, reading)
        posix_spawnattr_setflags(&attributes, Int16(POSIX_SPAWN_SETPGROUP))
        posix_spawnattr_setpgroup(&attributes, 0)
        var pid: pid_t = 0
        let arguments = ["/bin/sh", "-c", command]
        var argv: [UnsafeMutablePointer<CChar>?] = arguments.map { strdup($0) } + [nil]
        var envp: [UnsafeMutablePointer<CChar>?] = ProcessInfo.processInfo.environment.map { strdup("\($0.key)=\($0.value)") } + [nil]
        defer {
            argv.forEach { free($0) }
            envp.forEach { free($0) }
        }
        let spawned = posix_spawn(&pid, "/bin/sh", &actions, &attributes, &argv, &envp)
        close(reading)
        guard spawned == 0 else {
            close(writing)
            return 0
        }
        let child = pid
        let feeder = Thread {
            input.withUnsafeBytes { buffer in
                var offset = 0
                while offset < buffer.count {
                    let count = write(writing, buffer.baseAddress! + offset, buffer.count - offset)
                    if count <= 0 { break }
                    offset += count
                }
            }
            close(writing)
        }
        feeder.start()
        let finished = DispatchSemaphore(value: 0)
        let result = ExitBox()
        Thread {
            var status: Int32 = 0
            while waitpid(child, &status, 0) < 0, errno == EINTR {}
            result.status = status
            finished.signal()
        }.start()
        if finished.wait(timeout: .now() + commandTimeout) == .timedOut {
            kill(-child, SIGKILL)
            kill(child, SIGKILL)
            finished.wait()
            return 0
        }
        let status = result.status
        // WIFEXITED / WEXITSTATUS.
        return status & 0x7F == 0 ? (status >> 8) & 0xFF : 0
    }

    private final class ExitBox: @unchecked Sendable {
        var status: Int32 = 0
    }

    /// Standard input up to the limit (one byte more tells it was cut).
    public static func readInput(_ descriptor: Int32 = 0) -> Data {
        var data = Data()
        var chunk = [UInt8](repeating: 0, count: 64 * 1024)
        while data.count <= inputLimit {
            let count = read(descriptor, &chunk, chunk.count)
            if count < 0, errno == EINTR { continue }
            if count <= 0 { break }
            data.append(contentsOf: chunk[0..<count])
        }
        return data.count > inputLimit ? data.prefix(inputLimit) : data
    }
}
