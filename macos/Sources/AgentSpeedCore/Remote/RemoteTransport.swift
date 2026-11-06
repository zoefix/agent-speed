import Foundation

/// One program to run: always `/usr/bin/ssh` with an argument vector, never a shell (REMOTE 4.1).
public struct RemoteProcessRequest: Equatable, Sendable {
    public var executable: String
    public var arguments: [String]
    public var environment: [String: String]

    public init(executable: String, arguments: [String], environment: [String: String]) {
        self.executable = executable
        self.arguments = arguments
        self.environment = environment
    }
}

/// Callbacks of a running process, called on any thread. `exit` comes last: after stdout and stderr reached their
/// end and the process was reaped.
public struct RemoteProcessHandlers: Sendable {
    public var stdout: @Sendable (Data) -> Void
    public var stderr: @Sendable (Data) -> Void
    /// The exit status, or 128 + the signal number.
    public var exit: @Sendable (Int32) -> Void

    public init(stdout: @escaping @Sendable (Data) -> Void, stderr: @escaping @Sendable (Data) -> Void,
                exit: @escaping @Sendable (Int32) -> Void) {
        self.stdout = stdout
        self.stderr = stderr
        self.exit = exit
    }
}

/// A running process. Writes go to its stdin in order, off the caller's thread.
public protocol RemoteProcess: AnyObject, Sendable {
    /// Queues `data`; `completion` (any thread) says whether all of it was written. Ignored after `closeInput`.
    func write(_ data: Data, completion: (@Sendable (Bool) -> Void)?)
    /// Closes stdin after the queued writes (end of file for the remote side).
    func closeInput()
    /// SIGTERM.
    func terminate()
    /// SIGKILL.
    func kill()
}

/// Starts processes; tests replace it with a scripted relay.
public protocol RemoteTransport: Sendable {
    /// The executable exists (`sshMissing` otherwise).
    func isAvailable(_ executable: String) -> Bool
    func launch(_ request: RemoteProcessRequest, handlers: RemoteProcessHandlers) throws -> any RemoteProcess
}

/// `Foundation.Process` with three pipes.
public struct ProcessRemoteTransport: RemoteTransport {
    public init() {}

    public func isAvailable(_ executable: String) -> Bool {
        FileManager.default.isExecutableFile(atPath: executable)
    }

    public func launch(_ request: RemoteProcessRequest, handlers: RemoteProcessHandlers) throws -> any RemoteProcess {
        try PipedProcess(request, handlers: handlers)
    }
}

/// `@unchecked Sendable`: `Process` is used from the caller and its callbacks only through its thread-safe API; the
/// input descriptor is only touched on `writeQueue`.
final class PipedProcess: RemoteProcess, @unchecked Sendable {
    private let process = Process()
    private let input = Pipe()
    private let writeQueue = DispatchQueue(label: "app.agentspeed.remote.stdin", qos: .utility)
    private var inputOpen = true

    init(_ request: RemoteProcessRequest, handlers: RemoteProcessHandlers) throws {
        let output = Pipe()
        let errors = Pipe()
        process.executableURL = URL(fileURLWithPath: request.executable)
        process.arguments = request.arguments
        process.environment = request.environment
        process.standardInput = input
        process.standardOutput = output
        process.standardError = errors
        // A relay that went away must not raise SIGPIPE in the app: the write fails with EPIPE instead.
        _ = fcntl(input.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1)

        let group = DispatchGroup()
        for (pipe, deliver) in [(output, handlers.stdout), (errors, handlers.stderr)] {
            group.enter()
            pipe.fileHandleForReading.readabilityHandler = { handle in
                let data = handle.availableData
                if data.isEmpty {
                    handle.readabilityHandler = nil
                    group.leave()
                } else {
                    deliver(data)
                }
            }
        }
        group.enter()
        process.terminationHandler = { _ in group.leave() }
        do {
            try process.run()
        } catch {
            output.fileHandleForReading.readabilityHandler = nil
            errors.fileHandleForReading.readabilityHandler = nil
            throw error
        }
        let process = self.process
        group.notify(queue: .global(qos: .utility)) {
            let status = process.terminationStatus
            handlers.exit(process.terminationReason == .uncaughtSignal ? 128 + status : status)
        }
    }

    func write(_ data: Data, completion: (@Sendable (Bool) -> Void)?) {
        writeQueue.async { [self] in
            guard inputOpen else {
                completion?(false)
                return
            }
            let descriptor = input.fileHandleForWriting.fileDescriptor
            let written = data.withUnsafeBytes { bytes -> Bool in
                guard var pointer = bytes.baseAddress else { return true }
                var remaining = bytes.count
                while remaining > 0 {
                    let count = Darwin.write(descriptor, pointer, remaining)
                    if count < 0 {
                        if errno == EINTR { continue }
                        return false
                    }
                    pointer += count
                    remaining -= count
                }
                return true
            }
            completion?(written)
        }
    }

    func closeInput() {
        writeQueue.async { [self] in
            guard inputOpen else { return }
            inputOpen = false
            try? input.fileHandleForWriting.close()
        }
    }

    func terminate() {
        if process.isRunning { process.terminate() }
    }

    func kill() {
        if process.isRunning { _ = Darwin.kill(process.processIdentifier, SIGKILL) }
    }
}
