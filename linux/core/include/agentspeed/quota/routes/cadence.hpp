#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

#include "agentspeed/quota/routes/fingerprint.hpp"

// QUOTAS 9.1 Cadence and NETWORK 9 When, and the status line of DESIGN 5 (額度, phase 2; REMOTE 11.1 `status`).
namespace agentspeed::quota {

enum class VendorStatusKind : std::uint8_t {
    Ok,              // the last request (or read) succeeded at `okMs`
    NoCredential,    // the agent is not signed in here
    NoBrowser,       // no browser on this computer has the site's session
    Expired,         // the token's own expiry has passed: the agent has to renew it
    Denied,          // the vendor answered 401 or 403 (`host`); nothing is asked until the credential changes
    Failed,
    NetworkOff,      // `networkEnabled` is off
    NotRunning,      // nothing asks yet: the agent is not running and the dashboard is closed
    NoCertificates,  // a remote host has no CA certificates (REMOTE 11.4)
};
// `ok`, `noCredential`, `noBrowser`, `expired`, `denied`, `failed`, `networkOff`, `notRunning`, `noCertificates`.
std::string_view vendorStatusId(VendorStatusKind kind);

struct VendorStatus {
    VendorStatusKind kind = VendorStatusKind::Ok;
    std::int64_t okMs = 0;  // Ok
    std::string host;       // Denied (and failures, for the log)
    bool operator==(const VendorStatus&) const = default;
    // Problems show in the warning style (DESIGN 5).
    bool isProblem() const;
};

// One request's credential: the account it belongs to and, for a file, the file's version (a new modification time
// is a new credential for a 401/403, the same account for its numbers).
struct CredentialId {
    Fingerprint account;
    std::int64_t version = 0;
    bool operator==(const CredentialId&) const = default;
};

// At most every 5 minutes, 15 after a failure, and after 401/403 not again until the credential changes; a new
// account may ask at once.
class Cadence {
public:
    static constexpr std::int64_t kIntervalMs = 5 * 60'000;
    static constexpr std::int64_t kFailureWaitMs = 15 * 60'000;

    enum class Outcome : std::uint8_t { Success, Failure, Denied };

    bool isDenied(const CredentialId& credential) const;
    bool isDue(std::int64_t nowMs, const CredentialId& credential) const;
    // The wait since the last request, whatever the credential (routes that only look when a request is due).
    bool waited(std::int64_t nowMs) const;
    void record(Outcome outcome, std::int64_t nowMs, const CredentialId& credential);

    const std::optional<std::int64_t>& lastRequestMs() const { return lastRequestMs_; }
    const std::optional<Outcome>& lastOutcome() const { return lastOutcome_; }
    const std::optional<CredentialId>& credential() const { return credential_; }

private:
    std::optional<std::int64_t> lastRequestMs_;
    std::optional<Outcome> lastOutcome_;
    std::optional<CredentialId> credential_;
};

}  // namespace agentspeed::quota
