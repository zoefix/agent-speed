import AgentSpeedCore
import Foundation

/// CSV export of the rows the daily table shows. Columns are locale independent: English snake_case headers, ISO
/// dates, plain integers, a dot as the decimal separator, RFC 4180 quoting and CRLF line ends (same as Windows).
enum HistoryCSV {
    static let columns = [
        "date", "source", "requests", "input_tokens", "cached_input_tokens", "cache_write_input_tokens", "output_tokens",
        "reasoning_tokens", "cache_hit_percent", "measured_output_tokens", "measured_ms", "avg_speed_tok_s", "peak_tok_s",
        "agents", "unfinalized", "models", "cost_usd", "unpriced_requests", "price_version",
    ]

    /// Traffic bytes (DESIGN 10): own and tools per source row, empty before measuring began.
    static let trafficColumns = ["traffic_rx_bytes", "traffic_tx_bytes", "traffic_rx_tools_bytes", "traffic_tx_tools_bytes"]

    /// The header, with `cost_<code>` (lower-case) for a non-USD display currency (DESIGN 9), then the traffic columns
    /// when traffic is recorded.
    static func header(money: HistoryMoney, traffic: Bool = false) -> [String] {
        (money.isUSD ? columns : columns + ["cost_\(money.currency.code.lowercased())"]) + (traffic ? trafficColumns : [])
    }

    /// Stable identifier of a source (or `all`) for the `source` column and file names.
    static func sourceID(_ source: SourceKind?) -> String {
        switch source {
        case nil: "all"
        case .codex: "codex"
        case .claudeCode: "claude-code"
        case .claudeDesktop: "claude-desktop"
        case .deepseek: "deepseek"
        case .opencode: "opencode"
        case .gemini: "gemini"
        case .omp: "omp"
        case .antigravity: "antigravity"
        case .workbuddy: "workbuddy"
        case .workbuddyAi: "workbuddy-ai"
        case .zcode: "zcode"
        case .kimi: "kimi"
        case .grokBuild: "grokBuild"
        case .pi: "pi"
        case .openclaw: "openclaw"
        case .hermes: "hermes"
        case .droid: "droid"
        case .trae: "trae"
        }
    }

    /// Costs are every source's (subscription exclusion does not apply to the CSV): `cost_usd` exact with 6 decimals,
    /// empty for a day without cost data; `cost_<code>` with the currency's digits + 2 at the rate in use.
    static func build(_ rows: [DaySummary], source: SourceKind?, money: HistoryMoney = .usd, traffic: Bool = false) -> String {
        let header = header(money: money, traffic: traffic)
        var text = header.joined(separator: ",") + "\r\n"
        let id = sourceID(source)
        for row in rows {
            var fields = [
                row.day.description, id,
                integer(row.requests), integer(row.input), integer(row.cachedInput), integer(row.cacheWriteInput),
                integer(row.output), integer(row.reasoning), decimal(row.cacheHitRatio.map { $0 * 100 }),
                integer(row.measuredOutput), String(row.measuredMs), decimal(row.averageSpeed), decimal(row.peak),
                integer(row.agents), integer(row.unfinalized), row.models.map(\.model).joined(separator: "; "),
                row.totalCost.map { MoneyFormat.csvUSD(microUsd: $0.costMicroUsd) } ?? "",
                row.totalCost.map { integer($0.unpricedRequests) } ?? "",
                row.priceVersion.map(String.init) ?? "",
            ]
            if !money.isUSD {
                fields.append(row.totalCost.map {
                    MoneyFormat.csv(microUsd: $0.costMicroUsd, rate: money.rate, digits: money.currency.digits)
                } ?? "")
            }
            if traffic {
                let day = row.traffic
                fields += [day.map { String($0.own.rx) } ?? "", day.map { String($0.own.tx) } ?? "",
                           day.map { String($0.tools.rx) } ?? "", day.map { String($0.tools.tx) } ?? ""]
            }
            text += fields.map(field).joined(separator: ",") + "\r\n"
            // This computer's traffic of the date: a row of its own carrying only the two byte columns.
            if traffic, let machine = row.traffic?.machine {
                var machineFields = Array(repeating: "", count: header.count)
                machineFields[0] = row.day.description
                machineFields[1] = "machine"
                machineFields[header.count - 4] = String(machine.rx)
                machineFields[header.count - 3] = String(machine.tx)
                text += machineFields.map(field).joined(separator: ",") + "\r\n"
            }
        }
        return text
    }

    /// UTF-8 with a byte order mark, so spreadsheet apps open non-ASCII model names correctly.
    static func encode(_ csv: String) -> Data {
        Data([0xEF, 0xBB, 0xBF]) + Data(csv.utf8)
    }

    /// `agent-speed-history-all-2026-09-22-2026-10-21.csv`
    static func fileName(_ range: DayRange, source: SourceKind?) -> String {
        "agent-speed-history-\(sourceID(source))-\(range.start)-\(range.end).csv"
    }

    private static func integer(_ value: Int) -> String { String(value) }

    private static func decimal(_ value: Double?) -> String {
        guard let value, value.isFinite else { return "" }
        return String(format: "%.1f", locale: Locale(identifier: "en_US_POSIX"), (value * 10).rounded() / 10)
    }

    private static func field(_ raw: String) -> String {
        var value = raw
        // Text a spreadsheet would read as a formula is prefixed with an apostrophe.
        if let first = value.first, "=+-@\t\r".contains(first) { value = "'" + value }
        guard value.contains(where: { ",\"\r\n".contains($0) }) else { return value }
        return "\"" + value.replacingOccurrences(of: "\"", with: "\"\"") + "\""
    }
}
