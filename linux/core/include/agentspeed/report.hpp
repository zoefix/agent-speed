#pragma once

#include <array>
#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/json.hpp"

// The Agent Speed Live Report API, client side (spec/REPORTING.md, METRICS 11; spec/tools/report_reference.py):
// ES256 keys and signatures, the canonical request string, usernames and the discovery document.
namespace agentspeed::report {

constexpr std::string_view kLabel = "AGENTSPEED-V1";

std::string b64url(std::string_view bytes);
// Strict unpadded base64url: other alphabets, padding and non-zero trailing bits are rejected.
std::optional<std::string> b64urlDecode(std::string_view text);
std::string sha256Hex(std::string_view bytes);

// ---- keys and signatures (REPORTING 5.1)

struct PublicKey {
    std::array<std::uint8_t, 32> x{}, y{};
    bool operator==(const PublicKey&) const = default;
};

// A P-256 private key: the 32-byte scalar d.
struct PrivateKey {
    std::array<std::uint8_t, 32> d{};
    static PrivateKey generate();
    static std::optional<PrivateKey> fromHex(std::string_view hex);
    std::string hex() const;
    PublicKey publicKey() const;
};

// ES256 over the message bytes (SHA-256 inside), IEEE P1363 r || s (64 bytes).
std::string sign(const PrivateKey& key, std::string_view message);
bool verify(const PublicKey& key, std::string_view message, std::string_view signature);

std::string jwk(const PublicKey& key);  // compact JSON
// A validated public key: kty EC, crv P-256, canonical 43-character coordinates, on the curve, no d.
std::optional<PublicKey> parseJwk(const json::Value& value);
std::string spki(const PublicKey& key);        // DER bytes
std::string thumbprint(const PublicKey& key);  // RFC 7638, base64url

// ---- signing (REPORTING 5.2, 5.3)

std::string authorityOf(std::string_view url);
std::string pathOf(std::string_view url);
std::string canonicalString(std::string_view method, std::string_view authority, std::string_view path, std::string_view timestamp,
                            std::string_view nonce, std::string_view deviceId, std::string_view body);
std::string newNonce();
std::string newDeviceId();  // a random UUID v4

struct SignOptions {
    std::int64_t timestamp = 0;  // Unix seconds
    std::string nonce;           // empty: a new one
    std::string apiKey;
    std::string version = "0.0.0";
    std::string platform = "Linux";
    const PrivateKey* newKey = nullptr;  // a pending rotated key (REPORTING 4.5)
};
using Headers = std::vector<std::pair<std::string, std::string>>;
Headers signedHeaders(const PrivateKey& key, std::string_view method, std::string_view url, std::string_view deviceId,
                      std::string_view body, const SignOptions& options);

enum class Verdict { Valid, Invalid, Malformed };
// REPORTING 5.4 as a service checks one signature header (lower-case names in `headers`).
Verdict verifyRequest(const PublicKey& key, std::string_view method, std::string_view authority, std::string_view path,
                      const std::map<std::string, std::string>& headers, std::string_view body,
                      std::string_view header = "x-agentspeed-signature");

// ---- usernames (REPORTING 4.3)

struct UsernameRules {
    int minLength = 2, maxLength = 32;
    bool ascii = false;  // charset
    bool spaces = true;
};
// The first failing reason in REPORTING 4.3 order (characters, length, spaces, start), or nullopt when valid.
std::optional<std::string> validateUsername(std::string_view name, const UsernameRules& rules);
std::optional<std::string> fitUsername(std::string_view computerName, const UsernameRules& rules);
std::string randomUsername();
std::string usernameKey(std::string_view name);

// ---- discovery (REPORTING 2.2, 3.2)

struct Limits {
    int intervalSeconds = 5, heartbeatSeconds = 60, maxAgeSeconds = 30, maxBodyBytes = 16384, maxModels = 16;
};
struct StreamSettings {
    int minIntervalMs = 50, maxInFlight = 8, ackTimeoutMs = 2000, maxMessageBytes = 65536, pingIntervalSeconds = 20,
        pongTimeoutSeconds = 10;
};
struct Discovery {
    std::vector<std::int64_t> versions;
    std::string name;
    std::optional<std::string> description, contact, terms, privacy, homepage, leaderboard;
    std::optional<std::int64_t> retentionDays;
    std::string api;
    std::string apiKeyMode = "none";
    std::optional<std::string> apiKeyUrl;
    UsernameRules username;
    Limits limits;
    std::vector<std::string> sections;
    std::map<std::string, int> extensions;
    std::optional<StreamSettings> websocket;
    bool has(std::string_view extension) const { return extensions.contains(std::string(extension)); }
};
// nullopt with `field` naming the first offending field when the document is invalid.
std::optional<Discovery> parseDiscovery(const json::Value& document, std::string_view fetchedFrom, std::string* field = nullptr);
std::string websocketUrl(std::string_view api);

}  // namespace agentspeed::report
