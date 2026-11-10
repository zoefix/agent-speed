import AgentSpeedStatusLineKit
import Darwin
import Foundation

/// The resolved path of this executable: its copy in `<data>/bin`.
private func executablePath() -> URL? {
    var size: UInt32 = 0
    _ = _NSGetExecutablePath(nil, &size)
    var buffer = [CChar](repeating: 0, count: Int(size) + 1)
    guard _NSGetExecutablePath(&buffer, &size) == 0, let resolved = realpath(buffer, nil) else { return nil }
    defer { free(resolved) }
    return URL(fileURLWithPath: String(cString: resolved))
}

let input = StatusLineHelper.readInput()
var code: Int32 = 0
if let executable = executablePath() {
    let nowMs = Int64((Date().timeIntervalSince1970 * 1000).rounded(.down))
    code = StatusLineHelper.run(input: input, folders: .beside(executable: executable), nowMs: nowMs) { data in
        data.withUnsafeBytes { buffer in
            var offset = 0
            while offset < buffer.count {
                let count = write(1, buffer.baseAddress! + offset, buffer.count - offset)
                if count <= 0 { break }
                offset += count
            }
        }
    }
}
exit(code)
