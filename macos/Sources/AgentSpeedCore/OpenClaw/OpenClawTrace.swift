import Foundation

/// One `trace.metadata` row of a window (METRICS 4.14, Trajectory rows): when it was written and what the attempt ran
/// with. A row that lost `data.model` to OpenClaw's size limit has no facts, which ends the previous row's reach.
struct OpenClawTrace: Equatable, Sendable {
    var createdMs: Int64
    var modes: ExecutionModes
}

/// Reads `data.model.thinkLevel` and `data.model.fastMode` of a trace row. The configuration, skills and prompt report
/// around them are skipped and never kept.
enum OpenClawTraceExtractor {
    private static let types: [StaticString] = ["trace.metadata"]

    /// Nil unless the text is a JSON object whose top-level `type` is `trace.metadata`.
    static func modes(_ bytes: UnsafeRawBufferPointer) -> ExecutionModes? {
        var scanner = JSONScanner(bytes)
        do {
            try scanner.beginObject()
            var isTrace = false
            var modes = ExecutionModes.empty
            while let key = try scanner.nextMember() {
                if scanner.key(key, is: "type") {
                    isTrace = try scanner.readStringMatch(types) == 0
                } else if scanner.key(key, is: "data") {
                    modes = try scanData(&scanner)
                } else {
                    try scanner.skipValue()
                }
            }
            try scanner.finish()
            return isTrace ? modes : nil
        } catch {
            return nil
        }
    }

    private static func scanData(_ scanner: inout JSONScanner) throws(JSONScanError) -> ExecutionModes {
        guard try scanner.beginObjectIfPresent() else { return .empty }
        var modes = ExecutionModes.empty
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "model") {
                modes = try scanModel(&scanner)
            } else {
                try scanner.skipValue()
            }
        }
        return modes
    }

    /// `fastMode` counts only when `true`: `ultrafast` and `auto` are written as `false` too.
    private static func scanModel(_ scanner: inout JSONScanner) throws(JSONScanError) -> ExecutionModes {
        guard try scanner.beginObjectIfPresent() else { return .empty }
        var thinkLevel: String?
        var fast = false
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "thinkLevel") {
                thinkLevel = try scanner.readString()
            } else if scanner.key(key, is: "fastMode") {
                fast = try scanner.readBool() == true
            } else {
                try scanner.skipValue()
            }
        }
        let tier = fast ? ExecutionModeFact(kind: .serviceTier, rawValue: "fast", evidence: .selected, scope: .turn) : nil
        return ExecutionModes([ExecutionModeAdapters.effort(thinkLevel, evidence: .recorded, scope: .turn), tier].compactMap { $0 })
    }
}
