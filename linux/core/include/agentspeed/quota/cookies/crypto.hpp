#pragma once

#include <optional>
#include <string>
#include <string_view>

// QUOTAS 9.10: Chromium's cookie encryption. `v10`/`v11` AES-128-CBC with a PBKDF2 key (Linux, macOS) and `v10`/`v11`
// AES-256-GCM (Windows, here as a pure function for the fixtures). `v20` cannot be read by a user program. Keys and
// plaintexts only live in memory.
namespace agentspeed::quota::cookies {

// Chrome 130 and later (`meta.version` ≥ 24) put the SHA-256 of `host_key` before the value.
constexpr int kHostHashVersion = 24;
constexpr int kLinuxIterations = 1;
constexpr int kMacIterations = 1003;
// The `v10` password of Chromium on Linux without a keyring.
constexpr std::string_view kLinuxV10Password = "peanuts";

// `v10` or `v11`.
bool isVersioned(std::string_view blob);

// PBKDF2-HMAC-SHA1(password, `saltysalt`, iterations, 16 bytes).
std::optional<std::string> cbcKey(std::string_view password, int iterations);

// A `v10`/`v11` value: AES-128-CBC, IV of 16 spaces, PKCS#7. From `kHostHashVersion` on, the leading SHA-256 of
// `host_key` is dropped, and checked when `host` is given. Nullopt unless the plaintext is UTF-8.
std::optional<std::string> decryptCbc(std::string_view blob, std::string_view key, int metaVersion,
                                      std::optional<std::string_view> host = std::nullopt);

// A Windows `v10`/`v11` value: a 12-byte nonce after the prefix, the ciphertext, then a 16-byte tag.
std::optional<std::string> decryptGcm(std::string_view blob, std::string_view key, int metaVersion,
                                      std::optional<std::string_view> host = std::nullopt);

}  // namespace agentspeed::quota::cookies
