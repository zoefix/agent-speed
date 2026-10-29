import CommonCrypto
import Darwin
import Foundation
import SQLite3

/// A bounded, authenticated snapshot of Trae's SQLCipher 4 store. Decrypted bytes never leave memory.
/// The raw key identifies Trae's storage format; it is not an account credential.
enum TraeSnapshot {
    static let pageSize = 4096
    static let maxBytes = 256 * 1024 * 1024
    private static let key: [UInt8] = stride(from: 0, to: 64, by: 2).map { offset in
        let hex = Array("3605f6691095a993f03d5009c918352ef5be31ae31e8f000212b81ff058da773")
        return UInt8(String(hex[offset..<offset + 2]), radix: 16)!
    }
    enum Error: Swift.Error { case unreadable, changed, unsupported, authentication, oversized }

    static func read(path: String) throws -> Data {
        let paths = [path, path + "-wal", path + "-journal"]
        let before = paths.map(FileStat.lstat)
        guard before[0]?.isRegularFile == true else { throw Error.unreadable }
        if let journal = before[2], journal.size > 0,
           try readFile(path + "-journal", prefix: 8).contains(where: { $0 != 0 }) { throw Error.changed }
        var bytes = try readFile(path)
        let wal = before[1] == nil ? Data() : try readFile(path + "-wal")
        guard before == paths.map(FileStat.lstat) else { throw Error.changed }
        guard bytes.count >= pageSize, bytes.count % pageSize == 0 else { throw Error.unsupported }
        let plaintext = bytes.starts(with: Data("SQLite format 3\0".utf8))
        if !wal.isEmpty { bytes = try overlay(wal: wal, database: bytes) }
        if !plaintext {
            let salt = Array(bytes.prefix(16)).map { $0 ^ 0x3a }
            var hmacKey = [UInt8](repeating: 0, count: 32)
            let status = key.withUnsafeBytes { keyBytes in
                CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2), keyBytes.baseAddress!.assumingMemoryBound(to: Int8.self),
                                    key.count, salt, salt.count, CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA512), 2, &hmacKey, hmacKey.count)
            }
            guard status == kCCSuccess else { throw Error.authentication }
            var decoded = Data(count: bytes.count)
            for offset in stride(from: 0, to: bytes.count, by: pageSize) {
                let page = Array(bytes[offset..<offset + pageSize])
                let pageNumber = UInt32(offset / pageSize + 1)
                let plain = try decrypt(page, number: pageNumber, hmacKey: hmacKey)
                decoded.replaceSubrange(offset..<offset + pageSize, with: plain)
            }
            bytes = decoded
        }
        guard bytes.starts(with: Data("SQLite format 3\0".utf8)), bytes[16] == 16, bytes[17] == 0 else { throw Error.unsupported }
        // A standalone in-memory image has no WAL; retaining WAL mode would make SQLite seek a disk sidecar.
        bytes[18] = 1
        bytes[19] = 1
        return bytes
    }

    private static func readFile(_ path: String, prefix: Int? = nil) throws -> Data {
        let fd = Darwin.open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK)
        guard fd >= 0 else { throw Error.unreadable }
        defer { Darwin.close(fd) }
        guard let stat = FileStat.fstat(fd), stat.isRegularFile, stat.size >= 0 else { throw Error.unreadable }
        guard prefix != nil || stat.size <= maxBytes else { throw Error.oversized }
        var bytes = Data(count: Int(min(stat.size, Int64(prefix ?? maxBytes))))
        let count = bytes.withUnsafeMutableBytes { buffer -> Int in
            var filled = 0
            while filled < buffer.count {
                let n = Darwin.pread(fd, buffer.baseAddress! + filled, buffer.count - filled, off_t(filled))
                if n < 0 && errno == EINTR { continue }
                guard n > 0 else { break }
                filled += n
            }
            return filled
        }
        guard count == bytes.count, FileStat.fstat(fd) == stat else { throw Error.changed }
        return bytes
    }

    private static func decrypt(_ page: [UInt8], number: UInt32, hmacKey: [UInt8]) throws -> [UInt8] {
        let start = number == 1 ? 16 : 0
        let cipher = Array(page[start..<4016]), iv = Array(page[4016..<4032])
        var authenticated = Array(page[start..<4032])
        authenticated += (0..<4).map { UInt8(truncatingIfNeeded: number >> (8 * $0)) }
        var digest = [UInt8](repeating: 0, count: 64)
        CCHmac(CCHmacAlgorithm(kCCHmacAlgSHA512), hmacKey, hmacKey.count, authenticated, authenticated.count, &digest)
        var difference: UInt8 = 0
        for index in 0..<64 { difference |= digest[index] ^ page[4032 + index] }
        guard difference == 0 else { throw Error.authentication }
        var plain = [UInt8](repeating: 0, count: cipher.count)
        var written = 0
        let status = CCCrypt(CCOperation(kCCDecrypt), CCAlgorithm(kCCAlgorithmAES), 0, key, key.count, iv,
                             cipher, cipher.count, &plain, plain.count, &written)
        guard status == kCCSuccess, written == cipher.count else { throw Error.authentication }
        var output = number == 1 ? Array("SQLite format 3\0".utf8) : []
        output += plain
        output += Array(repeating: 0, count: 80)
        return output
    }

    /// Apply only complete committed WAL transactions, validating the SQLite header and rolling frame checksums.
    static func overlay(wal: Data, database: Data) throws -> Data {
        guard wal.count >= 32 else { throw Error.changed }
        let magic = word(wal, 0)
        guard magic == 0x377f0682 || magic == 0x377f0683, word(wal, 4) == 3_007_000,
              word(wal, 8) == UInt32(pageSize) else { throw Error.unsupported }
        let little = magic == 0x377f0682
        var checksum = checksum(wal, range: 0..<24, little: little, seed: (0, 0))
        guard checksum == (word(wal, 24), word(wal, 28)) else { throw Error.authentication }
        var pending: [UInt32: Data] = [:]
        var committed: [UInt32: Data] = [:]
        var databasePages = database.count / pageSize
        let salt = wal[16..<24]
        var offset = 32
        while offset + 24 + pageSize <= wal.count {
            let page = word(wal, offset), count = word(wal, offset + 4)
            guard page > 0, page <= maxBytes / pageSize, count <= maxBytes / pageSize,
                  wal[offset + 8..<offset + 16] == salt else { break }
            let first = Self.checksum(wal, range: offset..<offset + 8, little: little, seed: checksum)
            let next = Self.checksum(wal, range: offset + 24..<offset + 24 + pageSize, little: little, seed: first)
            guard next == (word(wal, offset + 16), word(wal, offset + 20)) else { break }
            checksum = next
            pending[page] = Data(wal[offset + 24..<offset + 24 + pageSize])
            if count > 0 {
                committed.merge(pending) { _, new in new }
                pending.removeAll(keepingCapacity: true)
                databasePages = Int(count)
                committed = committed.filter { $0.key <= count }
            }
            offset += 24 + pageSize
        }
        var output = Data(database.prefix(databasePages * pageSize))
        if output.count < databasePages * pageSize { output.append(Data(count: databasePages * pageSize - output.count)) }
        for (page, data) in committed where page <= databasePages {
            let start = (Int(page) - 1) * pageSize
            output.replaceSubrange(start..<start + pageSize, with: data)
        }
        return output
    }

    private static func word(_ data: Data, _ offset: Int, little: Bool = false) -> UInt32 {
        let bytes = Array(data[offset..<offset + 4])
        return little ? UInt32(bytes[0]) | UInt32(bytes[1]) << 8 | UInt32(bytes[2]) << 16 | UInt32(bytes[3]) << 24
            : UInt32(bytes[3]) | UInt32(bytes[2]) << 8 | UInt32(bytes[1]) << 16 | UInt32(bytes[0]) << 24
    }

    private static func checksum(_ data: Data, range: Range<Int>, little: Bool, seed: (UInt32, UInt32)) -> (UInt32, UInt32) {
        var (a, b) = seed
        for offset in stride(from: range.lowerBound, to: range.upperBound, by: 8) {
            a = a &+ word(data, offset, little: little) &+ b
            b = b &+ word(data, offset + 4, little: little) &+ a
        }
        return (a, b)
    }
}

extension SQLiteConnection {
    /// SQLite owns and releases this read-only memory image; no plaintext database is written to disk.
    static func snapshot(_ data: Data) throws -> SQLiteConnection {
        let connection = try SQLiteConnection(path: ":memory:", mode: .readWriteCreate)
        guard let bytes = sqlite3_malloc64(UInt64(data.count)) else { throw TraeSnapshot.Error.oversized }
        data.copyBytes(to: bytes.assumingMemoryBound(to: UInt8.self), count: data.count)
        let status = sqlite3_deserialize(connection.handle, "main", bytes.assumingMemoryBound(to: UInt8.self),
                                         Int64(data.count), Int64(data.count), UInt32(SQLITE_DESERIALIZE_FREEONCLOSE | SQLITE_DESERIALIZE_READONLY))
        guard status == SQLITE_OK else {
            // FREEONCLOSE also releases the buffer when deserialize fails.
            throw connection.error(status)
        }
        try connection.execute("PRAGMA query_only = 1")
        return connection
    }
}
