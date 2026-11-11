#include "agentspeed/quota/estimate/sign_in.hpp"

#include <string.h>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/presence.hpp"

namespace agentspeed::quota::estimate {

namespace {

void wipe(std::string& text) {
    if (!text.empty()) ::explicit_bzero(text.data(), text.size());
}

void wipe(std::optional<std::string>& text) {
    if (text) wipe(*text);
}

}  // namespace

std::optional<SignedInAccount> signedInAccount(AccountSource source, std::string_view bytes, const Salt& salt) {
    json::Document doc;
    if (!doc.parse(bytes) || !doc.root().isObject()) return std::nullopt;
    const auto root = doc.root();
    std::optional<SignedInAccount> out;
    if (source == AccountSource::Codex) {
        auto id = root["tokens"]["account_id"].nonEmptyString();
        if (id) out = SignedInAccount{accountKey(salt, "codex", *id), {}, std::nullopt, std::nullopt};
        wipe(id);
    } else {
        const auto account = root["oauthAccount"];
        if (!account.isObject()) return std::nullopt;
        auto organization = account["organizationUuid"].nonEmptyString();
        auto user = account["accountUuid"].nonEmptyString();
        if (organization && user) {
            std::string id = *organization + ":" + *user;
            out = SignedInAccount{accountKey(salt, "claude", id), accountKey(salt, "claudeDesktop", *organization),
                                  cleanPlan(account["organizationType"]), cleanPlan(account["organizationRateLimitTier"])};
            wipe(id);
        }
        wipe(organization);
        wipe(user);
    }
    if (out && out->key.empty()) return std::nullopt;
    return out;
}

std::optional<std::string> desktopOrgKey(std::string_view url, const Salt& salt) {
    constexpr std::string_view head = "/api/organizations/";
    const auto at = url.find(head);
    if (at == std::string_view::npos) return std::nullopt;
    const auto rest = url.substr(at + head.size());
    const auto end = rest.find('/');
    if (end == std::string_view::npos || end == 0) return std::nullopt;
    auto key = accountKey(salt, "claudeDesktop", rest.substr(0, end));
    if (key.empty()) return std::nullopt;
    return key;
}

bool AccountFile::refresh(std::int64_t nowMs, bool enabled, const Salt& salt) {
    if (!enabled) {
        stamp_.reset();
        readMs_.reset();
        return replace(std::nullopt);
    }
    const auto stamp = files::stampOf(file_, true);
    if (!stamp) {
        stamp_.reset();
        return replace(std::nullopt);
    }
    if (stamp_ == stamp || (readMs_ && nowMs - *readMs_ < SignInMarker::kRereadAfterMs)) return false;
    readMs_ = nowMs;
    stamp_ = stamp;
    auto read = files::readFile(file_, SignInMarker::kFileLimit, true);
    auto account = read.status == files::ReadStatus::Ok ? signedInAccount(source_, read.bytes, salt) : std::nullopt;
    // The file may hold tokens: nothing of it outlives the read but the keys.
    wipe(read.bytes);
    return replace(std::move(account));
}

bool AccountFile::replace(std::optional<SignedInAccount> next) {
    const auto signature = [](const std::optional<SignedInAccount>& a) {
        return a ? a->key + '|' + a->orgKey + '|' + a->plan.value_or("-") + '|' + a->tier.value_or("-") : std::string();
    };
    const bool changed = signature(next) != signature(account_);
    account_ = std::move(next);
    return changed;
}

}  // namespace agentspeed::quota::estimate
