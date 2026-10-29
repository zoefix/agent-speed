import Foundation

/// The tool calls and results of one line (METRICS 6.10), per source format. Inputs and outputs are hashed or
/// inspected in place and never kept. Only live lines need the digests (the work signals); seeding and the history
/// analysis read the calls without hashing their inputs.
enum ToolEventExtractor {
    /// Whether the family's lines hold tool calls (METRICS 6.10 table).
    static func reads(_ family: TranscriptFamily) -> Bool {
        switch family {
        case .claude, .codex, .omp, .pi, .droid: true
        default: false
        }
    }

    static func events(_ family: TranscriptFamily, _ bytes: UnsafeRawBufferPointer, hashes: Bool = true) -> [ToolEvent] {
        let reading = Reading(hashes: hashes)
        let line: Line
        switch family {
        case .claude: line = claudeLine
        case .codex: line = codexLine
        case .omp, .pi: line = piFamilyLine
        case .droid: line = droidLine
        default: return []
        }
        return scan(bytes) { (scanner: inout JSONScanner) throws(JSONScanError) in try line(&scanner, reading) }
    }

    /// An OpenClaw transcript entry (Pi's format, METRICS 6.10).
    static func openclawEvents(_ bytes: UnsafeRawBufferPointer, hashes: Bool = true) -> [ToolEvent] {
        let reading = Reading(hashes: hashes)
        return scan(bytes) { (scanner: inout JSONScanner) throws(JSONScanError) in try piFamilyLine(&scanner, reading) }
    }

    private typealias Line = (inout JSONScanner, Reading) throws(JSONScanError) -> [ToolEvent]

    private static func blocks(_ reading: Reading) -> (inout JSONScanner) throws(JSONScanError) -> ToolEvent? {
        { (scanner: inout JSONScanner) throws(JSONScanError) in try claudeBlock(&scanner, reading) }
    }

    /// How inputs are read: hashed, or skipped (every digest empty).
    struct Reading {
        var hashes: Bool

        func digest(_ scanner: inout JSONScanner) throws(JSONScanError) -> ToolDigest {
            guard hashes else {
                try scanner.skipValue()
                return .empty
            }
            return try scanner.readToolDigest()
        }
    }

    // MARK: - Claude

