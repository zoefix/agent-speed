#include "quota_accounts.hpp"

#include <mbedtls/md.h>
#include <string.h>

#include <algorithm>
#include <exception>
#include <random>

#include "agentspeed/quota/codex.hpp"
#include "agentspeed/quota/presence.hpp"

namespace agentspeed::relay {

namespace {

constexpr std::size_t kKeyDigits = 16;
constexpr std::size_t kPlanCharacters = 64;

// Only tells this process's accounts apart: without a random source it stays zero, which still does.
const Salt& processSalt() {
    static const Salt salt = [] {
        Salt bytes{};
        try {
            std::random_device device;
            for (auto& b : bytes) b = static_cast<unsigned char>(device() & 0xFF);
        } catch (const std::exception&) {
        }
        return bytes;
    }();
    return salt;
}

void wipe(std::string& text) {
    if (!text.empty()) ::explicit_bzero(text.data(), text.size());
}

void wipe(std::optional<std::string>& text) {
    if (text) wipe(*text);
}

std::optional<std::string> hmacKey(const Salt& salt, std::string_view message) {
    const auto* sha256 = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
    unsigned char digest[32];
    if (!sha256 || mbedtls_md_hmac(sha256, salt.data(), salt.size(), reinterpret_cast<const unsigned char*>(message.data()), message.size(),
                                   digest) != 0) {
        return std::nullopt;
    }
    static constexpr char kHex[] = "0123456789abcdef";
    std::string key(kKeyDigits, '0');
    for (std::size_t i = 0; i < kKeyDigits / 2; ++i) {
        key[2 * i] = kHex[digest[i] >> 4];
        key[2 * i + 1] = kHex[digest[i] & 0x0F];
    }
    ::explicit_bzero(digest, sizeof digest);
    return key;
}

// `<vendor>:<parts joined by ':'>` in one buffer, so no copy of the id is left behind; wiped after the hash.
std::optional<std::string> keyOf(const Salt& salt, std::string_view vendor, std::initializer_list<std::string_view> parts) {
    std::size_t size = vendor.size();
    for (const auto part : parts) size += 1 + part.size();
    std::string message;
    message.reserve(size);
    message.append(vendor);
    for (const auto part : parts) message.append(1, ':').append(part);
    auto key = hmacKey(salt, message);
    wipe(message);
    return key;
}

}  // namespace

std::optional<std::string> accountKey(const Salt& salt, std::string_view vendor, std::string_view id) { return keyOf(salt, vendor, {id}); }

bool isAccountKey(std::string_view text) {
    return text.size() == kKeyDigits && std::all_of(text.begin(), text.end(), [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); });
}

bool isPlanText(std::string_view text) {
    const auto allowed = [](char c) { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-'; };
    return !text.empty() && text.size() <= kPlanCharacters && std::all_of(text.begin(), text.end(), allowed);
}

std::optional<std::string> cleanPlan(const json::Value& value) {
    auto text = value.string();
    if (!text || !isPlanText(*text)) return std::nullopt;
    return text;
}

AccountFacts accountFacts(AccountKind kind, std::string_view bytes, const std::optional<Salt>& salt) {
    AccountFacts facts;
    json::Document doc;
    if (!doc.parse(bytes) || !doc.root().isObject()) return facts;
    const auto root = doc.root();
    switch (kind) {
        case AccountKind::Codex: {
            auto id = root["tokens"]["account_id"].nonEmptyString();
            if (!id) break;
            if (salt) facts.account = keyOf(*salt, "codex", {*id});
            facts.identity = keyOf(processSalt(), "codex", {*id});
            wipe(id);
            break;
        }
        case AccountKind::ClaudeCode: {
            const auto account = root["oauthAccount"];
            if (!account.isObject()) break;
            auto organization = account["organizationUuid"].nonEmptyString();
            auto user = account["accountUuid"].nonEmptyString();
            if (organization && user) {
                if (salt) facts.account = keyOf(*salt, "claude", {*organization, *user});
                facts.identity = keyOf(processSalt(), "claude", {*organization, *user});
            }
            wipe(organization);
            wipe(user);
            facts.plan = cleanPlan(account["organizationType"]);
            facts.tier = cleanPlan(account["organizationRateLimitTier"]);
            break;
        }
    }
    return facts;
}

void AccountFile::refresh(std::int64_t nowMs, bool enabled, const std::optional<Salt>& salt) {
    if (!enabled) {
        stamp_.reset();
        readMs_.reset();
        facts_ = {};
        return;
    }
    const auto stamp = quota::files::stampOf(file_, true);
    if (!stamp) {
        stamp_.reset();
        facts_ = {};
        return;
    }
    if (salt == salt_ && (stamp_ == stamp || (readMs_ && nowMs - *readMs_ < quota::SignInMarker::kRereadAfterMs))) return;
    readMs_ = nowMs;
    stamp_ = stamp;
    salt_ = salt;
    ++reads_;
    auto read = quota::files::readFile(file_, quota::SignInMarker::kFileLimit, true);
    facts_ = read.status == quota::files::ReadStatus::Ok ? accountFacts(kind_, read.bytes, salt) : AccountFacts{};
    // The file may hold tokens: nothing of it outlives the read but the facts.
    wipe(read.bytes);
}

void CodexPlans::observe(const json::Value& rateLimits, std::int64_t timestampMs) {
    if (quota::codexWindows(rateLimits, timestampMs).empty()) return;
    auto plan = cleanPlan(rateLimits["plan_type"]);
    const std::lock_guard<std::mutex> lock(mutex_);
    if (!enabled_) return;
    if (!newest_ || timestampMs > newest_->observedMs) newest_ = Seen{timestampMs, plan};
    if (!plan) return;
    const auto latest = latest_.find(account_);
    if (latest == latest_.end()) {
        latest_.emplace(account_, Seen{timestampMs, std::move(plan)});
    } else if (timestampMs >= latest->second.observedMs) {
        latest->second = Seen{timestampMs, std::move(plan)};
    }
}

void CodexPlans::setAccount(const std::optional<std::string>& identity) {
    const std::lock_guard<std::mutex> lock(mutex_);
    account_ = identity;
}

void CodexPlans::setEnabled(bool enabled) {
    const std::lock_guard<std::mutex> lock(mutex_);
    enabled_ = enabled;
    if (!enabled) newest_.reset();
}

std::optional<std::string> CodexPlans::plan(std::int64_t observedMs) const {
    const std::lock_guard<std::mutex> lock(mutex_);
    if (newest_ && newest_->observedMs == observedMs && newest_->plan) return newest_->plan;
    const auto latest = latest_.find(account_);
    if (latest == latest_.end()) return std::nullopt;
    return latest->second.plan;
}

}  // namespace agentspeed::relay
