import Foundation

extension QuotaEstimateFile {
    /// The stored form, without the file's path (its row names it): rows `[source, model, start, end, cost, tokens,
    /// requests, line, counts, public]` with counts `[input, output, cachedInput, cacheWriteInput, reasoning]` or null
    /// (version 1 rows end after `line`: unmeasured), and lines `[ts, tLo, plan, [[kind, name, minutes, percent, reset]]]`.
    func encoded() -> Data {
        func optional(_ value: Int64?) -> Any { value as Any? ?? NSNull() }
        let rows: [[Any]] = spend.map {
            [$0.source, $0.model, optional($0.startMs), $0.endMs, optional($0.costMicroUsd), $0.tokens, $0.requests,
             optional($0.codexLine?.ts),
             $0.counts.map { [$0.input, $0.output, optional($0.cachedInput), optional($0.cacheWriteInput),
                              optional($0.reasoning)] as [Any] } as Any? ?? NSNull(),
             $0.publicModel]
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
            guard row.count == 8 || row.count == 10, let source = row[0] as? String, let model = row[1] as? String,
                  let end = int(row[3]), let tokens = int(row[5]), let requests = int(row[6]) else { return nil }
            var counts: QuotaTokenCounts?
            var publicModel = true
            if row.count == 10 {
                guard let flag = row[9] as? Bool else { return nil }
                publicModel = flag
                if let values = row[8] as? [Any] {
                    guard values.count == 5, let input = int(values[0]), let output = int(values[1]) else { return nil }
                    let decoded = QuotaTokenCounts(input: input, output: output, cachedInput: int(values[2]),
                                                   cacheWriteInput: int(values[3]), reasoning: int(values[4]))
                    guard decoded.isValid else { return nil }
                    counts = decoded
                } else if !(row[8] is NSNull) {
                    return nil
                }
            }
            out.spend.append(QuotaEstimateSpend(
                source: source, model: model, startMs: int(row[2]), endMs: end, costMicroUsd: int(row[4]), tokens: tokens,
                counts: counts, publicModel: publicModel, requests: requests, file: file,
                codexLine: int(row[7]).map { QuotaCodexLine.ID(file: file, ts: $0) }))
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