    /// An assistant's `tool_use` blocks, or a user's `tool_result` blocks (`is_error`).
    private static func claudeLine(_ scanner: inout JSONScanner, _ reading: Reading) throws(JSONScanError) -> [ToolEvent] {
        var role: Int?
        var blocks: [ToolEvent] = []
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "type") {
                role = try scanner.readStringMatch(["assistant", "user"])
            } else if scanner.key(key, is: "message") {
                blocks = try member("content", of: &scanner) { scanner throws(JSONScanError) in
                    try objects(of: &scanner, Self.blocks(reading))
                } ?? []
            } else {
                try scanner.skipValue()
            }
        }
        switch role {
        case 0: return blocks.filter(\.isCall)
        case 1: return blocks.filter { !$0.isCall }
        default: return []
        }
    }

    private static func claudeBlock(_ scanner: inout JSONScanner, _ reading: Reading) throws(JSONScanError) -> ToolEvent? {
        var kind: Int?
        var id: String?
        var name: String?
        var digest = ToolDigest.empty
        var resultId: String?
        var isError = false
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "type") {
                kind = try scanner.readStringMatch(["tool_use", "tool_result"])
            } else if scanner.key(key, is: "id") {
                id = try scanner.readString()
            } else if scanner.key(key, is: "name") {
                name = try scanner.readString()
            } else if scanner.key(key, is: "input") {
                digest = try reading.digest(&scanner)
            } else if scanner.key(key, is: "tool_use_id") {
                resultId = try scanner.readString()
            } else if scanner.key(key, is: "is_error") {
                isError = try scanner.readBool() == true
            } else {
                try scanner.skipValue()
            }
        }
        switch kind {
        case 0: return call(id: id, tool: name, digest: digest)
        case 1: return resultId.map { .result(id: $0, failed: isError) }
        default: return nil
        }
    }

    // MARK: - Factory Droid

    /// A `message` line: the assistant's `tool_use` blocks, or the user's `tool_result` blocks (`is_error`).
    private static func droidLine(_ scanner: inout JSONScanner, _ reading: Reading) throws(JSONScanError) -> [ToolEvent] {
        var isMessage = false
        var role: Int?
        var blocks: [ToolEvent] = []
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "type") {
                isMessage = try scanner.readStringMatch(["message"]) == 0
            } else if scanner.key(key, is: "message") {
                (role, blocks) = try droidMessage(&scanner, reading)
            } else {
                try scanner.skipValue()
            }
        }
        guard isMessage else { return [] }
        switch role {
        case 0: return blocks.filter(\.isCall)
        case 1: return blocks.filter { !$0.isCall }
        default: return []
        }
    }

    private static func droidMessage(_ scanner: inout JSONScanner, _ reading: Reading) throws(JSONScanError) -> (Int?, [ToolEvent]) {
        guard try scanner.beginObjectIfPresent() else { return (nil, []) }
        var role: Int?
        var blocks: [ToolEvent] = []
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "role") {
                role = try scanner.readStringMatch(["assistant", "user"])
            } else if scanner.key(key, is: "content") {
                blocks = try objects(of: &scanner, Self.blocks(reading))
            } else {
                try scanner.skipValue()
            }
        }
        return (role, blocks)
    }

    // MARK: - Codex

    /// A `response_item` function or custom tool call, or its output with the exit code of an exec.
    private static func codexLine(_ scanner: inout JSONScanner, _ reading: Reading) throws(JSONScanError) -> [ToolEvent] {
        var isResponseItem = false
        var event: ToolEvent?
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "type") {
                isResponseItem = try scanner.readStringMatch(["response_item"]) == 0
            } else if scanner.key(key, is: "payload") {
                event = try scanner.beginObjectIfPresent() ? try codexPayload(&scanner, reading) : nil
            } else {
                try scanner.skipValue()
            }
        }
        return isResponseItem ? [event].compactMap { $0 } : []
    }

    private static func codexPayload(_ scanner: inout JSONScanner, _ reading: Reading) throws(JSONScanError) -> ToolEvent? {
        var kind: Int?
        var callId: String?
        var name: String?
        var arguments = ToolDigest.empty
        var input = ToolDigest.empty
        var exitCode: Int64?
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "type") {
                kind = try scanner.readStringMatch(
                    ["function_call", "custom_tool_call", "function_call_output", "custom_tool_call_output"]
                )
            } else if scanner.key(key, is: "call_id") {
                callId = try scanner.readString()
            } else if scanner.key(key, is: "name") {
                name = try scanner.readString()
            } else if scanner.key(key, is: "arguments") {
                arguments = try reading.digest(&scanner)
            } else if scanner.key(key, is: "input") {
                input = try reading.digest(&scanner)
            } else if scanner.key(key, is: "output") {
                exitCode = try CodexExitCode.read(&scanner)
            } else {
                try scanner.skipValue()
            }
        }
        switch kind {
        case 0: return call(id: callId, tool: name, digest: arguments)
        case 1: return call(id: callId, tool: name, digest: input)
        case 2, 3: return callId.map { .result(id: $0, failed: exitCode.map { $0 != 0 }) }
        default: return nil
        }
    }

    // MARK: - omp and Pi

    /// omp and Pi share the message shape: an assistant's `toolCall` blocks, or a `toolResult` (`isError`).
    private static func piFamilyLine(_ scanner: inout JSONScanner, _ reading: Reading) throws(JSONScanError) -> [ToolEvent] {
        var isMessage = false
        var events: [ToolEvent] = []
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "type") {
                isMessage = try scanner.readStringMatch(["message"]) == 0
            } else if scanner.key(key, is: "message") {
                events = try scanner.beginObjectIfPresent() ? try piFamilyMessage(&scanner, reading) : []
            } else {
                try scanner.skipValue()
            }
        }
        return isMessage ? events : []
    }

    private static func piFamilyMessage(_ scanner: inout JSONScanner, _ reading: Reading) throws(JSONScanError) -> [ToolEvent] {
        var role: Int?
        var calls: [ToolEvent] = []
        var resultId: String?
        var isError = false
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "role") {
                role = try scanner.readStringMatch(["assistant", "toolResult"])
            } else if scanner.key(key, is: "content") {
                calls = try objects(of: &scanner) { (inner: inout JSONScanner) throws(JSONScanError) in
                    try piFamilyToolCall(&inner, reading)
                }
            } else if scanner.key(key, is: "toolCallId") {
                resultId = try scanner.readString()
            } else if scanner.key(key, is: "isError") {
                isError = try scanner.readBool() == true
            } else {
                try scanner.skipValue()
            }
        }
        switch role {
        case 0: return calls
        case 1: return resultId.map { [.result(id: $0, failed: isError)] } ?? []
        default: return []
        }
    }

    private static func piFamilyToolCall(_ scanner: inout JSONScanner, _ reading: Reading) throws(JSONScanError) -> ToolEvent? {
        var isToolCall = false
        var id: String?
        var name: String?
        var digest = ToolDigest.empty
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "type") {
                isToolCall = try scanner.readStringMatch(["toolCall"]) == 0
            } else if scanner.key(key, is: "id") {
                id = try scanner.readString()
            } else if scanner.key(key, is: "name") {
                name = try scanner.readString()
            } else if scanner.key(key, is: "arguments") {
                digest = try reading.digest(&scanner)
            } else {
                try scanner.skipValue()
            }
        }
        return isToolCall ? call(id: id, tool: name, digest: digest) : nil
    }

    // MARK: - Scanning

    private static func call(id: String?, tool: String?, digest: ToolDigest) -> ToolEvent? {
        guard let id, let tool else { return nil }
        return .call(id: id, ToolCall(tool: tool, digest: digest))
    }

    /// A whole line as one object; a malformed line has no events.
    private static func scan(
        _ bytes: UnsafeRawBufferPointer,
        _ line: (inout JSONScanner) throws(JSONScanError) -> [ToolEvent]
    ) -> [ToolEvent] {
        var scanner = JSONScanner(bytes)
        do {
            try scanner.beginObject()
            let events = try line(&scanner)
            try scanner.finish()
            return events
        } catch {
            return []
        }
    }

    /// The value of member `name` of an object value read by `body` (the last one when repeated); nil when the
    /// value is not an object or has no such member.
    private static func member<Result>(
        _ name: StaticString,
        of scanner: inout JSONScanner,
        _ body: (inout JSONScanner) throws(JSONScanError) -> Result
    ) throws(JSONScanError) -> Result? {
        guard try scanner.beginObjectIfPresent() else { return nil }
        var result: Result?
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: name) {
                result = try body(&scanner)
            } else {
                try scanner.skipValue()
            }
        }
        return result
    }

    /// The object elements of an array value read by `body`; any other value gives none.
    static func objects<Element>(
        of scanner: inout JSONScanner,
        _ body: (inout JSONScanner) throws(JSONScanError) -> Element?
    ) throws(JSONScanError) -> [Element] {
        guard try scanner.beginArrayIfPresent() else {
            try scanner.skipValue()
            return []
        }
        var elements: [Element] = []
        while try scanner.nextElement() {
            guard try scanner.beginObjectIfPresent(), let element = try body(&scanner) else { continue }
            elements.append(element)
        }
        return elements
    }
}

