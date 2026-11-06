import Foundation
@testable import AgentSpeedCore

/// Relay history lines (REMOTE 5.2) decoded as the connection delivers them, and the Linux core's aggregate encoding.
enum RemoteHistoryLines {
    static let host = "8C0D6B0A-1111-4222-8333-944455556666"
    static let otherHost = "1F2E3D4C-5555-4666-8777-988899990000"

    static func aggregate(requests: Int = 3, output: Int = 340, model: String = "gpt-5.5", cost: Int? = 1234) -> String {
        let micro = cost.map(String.init) ?? "null"
        let unpriced = cost == nil ? "null" : "0"
        return #"{"requests":\#(requests),"input":1200,"cachedInput":200,"cacheWriteInput":10,"output":\#(output),"#
            + #""reasoning":20,"measuredOutput":\#(output),"measuredMs":2000,"unfinalized":0,"agents":1,"#
            + #""costMicroUsd":\#(micro),"unpricedRequests":\#(unpriced),"#
            + #""models":{"\#(model)":[\#(requests),\#(output),\#(output),2000,\#(micro),\#(unpriced)]},"unpriced":[],"#
            + #""modes":[["\#(model)","serviceTier","fast","recorded","openai",1,100,90,500]]}"#
    }

    static let emptyAggregate = #"{"requests":0,"input":0,"cachedInput":0,"cacheWriteInput":0,"output":0,"reasoning":0,"#
        + #""measuredOutput":0,"measuredMs":0,"unfinalized":0,"agents":0,"costMicroUsd":0,"unpricedRequests":0,"#
        + #""models":{},"unpriced":[],"modes":[]}"#

    static func record(n: Int = 4, toolN: Int = 0) -> String {
        let bins = "[" + (0..<64).map { $0 == 10 ? String(n) : "0" }.joined(separator: ",") + "]"
        return #"{"n":\#(n),"outputSum":\#(n * 100),"durationMsSum":\#(n * 1000),"latencyBins":\#(bins),"reasonN":0,"#
            + #""reasonOutput":0,"reasonDurationMsSum":0,"cacheN":\#(n),"cacheInput":1000,"cacheCached":800,"#
            + #""toolN":\#(toolN),"toolLnSum":\#(Double(toolN) * 7.5),"toolFailed":0,"activeMs":\#(n * 1500)}"#
    }

    static func message(_ line: String, host: String = host) -> RelayMessage {
        guard case .message(let message) = RelayLineDecoder(hostId: host).decode(Data(line.utf8)) else {
            fatalError("not a relay message: \(line)")
        }
        return message
    }

    static func day(_ day: String, _ source: SourceKind = .codex, aggregate: String = aggregate(),
                    digest: String? = nil) -> RelayMessage {
        let digest = digest ?? String(repeating: "a", count: 15) + String(day.last ?? "0")
        return message(#"{"t":"historyDay","day":"\#(day)","source":"\#(source.rawValue)","aggregate":\#(aggregate),"#
                       + #""digest":"\#(digest)"}"#)
    }

    static func perf(_ day: String, _ source: SourceKind = .codex, model: String = "gpt-5.5",
                     record: String = record()) -> RelayMessage {
        message(#"{"t":"historyPerf","day":"\#(day)","source":"\#(source.rawValue)","model":"\#(model)","record":\#(record)}"#)
    }

    static let done = RelayMessage.historyDone(days: 0, analyzed: 0)

    static func event(_ message: RelayMessage, host: String = host, connection: Int = 1) -> RemoteHostEvent {
        RemoteHostEvent(hostId: host, connection: connection, kind: .message(message, seeded: false))
    }

    static func status(_ status: RemoteStatus, host: String = host, connection: Int = 1) -> RemoteHostEvent {
        RemoteHostEvent(hostId: host, connection: connection, kind: .status(RemoteHostStatus(status: status)))
    }

    static func due(host: String = host, connection: Int = 1) -> RemoteHostEvent {
        RemoteHostEvent(hostId: host, connection: connection, kind: .historyDue)
    }

    /// `history_store.cpp` `encode` of a merged day aggregate.
    static func linuxJSON(_ d: DayAggregate) -> String {
        func string(_ text: String) -> String { ReportJSON.quoted(text) }
        func optional(_ value: Int64?) -> String { value.map(String.init) ?? "null" }
        var out = #"{"requests":\#(d.totals.requests),"input":\#(d.totals.input),"cachedInput":\#(d.totals.cachedInput),"#
            + #""cacheWriteInput":\#(d.totals.cacheWriteInput),"output":\#(d.totals.output),"reasoning":\#(d.totals.reasoning),"#
            + #""measuredOutput":\#(d.measuredOutput),"measuredMs":\#(d.measuredMs),"unfinalized":\#(d.unfinalized),"#
            + #""agents":\#(d.agents),"costMicroUsd":\#(optional(d.cost?.costMicroUsd)),"#
            + #""unpricedRequests":\#(optional(d.cost.map { Int64($0.unpricedRequests) })),"models":{"#
        out += d.models.sorted { $0.key < $1.key }.map { model, t in
            string(model) + ":[\(t.requests),\(t.output),\(t.measuredOutput),\(t.measuredMs),"
                + "\(optional(t.cost?.costMicroUsd)),\(optional(t.cost.map { Int64($0.unpricedRequests) }))]"
        }.joined(separator: ",")
        out += #"},"unpriced":["#
        out += d.unpricedFacts.sorted { UnpricedFactKey.precedes($0.key, $1.key) }.map { fact, count in
            "[\(string(fact.model)),\(fact.provider.map(string) ?? "null"),\(fact.fast),\(fact.noDate),\(count)]"
        }.joined(separator: ",")
        out += #"],"modes":["#
        out += d.modes.values.sorted { $0.id < $1.id }.map { m in
            "[\(string(m.model)),\(string(m.attribution.kind.rawValue)),\(string(m.attribution.mode)),"
                + "\(string(m.attribution.evidence)),\(m.attribution.provider.map(string) ?? "null"),"
                + "\(m.requests),\(m.output),\(m.measuredOutput),\(m.measuredMs)]"
        }.joined(separator: ",")
        return out + "]}"
    }
}

extension RemoteHistoryLedger {
    /// Applies events in order and returns the last effect.
    @discardableResult
    func receive(_ events: [RemoteHostEvent], database: HistoryDatabase) -> Effect {
        var last = Effect.none
        for event in events { last = receive(event, database: database) }
        return last
    }
}
