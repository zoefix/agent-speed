#include "agentspeed/quota/routes/cadence.hpp"

namespace agentspeed::quota {

std::string_view vendorStatusId(VendorStatusKind kind) {
    switch (kind) {
        case VendorStatusKind::Ok: return "ok";
        case VendorStatusKind::NoCredential: return "noCredential";
        case VendorStatusKind::NoBrowser: return "noBrowser";
        case VendorStatusKind::Expired: return "expired";
        case VendorStatusKind::Denied: return "denied";
        case VendorStatusKind::Failed: return "failed";
        case VendorStatusKind::NetworkOff: return "networkOff";
        case VendorStatusKind::NotRunning: return "notRunning";
        case VendorStatusKind::NoCertificates: return "noCertificates";
    }
    return "failed";
}

bool VendorStatus::isProblem() const {
    return kind != VendorStatusKind::Ok && kind != VendorStatusKind::NotRunning && kind != VendorStatusKind::NetworkOff;
}

bool Cadence::isDenied(const CredentialId& credential) const {
    return lastOutcome_ == Outcome::Denied && credential_ == credential;
}

bool Cadence::isDue(std::int64_t nowMs, const CredentialId& credential) const {
    if (!lastRequestMs_ || !credential_ || credential_->account != credential.account) return true;
    const std::int64_t since = nowMs - *lastRequestMs_;
    switch (lastOutcome_.value_or(Outcome::Success)) {
        case Outcome::Denied: return *credential_ != credential && since >= kIntervalMs;
        case Outcome::Failure: return since >= kFailureWaitMs;
        case Outcome::Success: return since >= kIntervalMs;
    }
    return false;
}

bool Cadence::waited(std::int64_t nowMs) const {
    if (!lastRequestMs_) return true;
    const std::int64_t wait = lastOutcome_ == Outcome::Success ? kIntervalMs : kFailureWaitMs;
    return nowMs - *lastRequestMs_ >= wait;
}

void Cadence::record(Outcome outcome, std::int64_t nowMs, const CredentialId& credential) {
    lastRequestMs_ = nowMs;
    lastOutcome_ = outcome;
    credential_ = credential;
}

}  // namespace agentspeed::quota
