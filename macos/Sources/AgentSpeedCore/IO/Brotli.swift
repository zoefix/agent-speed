import Compression
import Foundation

/// Brotli streams of known size (METRICS 4.14).
enum Brotli {
    /// The stream decoded to exactly `size` bytes, in memory only; nil for a damaged or truncated stream and for one that
    /// would decode to anything else. The output buffer stops at `size + 1`, so a small stream cannot expand without limit.
    static func decode(_ stream: UnsafeRawBufferPointer, size: Int) -> [UInt8]? {
        guard size > 0, let source = stream.bindMemory(to: UInt8.self).baseAddress, !stream.isEmpty else { return nil }
        var output = [UInt8](repeating: 0, count: size + 1)
        var decoder = compression_stream(dst_ptr: UnsafeMutablePointer(bitPattern: 1)!, dst_size: 0,
                                         src_ptr: UnsafePointer(bitPattern: 1)!, src_size: 0, state: nil)
        guard compression_stream_init(&decoder, COMPRESSION_STREAM_DECODE, COMPRESSION_BROTLI) == COMPRESSION_STATUS_OK else {
            return nil
        }
        defer { compression_stream_destroy(&decoder) }
        let status: compression_status = output.withUnsafeMutableBufferPointer { buffer in
            decoder.dst_ptr = buffer.baseAddress!
            decoder.dst_size = buffer.count
            decoder.src_ptr = source
            decoder.src_size = stream.count
            return compression_stream_process(&decoder, Int32(COMPRESSION_STREAM_FINALIZE.rawValue))
        }
        guard status == COMPRESSION_STATUS_END, output.count - decoder.dst_size == size else { return nil }
        output.removeLast()
        return output
    }
}
