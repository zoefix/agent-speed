import Compression
import CZstd
import Foundation
import zlib

/// HTTP content codings of a cached response body (QUOTAS 4.2): `zstd`, `gzip`, `br` and `identity`. Every decoder stops
/// past `limit` bytes of output, so a small body cannot expand without bound; a damaged or truncated body gives nil.
enum ContentDecoding {
    enum Coding: Equatable {
        case identity, zstd, gzip, brotli
    }

    /// The coding a `content-encoding` value names (absent is identity); nil for any other value.
    static func coding(_ header: String?) -> Coding? {
        switch (header ?? "identity").trimmingCharacters(in: .whitespaces).lowercased() {
        case "identity", "": .identity
        case "zstd": .zstd
        case "gzip": .gzip
        case "br": .brotli
        default: nil
        }
    }

    static func decode(_ body: [UInt8], coding: Coding, limit: Int) -> [UInt8]? {
        switch coding {
        case .identity: body.count <= limit ? body : nil
        case .zstd: zstd(body, limit: limit)
        case .gzip: gzip(body, limit: limit)
        case .brotli: brotli(body, limit: limit)
        }
    }

    /// Every frame of the input, which must be consumed whole.
    static func zstd(_ input: [UInt8], limit: Int) -> [UInt8]? {
        guard !input.isEmpty, let context = ZSTD_createDCtx() else { return nil }
        defer { ZSTD_freeDCtx(context) }
        var output = [UInt8](repeating: 0, count: min(limit + 1, max(4096, 8 * input.count)))
        var produced = 0
        var finished = false
        let ok = input.withUnsafeBytes { source -> Bool in
            var inBuffer = ZSTD_inBuffer(src: source.baseAddress, size: source.count, pos: 0)
            while inBuffer.pos < inBuffer.size || !finished {
                if produced == output.count {
                    guard output.count <= limit else { return false }
                    output.append(contentsOf: repeatElement(0, count: min(output.count, limit + 1 - output.count)))
                }
                let before = inBuffer.pos
                let (status, written) = output.withUnsafeMutableBytes { target -> (Int, Int) in
                    var outBuffer = ZSTD_outBuffer(dst: target.baseAddress! + produced, size: target.count - produced, pos: 0)
                    let status = ZSTD_decompressStream(context, &outBuffer, &inBuffer)
                    return (status, outBuffer.pos)
                }
                guard ZSTD_isError(status) == 0 else { return false }
                produced += written
                guard produced <= limit else { return false }
                finished = status == 0
                // No progress with room left: the input ends inside a frame.
                if written == 0, inBuffer.pos == before, produced < output.count { return false }
            }
            return true
        }
        guard ok, finished else { return nil }
        return Array(output[0..<produced])
    }

    /// A gzip member (RFC 1952) with its CRC and length checked by zlib.
    static func gzip(_ input: [UInt8], limit: Int) -> [UInt8]? {
        guard !input.isEmpty else { return nil }
        var stream = z_stream()
        guard inflateInit2_(&stream, 16 + MAX_WBITS, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else {
            return nil
        }
        defer { inflateEnd(&stream) }
        var output = [UInt8](repeating: 0, count: min(limit + 1, max(4096, 8 * input.count)))
        var status: Int32 = Z_OK
        var source = input
        source.withUnsafeMutableBufferPointer { buffer in
            stream.next_in = buffer.baseAddress
            stream.avail_in = uInt(buffer.count)
            while status == Z_OK {
                let produced = Int(stream.total_out)
                if produced == output.count {
                    guard output.count <= limit else {
                        status = Z_BUF_ERROR
                        return
                    }
                    output.append(contentsOf: repeatElement(0, count: min(output.count, limit + 1 - output.count)))
                }
                output.withUnsafeMutableBufferPointer { target in
                    stream.next_out = target.baseAddress! + produced
                    stream.avail_out = uInt(target.count - produced)
                    status = inflate(&stream, Z_NO_FLUSH)
                }
                if status == Z_BUF_ERROR, stream.avail_in == 0 { return }
                if status == Z_BUF_ERROR { status = Z_OK }
            }
        }
        let produced = Int(stream.total_out)
        guard status == Z_STREAM_END, stream.avail_in == 0, produced <= limit else { return nil }
        return Array(output[0..<produced])
    }

    /// A Brotli stream through Apple's Compression.
    static func brotli(_ input: [UInt8], limit: Int) -> [UInt8]? {
        guard !input.isEmpty else { return nil }
        var stream = compression_stream(dst_ptr: UnsafeMutablePointer(bitPattern: 1)!, dst_size: 0,
                                        src_ptr: UnsafePointer(bitPattern: 1)!, src_size: 0, state: nil)
        guard compression_stream_init(&stream, COMPRESSION_STREAM_DECODE, COMPRESSION_BROTLI) == COMPRESSION_STATUS_OK else {
            return nil
        }
        defer { compression_stream_destroy(&stream) }
        var output = [UInt8](repeating: 0, count: min(limit + 1, max(4096, 8 * input.count)))
        var produced = 0
        var status = COMPRESSION_STATUS_OK
        input.withUnsafeBufferPointer { source in
            stream.src_ptr = source.baseAddress!
            stream.src_size = source.count
            while status == COMPRESSION_STATUS_OK {
                if produced == output.count {
                    guard output.count <= limit else {
                        status = COMPRESSION_STATUS_ERROR
                        return
                    }
                    output.append(contentsOf: repeatElement(0, count: min(output.count, limit + 1 - output.count)))
                }
                output.withUnsafeMutableBufferPointer { target in
                    stream.dst_ptr = target.baseAddress! + produced
                    stream.dst_size = target.count - produced
                    status = compression_stream_process(&stream, Int32(COMPRESSION_STREAM_FINALIZE.rawValue))
                    produced = target.count - stream.dst_size
                }
                // Finalizing with input left and no room: more output is needed, which the loop grows.
                if status == COMPRESSION_STATUS_OK, stream.src_size == 0, produced < output.count { status = COMPRESSION_STATUS_ERROR }
            }
        }
        guard status == COMPRESSION_STATUS_END, produced <= limit else { return nil }
        return Array(output[0..<produced])
    }
}