/// `exit_code` of an exec output's leading JSON object (METRICS 6.10): the output text itself, or the text of the
/// first `input_text` item when the output is an array (or a string holding one). nil when it names none.
enum CodexExitCode {
    static func read(_ scanner: inout JSONScanner) throws(JSONScanError) -> Int64? {
        if scanner.nextIsString {
            return try scanner.withStoredText { text in
                let trimmed = trimmingLeadingWhitespace(text)
                guard trimmed.first == UInt8(ascii: "[") else { return leadingObjectCode(trimmed) }
                var items = JSONScanner(text)
                guard let code = try? firstInputTextCode(&items), (try? items.finish()) != nil else { return nil }
                return code
            }
        }
        return try firstInputTextCode(&scanner)
    }

    /// The code in the text of the first `input_text` item of an array value (any other value is skipped).
    private static func firstInputTextCode(_ scanner: inout JSONScanner) throws(JSONScanError) -> Int64? {
        try ToolEventExtractor.objects(of: &scanner, inputTextItem).first.flatMap { $0 }
    }

    /// An `input_text` item gives the code of its `text` (nil inside when it has none); other items give nothing.
    private static func inputTextItem(_ scanner: inout JSONScanner) throws(JSONScanError) -> Int64?? {
        var isInputText = false
        var code: Int64?
        while let key = try scanner.nextMember() {
            if scanner.key(key, is: "type") {
                isInputText = try scanner.readStringMatch(["input_text"]) == 0
            } else if scanner.key(key, is: "text") {
                let isString = scanner.nextIsString
                code = try scanner.withStoredText { isString ? leadingObjectCode(trimmingLeadingWhitespace($0)) : nil }
            } else {
                try scanner.skipValue()
            }
        }
        return isInputText ? .some(code) : nil
    }

    /// An integer `exit_code` of the JSON object the text starts with; what follows the object does not matter.
    private static func leadingObjectCode(_ text: UnsafeRawBufferPointer) -> Int64? {
        guard text.first == UInt8(ascii: "{") else { return nil }
        var scanner = JSONScanner(text)
        var code: Int64?
        do {
            try scanner.beginObject()
            while let key = try scanner.nextMember() {
                if scanner.key(key, is: "exit_code") {
                    code = try scanner.readNumber().flatMap { $0.isInteger ? $0.truncated : nil }
                } else {
                    try scanner.skipValue()
                }
            }
        } catch {
            return nil
        }
        return code
    }

    /// Python's `str.lstrip()`: ASCII and Unicode whitespace.
    private static func trimmingLeadingWhitespace(_ text: UnsafeRawBufferPointer) -> UnsafeRawBufferPointer {
        var index = 0
        while index < text.count {
            let (scalar, length) = decodeScalar(text, at: index)
            guard JSONScanner.isPythonWhitespace(scalar) else { break }
            index += length
        }
        return UnsafeRawBufferPointer(rebasing: text[index...])
    }

    private static func decodeScalar(_ text: UnsafeRawBufferPointer, at index: Int) -> (UInt32, Int) {
        var iterator = text[index...].makeIterator()
        var decoder = UTF8()
        guard case .scalarValue(let scalar) = decoder.decode(&iterator) else { return (0, 1) }
        return (scalar.value, UTF8.width(scalar))
    }
}
