#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/json.hpp"

// QUOTAS 10.1 and 10.2: account keys, the fingerprint of two sources, the Desktop merge rule, plan strings and the Codex
// account chains.
namespace agentspeed::quota::estimate {

using Salt = std::vector<unsigned char>;
inline constexpr std::size_t kSaltBytes = 32;

// The first 16 lowercase hex digits of HMAC-SHA256(salt, "<vendor>:<id>"); empty when the hash fails.
std::string accountKey(const Salt& salt, std::string_view vendor, std::string_view id);
// 64 lowercase hex digits (REMOTE 11.1 `salt`).
std::string saltHex(const Salt& salt);

// One observation of a window as the fingerprint compares them: when, which window, its reset and length.
struct WindowPoint {
    std::int64_t t = 0;
    std::string window;
    std::int64_t R = 0;
    std::int64_t minutes = 0;
};

// The comparable pairs of two sources' windows (the same window, both instances live together for more than a minute),
// computed once and asked at any moment: true when the latest pair observed by `at` confirms one account (resets
// within a minute), false when it contradicts it, else the first pair after `at`; nullopt without a pair.
class Fingerprint {
public:
    Fingerprint(const std::vector<WindowPoint>& a, const std::vector<WindowPoint>& b);
    std::optional<bool> at(std::int64_t at) const;

private:
    struct Pair {
        std::int64_t later, earlier;
        bool contradicts;
    };
    std::vector<Pair> pairs_;
};
std::optional<bool> fingerprint(const std::vector<WindowPoint>& a, const std::vector<WindowPoint>& b, std::int64_t at);

// The personal Claude plans (QUOTAS 10.1): a team or enterprise organization is shared by its members.
bool isPersonalClaudePlan(const std::optional<std::string>& organizationType);
// Desktop spends on the CLI's account only when the organizations match, the plan is personal and the fingerprint
// confirms it.
bool desktopSharesCli(const std::optional<std::string>& desktopOrg, const std::optional<std::string>& cliOrg,
                      const std::optional<std::string>& cliOrgType, std::optional<bool> fingerprint);
// Equal keys on two computers are one account unless the fingerprint contradicts it.
inline bool keysShare(std::optional<bool> fingerprint) { return !(fingerprint && !*fingerprint); }

// QUOTAS 10.2: a plan or tier is kept only when it is 1–64 characters of [a-z0-9_.-].
bool isPlanText(std::string_view text);
std::optional<std::string> cleanPlan(const std::optional<std::string>& value);
std::optional<std::string> cleanPlan(const json::Value& value);

// Codex history accounts: window instances named by one token count line belong to one account (union-find).
class Chains {
public:
    std::string find(const std::string& x);
    std::string link(const std::vector<std::string>& instances);

private:
    std::map<std::string, std::string> parent_;
};

}  // namespace agentspeed::quota::estimate
