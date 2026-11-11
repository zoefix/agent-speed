#include "agentspeed/quota/estimate/accounts.hpp"

#include <openssl/evp.h>
#include <openssl/hmac.h>
#include <string.h>

#include <algorithm>
#include <cstdlib>
#include <tuple>

#include "agentspeed/quota/estimate/model.hpp"

namespace agentspeed::quota::estimate {

namespace {

constexpr char kHex[] = "0123456789abcdef";

std::string hex(const unsigned char* bytes, std::size_t count) {
    std::string out(count * 2, '0');
    for (std::size_t i = 0; i < count; ++i) {
        out[2 * i] = kHex[bytes[i] >> 4];
        out[2 * i + 1] = kHex[bytes[i] & 0x0F];
    }
    return out;
}

}  // namespace

std::string accountKey(const Salt& salt, std::string_view vendor, std::string_view id) {
    std::string message;
    message.reserve(vendor.size() + 1 + id.size());
    message.append(vendor).append(1, ':').append(id);
    unsigned char digest[EVP_MAX_MD_SIZE];
    unsigned int length = 0;
    const auto* result = HMAC(EVP_sha256(), salt.data(), static_cast<int>(salt.size()), reinterpret_cast<const unsigned char*>(message.data()),
                              message.size(), digest, &length);
    ::explicit_bzero(message.data(), message.size());
    if (!result || length < 8) return {};
    auto key = hex(digest, 8);
    ::explicit_bzero(digest, sizeof digest);
    return key;
}

std::string saltHex(const Salt& salt) { return hex(salt.data(), salt.size()); }

Fingerprint::Fingerprint(const std::vector<WindowPoint>& a, const std::vector<WindowPoint>& b) {
    for (const auto& x : a) {
        for (const auto& y : b) {
            if (y.window != x.window) continue;
            const auto overlap = std::min(x.R, y.R) - std::max(x.R - x.minutes * kMinute, y.R - y.minutes * kMinute);
            if (overlap <= kFingerprintMs) continue;
            const bool same = std::llabs(x.R - y.R) <= kFingerprintMs;
            pairs_.push_back({std::max(x.t, y.t), std::min(x.t, y.t), !same});
        }
    }
}

std::optional<bool> Fingerprint::at(std::int64_t at) const {
    if (pairs_.empty()) return std::nullopt;
    const Pair* latest = nullptr;
    for (const auto& p : pairs_) {
        if (p.later > at) continue;
        if (!latest || std::tie(p.later, p.earlier, p.contradicts) > std::tie(latest->later, latest->earlier, latest->contradicts)) latest = &p;
    }
    if (latest) return !latest->contradicts;
    const Pair* first = nullptr;
    for (const auto& p : pairs_) {
        const bool confirms = !p.contradicts;
        if (!first || std::make_tuple(p.later, p.earlier, confirms) < std::make_tuple(first->later, first->earlier, !first->contradicts)) first = &p;
    }
    return !first->contradicts;
}

std::optional<bool> fingerprint(const std::vector<WindowPoint>& a, const std::vector<WindowPoint>& b, std::int64_t at) {
    return Fingerprint(a, b).at(at);
}

bool isPersonalClaudePlan(const std::optional<std::string>& organizationType) {
    if (!organizationType) return false;
    for (const auto* plan : {"claude_free", "claude_pro", "claude_max", "claude_ultra"}) {
        if (*organizationType == plan) return true;
    }
    return false;
}

bool desktopSharesCli(const std::optional<std::string>& desktopOrg, const std::optional<std::string>& cliOrg,
                      const std::optional<std::string>& cliOrgType, std::optional<bool> fingerprint) {
    return desktopOrg && !desktopOrg->empty() && desktopOrg == cliOrg && isPersonalClaudePlan(cliOrgType) && fingerprint && *fingerprint;
}

bool isPlanText(std::string_view text) {
    const auto allowed = [](char c) { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-'; };
    return !text.empty() && text.size() <= 64 && std::all_of(text.begin(), text.end(), allowed);
}

std::optional<std::string> cleanPlan(const std::optional<std::string>& value) {
    if (!value || !isPlanText(*value)) return std::nullopt;
    return value;
}

std::optional<std::string> cleanPlan(const json::Value& value) { return cleanPlan(value.string()); }

std::string Chains::find(const std::string& start) {
    auto x = start;
    parent_.try_emplace(x, x);
    while (parent_[x] != x) {
        parent_[x] = parent_[parent_[x]];
        x = parent_[x];
    }
    return x;
}

std::string Chains::link(const std::vector<std::string>& instances) {
    std::vector<std::string> roots;
    roots.reserve(instances.size());
    for (const auto& i : instances) roots.push_back(find(i));
    for (std::size_t k = 1; k < roots.size(); ++k) parent_[find(roots[k])] = find(roots[0]);
    return find(roots[0]);
}

}  // namespace agentspeed::quota::estimate
