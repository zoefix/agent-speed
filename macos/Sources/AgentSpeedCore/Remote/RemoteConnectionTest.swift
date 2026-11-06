import Foundation

/// What *Test Connection* found (DESIGN 13, Add / Edit sheet): the probe of REMOTE 4.2 step 1 succeeded on a supported
/// system, or the status the connection would show.
public enum RemoteTestOutcome: Equatable, Sendable {
    /// `uname -s -m` as the host printed it, and the relay it would run.
    case supported(system: String, arch: RemoteArch)
    case failed(RemoteStatus)
}

/// One run of the probe command for a host that may not be saved yet: nothing is uploaded or started, and nothing on
/// the host is written. The result arrives once on `queue`; `cancel()` ends the `ssh` process and drops the result.
///
/// `@unchecked Sendable`: every mutable property is only touched on `queue`.
public final class RemoteConnectionTest: @unchecked Sendable {
    /// Longer than `ConnectTimeout` plus a slow login shell; a host that never answers reads as `unreachable`.
    public static let timeLimit: TimeInterval = 45

    private let queue: DispatchQueue
    private let environment: RemoteEnvironment
    private var completion: (@Sendable (RemoteTestOutcome) -> Void)?
    private var process: (any RemoteProcess)?
    private var timer: (any RemoteScheduledWork)?
    private var stdout = Data()
    private var stderr = Data()
    private var timedOut = false

    private init(environment: RemoteEnvironment, queue: DispatchQueue,
                 completion: @escaping @Sendable (RemoteTestOutcome) -> Void) {
        self.environment = environment
        self.queue = queue
        self.completion = completion
    }

    /// Starts the probe of `host` and returns the running test.
    @discardableResult
    public static func start(_ host: RemoteHost, environment: RemoteEnvironment = RemoteEnvironment(),
                             queue: DispatchQueue = .main,
                             completion: @escaping @Sendable (RemoteTestOutcome) -> Void) -> RemoteConnectionTest {
        let test = RemoteConnectionTest(environment: environment, queue: queue, completion: completion)
        queue.async { test.launch(host) }
        return test
    }

    /// Ends the probe; its result is never delivered.
    public func cancel() {
        queue.async { [self] in
            completion = nil
            end()
        }
    }

    private func launch(_ host: RemoteHost) {
        guard completion != nil else { return }
        guard host.isConnectable, let command = RemoteCommand.probe(folder: host.effectiveRelayFolder),
              let arguments = RemoteSSH.arguments(destination: host.destination, remoteCommand: command,
                                                  configFile: RemoteSSH.developmentConfigFile(environment.processEnvironment))
        else {
            finish(.failed(.invalidDestination))
            return
        }
        guard environment.transport.isAvailable(RemoteSSH.executable) else {
            finish(.failed(.sshMissing))
            return
        }
        // The running process keeps the test alive until it ends (`exited` lets go of it).
        let handlers = RemoteProcessHandlers(
            stdout: { data in self.queue.async { self.received(stdout: data) } },
            stderr: { data in self.queue.async { self.received(stderr: data) } },
            exit: { status in self.queue.async { self.exited(status) } }
        )
        let request = RemoteProcessRequest(executable: RemoteSSH.executable, arguments: arguments,
                                           environment: RemoteSSH.environment(environment.processEnvironment))
        do {
            process = try environment.transport.launch(request, handlers: handlers)
        } catch {
            finish(.failed(.exited(status: 127)))
            return
        }
        process?.closeInput()
        let deadline = environment.scheduling.now.addingTimeInterval(Self.timeLimit)
        timer = environment.scheduling.schedule(at: deadline, on: queue) { [weak self] in
            guard let self, self.process != nil else { return }
            self.timedOut = true
            self.end()
        }
    }

    private func received(stdout data: Data) {
        guard stdout.count < RemoteProbeResult.maxOutputBytes else { return }
        stdout.append(data.prefix(RemoteProbeResult.maxOutputBytes - stdout.count))
    }

    private func received(stderr data: Data) {
        stderr.append(data)
        if stderr.count > RemoteStatusClassifier.maxStderrBytes {
            stderr = Data(stderr.suffix(RemoteStatusClassifier.maxStderrBytes))
        }
    }

    private func exited(_ status: Int32) {
        process = nil
        timer?.cancel()
        timer = nil
        finish(timedOut ? .failed(.unreachable) : Self.outcome(exitStatus: status, stdout: stdout, stderr: stderr))
    }

    /// How a finished probe reads: its exit and stderr as a connection classifies them, else the probe's verdict.
    static func outcome(exitStatus: Int32, stdout: Data, stderr: Data) -> RemoteTestOutcome {
        guard exitStatus == 0 else {
            return .failed(RemoteStatusClassifier.classify(exitStatus: exitStatus, stderr: stderr, stage: .probe))
        }
        let result = RemoteProbeResult.parse(stdout)
        switch result.verdict {
        case .supported(let arch): return .supported(system: result.system, arch: arch)
        case .unsupportedSystem(let system): return .failed(.unsupportedSystem(system: system))
        case .cannotVerify: return .failed(.cannotVerify)
        case .malformed: return .failed(.exited(status: exitStatus))
        }
    }

    private func end() {
        timer?.cancel()
        timer = nil
        guard let process else { return }
        process.closeInput()
        process.terminate()
        let deadline = environment.scheduling.now.addingTimeInterval(RemoteConnection.killDelay)
        _ = environment.scheduling.schedule(at: deadline, on: queue) { process.kill() }
    }

    private func finish(_ outcome: RemoteTestOutcome) {
        guard let completion else { return }
        self.completion = nil
        completion(outcome)
    }
}
