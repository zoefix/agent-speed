import Foundation

extension QuotaEstimateFile {
    /// The stored form, without the file's path (its row names it): rows `[source, model, start, end, cost, tokens,
    /// requests, line, optional counters]` and lines `[ts, tLo, plan, [[kind, name, minutes, percent, reset]]]`.
    func encoded() -> Data {
        func nullable<T>(_ value: T?) -> Any { value.map { $0 as Any } ?? NSNull() }
        let rows: [[Any]] = spend.map {
            var row: [Any] = [$0.source, $0.model, nullable($0.startMs), $0.endMs, nullable($0.costMicroUsd),
                              $0.tokens, $0.requests, nullable($0.codexLine?.ts)]
            if let contribution = $0.tokenContribution {
                let counts = contribution.counts
                let detail: [Any] = [counts.input, counts.output, nullable(counts.cachedInput),
                                     nullable(counts.cacheWriteInput), nullable(counts.reasoning),
                                     nullable(contribution.model), contribution.earliestEndMs, contribution.latestEndMs]
                row.append(detail)
            }
            return row
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
            guard (8...9).contains(row.count), let source = row[0] as? String, let model = row[1] as? String, let end = int(row[3]),
                  let tokens = int(row[5]), let requests = int(row[6]) else { return nil }
            var spend = QuotaEstimateSpend(
                source: source, model: model, startMs: int(row[2]), endMs: end, costMicroUsd: int(row[4]), tokens: tokens,
                requests: requests, file: file, codexLine: int(row[7]).map { QuotaCodexLine.ID(file: file, ts: $0) })
            if row.count == 9, let detail = row[8] as? [Any], detail.count == 8,
               let input = int(detail[0]), let output = int(detail[1]),
               let earliest = int(detail[6]), let latest = int(detail[7]), earliest <= latest, latest == end {
                let counts = QuotaTokenCounts(input: input, output: output, cachedInput: int(detail[2]),
                                             cacheWriteInput: int(detail[3]), reasoning: int(detail[4]))
                if counts.isValid, input + output == tokens {
                    let model = (detail[5] as? String).flatMap { ReportModelIds.isValid($0) ? $0 : nil }
                    spend.tokenContribution = QuotaTokenContribution(counts: counts, model: model,
                                                                     earliestEndMs: earliest, latestEndMs: latest)
                }
            }
            out.spend.append(spend)
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
