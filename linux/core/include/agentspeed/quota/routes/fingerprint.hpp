#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <utility>

// QUOTAS 9.1 Credentials: what is kept of a credential (its fingerprint) and how its bytes are dropped.
namespace agentspeed::quota {

// The first 8 bytes of the SHA-256 of a credential, big-endian: a snapshot belongs to it, so another account drops
// the old numbers, and a refused credential is not sent again until it changes. The credential itself is never kept.
struct Fingerprint {
    std::uint64_t value = 0;
    bool operator==(const Fingerprint&) const = default;
    auto operator<=>(const Fingerprint&) const = default;
};
Fingerprint fingerprintOf(std::string_view credential);

std::string sha256(std::string_view bytes);

// Overwrites the bytes before they are released (OPENSSL_cleanse), then empties the string.
void wipe(std::string& secret);

// A credential for one request. Its bytes are wiped when it goes.
class Secret {
public:
    Secret() = default;
    explicit Secret(std::string value) : value_(std::move(value)) {}
    ~Secret() { wipe(value_); }
    Secret(const Secret&) = delete;
    Secret& operator=(const Secret&) = delete;
    Secret(Secret&& other) noexcept : value_(std::move(other.value_)) { wipe(other.value_); }
    Secret& operator=(Secret&& other) noexcept {
        if (this != &other) {
            wipe(value_);
            value_ = std::move(other.value_);
            wipe(other.value_);
        }
        return *this;
    }
    const std::string& value() const { return value_; }
    bool empty() const { return value_.empty(); }
    Fingerprint fingerprint() const { return fingerprintOf(value_); }

private:
    std::string value_;
};

// Printable ASCII only (space allowed), at most 8 KiB, not empty: a credential never breaks a header.
bool headerSafe(std::string_view value);

}  // namespace agentspeed::quota
