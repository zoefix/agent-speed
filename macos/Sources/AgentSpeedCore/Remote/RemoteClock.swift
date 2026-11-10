import Foundation

/// The host's clock offset `δ = remote − local` (REMOTE 6), from `ping` / `clock` pairs: `δ = now_remote −
/// (t_send + t_receive) / 2`, the sample with the smallest round trip among the last 8. Until the first sample,
/// `hello.now` gives `δ`.
public struct RemoteClockEstimator: Equatable, Sendable {
    public static let sampleLimit = 8
    public static let skewThresholdMs: Int64 = 5000

    public struct Sample: Equatable, Sendable {
        public var offsetMs: Double
        public var roundTripMs: Int64
    }

    public private(set) var samples: [Sample] = []
    private var helloOffsetMs: Double?

    public init() {}

    public mutating func noteHello(remoteNowMs: Int64, localMs: Int64) {
        helloOffsetMs = Double(remoteNowMs) - Double(localMs)
    }

    /// A `clock` reply to a ping sent at `sendMs` and received at `receiveMs` (both local).
    public mutating func addSample(sendMs: Int64, receiveMs: Int64, remoteNowMs: Int64) {
        guard receiveMs >= sendMs else { return }
        let offset = Double(remoteNowMs) - (Double(sendMs) + Double(receiveMs)) / 2
        samples.append(Sample(offsetMs: offset, roundTripMs: receiveMs - sendMs))
        if samples.count > Self.sampleLimit { samples.removeFirst(samples.count - Self.sampleLimit) }
    }

    /// `δ` in whole milliseconds; nil before `hello`.
    public var offsetMs: Int64? {
        let best = samples.min { $0.roundTripMs < $1.roundTripMs }
        guard let offset = best?.offsetMs ?? helloOffsetMs else { return nil }
        return Int64(offset.rounded())
    }

    public var isSkewed: Bool {
        offsetMs.map(Self.isSkewed) ?? false
    }

    public static func isSkewed(_ offsetMs: Int64) -> Bool {
        offsetMs.magnitude >= UInt64(skewThresholdMs)
    }
}

extension RelayMessage {
    /// The message with every time of an `agent`, `response`, `tool` or `quota` line converted to local time (`t − δ`);
    /// nil when a converted time leaves the years 2000–2100 (the line is then invalid). Durations are unchanged.
    public func converted(offsetMs delta: Int64) -> RelayMessage? {
        func shift(_ ms: Int64) -> Int64? {
            let (local, overflow) = ms.subtractingReportingOverflow(delta)
            return !overflow && RemoteFields.isTime(local) ? local : nil
        }
        func shiftOptional(_ ms: Int64?) -> Int64?? {
            guard let ms else { return .some(nil) }
            guard let local = shift(ms) else { return nil }
            return .some(local)
        }
        switch self {
        case .agent(var agent):
            guard let inFlight = shiftOptional(agent.inFlightStartMs),
                  let lastLine = shiftOptional(agent.lastLineMs) else { return nil }
            agent.inFlightStartMs = inFlight
            agent.lastLineMs = lastLine
            for index in agent.actions.indices {
                guard let call = shift(agent.actions[index].callMs),
                      let result = shiftOptional(agent.actions[index].resultMs) else { return nil }
                agent.actions[index].callMs = call
                agent.actions[index].resultMs = result
            }
            return .agent(agent)
        case .response(var response):
            guard let end = shift(response.record.endMs), let start = shiftOptional(response.record.startMs),
                  let first = shiftOptional(response.record.firstMs) else { return nil }
            response.record.endMs = end
            response.record.startMs = start
            response.record.firstMs = first
            return .response(response)
        case .tool(var tool):
            guard let call = shift(tool.completion.callMs), let result = shift(tool.completion.resultMs) else { return nil }
            tool.completion.callMs = call
            tool.completion.resultMs = result
            return .tool(tool)
        case .quota(var report):
            guard let observed = shift(report.snapshot.observedMs) else { return nil }
            var windows = report.snapshot.windows
            for index in windows.indices {
                guard let resets = shiftOptional(windows[index].resetsAtMs) else { return nil }
                windows[index].resetsAtMs = resets
            }
            if case .ok(let at) = report.status {
                guard let local = shift(at) else { return nil }
                report.status = .ok(local)
            }
            report.snapshot = QuotaSnapshot(source: report.snapshot.source, observedMs: observed, windows: windows)
            return .quota(report)
        default:
            return self
        }
    }
}
