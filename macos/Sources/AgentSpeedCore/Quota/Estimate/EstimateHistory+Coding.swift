import Foundation

extension QuotaEstimateFile {
    /// The stored form, without the file's path (its row names it): rows `[source, model, start, end, cost, tokens,
    /// requests, line]` and lines `[ts, tLo, plan, [[kind, name, minutes, percent, reset]]]`.
    func encoded() -> Data {
        let rows: [[Any]] = spend.map {
            [$0.source, $0.model, $0.startMs as Any? ?? NSNull(), $0.endMs, $0.costMicroUsd as Any? ?? NSNull(), $0.tokens,
             $0.requests, $0.codexLine?.ts as Any? ?? NSNull()]
        }
        let lines: [[Any]] = self.lines.map { line in
            [line.ts, line.tLo as Any? ?? NSNull(), line.plan as Any? ?? NSNull(), line.windows.map { w -> [Any] in
                [w.kind.rawValue, w.name as Any? ?? NSNull(), w.minutes ?? 0, w.usedPercent, w.resetsAtMs ?? 0]
            }]
        }
        return (try? JSONSerialization.data(withJSONObject: ["spend": rows, "lines": lines])) ?? Data()
    }

    /// Nil when the data does not parse.
    static func decode(_ data: Data, file: String) -> QuotaEstimateFile? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let rows = object["spend"] as? [[Any]], let lines = object["lines"] as? [[Any]] else { return nil }
        func int(_ value: Any) -> Int64? { (value as? NSNumber)?.int64Value }
        var out = QuotaEstimateFile()
        for row in rows {
            guard row.count == 8, let source = row[0] as? String, let model = row[1] as? String, let end = int(row[3]),
                  let tokens = int(row[5]), let requests = int(row[6]) else { return nil }
            out.spend.append(QuotaEstimateSpend(
                source: source, model: model, startMs: int(row[2]), endMs: end, costMicroUsd: int(row[4]), tokens: tokens,
                requests: requests, file: file, codexLine: int(row[7]).map { QuotaCodexLine.ID(file: file, ts: $0) }))
        }
        for line in lines {
            guard line.count == 4, let ts = int(line[0]), let windows = line[3] as? [[Any]] else { return nil }
            let decoded = windows.compactMap { w -> QuotaWindow? in
                guard w.count == 5, let kind = (w[0] as? String).flatMap(QuotaWindowKind.init(rawValue:)),
                      let minutes = int(w[2]), let percent = (w[3] as? NSNumber)?.doubleValue, let reset = int(w[4])
                else { return nil }
                return QuotaWindow(kind: kind, name: w[1] as? String, minutes: Int(minutes), usedPercent: percent,
                                   resetsAtMs: reset)
            }
            out.lines.append(QuotaCodexLine(file: file, ts: ts, windows: decoded, plan: line[2] as? String, tLo: int(line[1])))
        }
        return out
    }
}
