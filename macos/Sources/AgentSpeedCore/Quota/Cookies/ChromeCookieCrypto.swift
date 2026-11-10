import CommonCrypto
import CryptoKit
import Foundation

/// Chromium's cookie encryption (QUOTAS 9.10): `v10`/`v11` AES-128-CBC with a PBKDF2 key on macOS and Linux, and
/// `v10`/`v11` AES-256-GCM with the profile's DPAPI-wrapped key on Windows (kept here as pure functions for the
/// fixtures). `v20` app-bound values cannot be read by a user program. Keys and plaintexts only live in memory.
public enum ChromeCookieCrypto {
    /// Chrome 130 and later (`meta.version` ≥ 24) put the SHA-256 of `host_key` before the value.
    static let hostHashVersion = 24
    static let macIterations = 1003
    static let linuxIterations = 1
    static let salt = Array("saltysalt".utf8)

    /// PBKDF2-HMAC-SHA1(password, `saltysalt`, iterations, 16 bytes).
    static func cbcKey(password: [UInt8], iterations: Int) -> [UInt8]? {
        guard iterations > 0 else { return nil }
        var key = [UInt8](repeating: 0, count: kCCKeySizeAES128)
        let status = password.withUnsafeBufferPointer { secret in
            secret.withMemoryRebound(to: CChar.self) { chars in
                CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2), chars.baseAddress, chars.count, salt, salt.count,
                                     CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA1), UInt32(iterations), &key, key.count)
            }
        }
        return status == kCCSuccess ? key : nil
    }

    /// A `v10` or `v11` value: AES-128-CBC with an IV of 16 spaces and PKCS#7 padding.
    static func decryptCBC(_ blob: [UInt8], key: [UInt8], metaVersion: Int) -> String? {
        guard blob.count > 3, isVersioned(blob), key.count == kCCKeySizeAES128 else { return nil }
        let body = Array(blob[3...])
        guard !body.isEmpty, body.count % kCCBlockSizeAES128 == 0 else { return nil }
        let iv = [UInt8](repeating: 0x20, count: kCCBlockSizeAES128)
        var out = [UInt8](repeating: 0, count: body.count + kCCBlockSizeAES128)
        var moved = 0
        let status = CCCrypt(CCOperation(kCCDecrypt), CCAlgorithm(kCCAlgorithmAES), CCOptions(kCCOptionPKCS7Padding),
                             key, key.count, iv, body, body.count, &out, out.count, &moved)
        defer { out.resetBytes() }
        guard status == kCCSuccess else { return nil }
        return plaintext(Array(out[0..<moved]), metaVersion: metaVersion)
    }

    /// A Windows `v10` or `v11` value: a 12-byte nonce after the prefix, the ciphertext, then a 16-byte tag.
    static func decryptGCM(_ blob: [UInt8], key: [UInt8], metaVersion: Int) -> String? {
        guard blob.count >= 3 + 12 + 16, isVersioned(blob), key.count == 32 else { return nil }
        let nonce = blob[3..<15], tag = blob[(blob.count - 16)...], cipher = blob[15..<(blob.count - 16)]
        guard let box = try? AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: nonce), ciphertext: cipher, tag: tag),
              let opened = try? AES.GCM.open(box, using: SymmetricKey(data: key)) else { return nil }
        return plaintext([UInt8](opened), metaVersion: metaVersion)
    }

    static func isVersioned(_ blob: [UInt8]) -> Bool {
        blob.count >= 3 && blob[0] == UInt8(ascii: "v") && blob[1] == UInt8(ascii: "1")
            && (blob[2] == UInt8(ascii: "0") || blob[2] == UInt8(ascii: "1"))
    }

    private static func plaintext(_ bytes: [UInt8], metaVersion: Int) -> String? {
        let value = metaVersion >= hostHashVersion ? Array(bytes.dropFirst(32)) : bytes
        guard metaVersion < hostHashVersion || bytes.count >= 32 else { return nil }
        return String(bytes: value, encoding: .utf8)
    }
}

private extension Array where Element == UInt8 {
    mutating func resetBytes() {
        for index in indices { self[index] = 0 }
    }
}
