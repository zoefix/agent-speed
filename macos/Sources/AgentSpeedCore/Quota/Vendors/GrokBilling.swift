import Foundation

/// Grok Build's billing config (QUOTAS 9.2), from its log line `billing: fetched credits config` or the live
/// `v1/billing?format=credits` response: one window, weekly or monthly.
public enum GrokBilling {
    static let logMessage = "billing: fetched credits config"

    /// The newest config among the log's lines, with its time; nil when no line has one.
    static func fromLog<Lines: Sequence>(_ lines: Lines) -> (observedMs: Int64, windows: [QuotaWindow])? where Lines.Element == Data {
        var best: (Int64, [String: JSONValue])?
        for line in lines {
            guard line.count < 1 << 20, let object = (try? JSONParser.parse(line))?.object,
                  object["msg"]?.string == logMessage,
                  let config = object["ctx"]?["config"]?.object,
                  let ts = VendorValue.timeMs(object["ts"]) else { continue }
            if best.map({ ts > $0.0 }) ?? true { best = (ts, config) }
        }
        return best.map { ($0.0, windows($0.1)) }
    }

    /// The live response's `config`.
    static func fromResponse(_ body: JSONValue) -> [QuotaWindow]? {
        guard let config = body["config"]?.object else { return nil }
        return windows(config)
    }

    /// `currentPeriod.type` containing `MONTH` is a month, else a week; `creditUsagePercent`, or 0 for a unified
    /// billing user with both period ends (protobuf drops zeros); the reset is the period's end, else
    /// `billingPeriodEnd`.
    static func windows(_ config: [String: JSONValue]) -> [QuotaWindow] {
        let period = VendorValue.object(config["currentPeriod"])
        let kind: QuotaWindowKind = VendorValue.text(period["type"]).uppercased().contains("MONTH") ? .month : .sevenDay
        var used = VendorValue.number(config["creditUsagePercent"])
        if used == nil {
            guard config["isUnifiedBillingUser"]?.bool == true, truthy(period["start"]), truthy(period["end"]) else { return [] }
            used = 0
        }
        let end = VendorValue.timeMs(period["end"])
        let resets = end ?? VendorValue.timeMs(config["billingPeriodEnd"])
        var minutes: Int?
        if kind == .month, let start = VendorValue.timeMs(period["start"]), let end {
            minutes = Int(clamping: (end - start) / 60_000)
        }
        return [VendorValue.window(kind, used ?? 0, resets, minutes: minutes)]
    }

    /// Python truthiness of a period end: a non-empty string or a non-zero number.
    private static func truthy(_ value: JSONValue?) -> Bool {
        switch value {
        case .string(let text)?: !text.isEmpty
        case .number(let literal)?: (literal.double ?? 0) != 0
        case .bool(let flag)?: flag
        case .array(let items)?: !items.isEmpty
        case .object(let members)?: !members.isEmpty
        default: false
        }
    }
}
