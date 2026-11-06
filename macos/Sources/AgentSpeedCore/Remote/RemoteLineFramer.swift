import Foundation

/// Splits the relay's stdout into lines (REMOTE 5.1): at most 1 MiB each. A longer line is dropped and counted, so
/// the connection counts it as invalid.
struct RemoteLineFramer: Sendable {
    static let maxLineBytes = 1 << 20

    private var splitter = LineSplitter(maxLineBytes: maxLineBytes)

    init() {}

    /// The complete lines of `chunk`, in order, and how many oversized lines it ended.
    mutating func consume(_ chunk: Data) -> (lines: [Data], oversized: Int) {
        var lines: [Data] = []
        let before = splitter.oversizedLines
        chunk.withUnsafeBytes { bytes in
            splitter.consume(bytes) { lines.append(Data($0)) }
        }
        return (lines, splitter.oversizedLines - before)
    }
}
