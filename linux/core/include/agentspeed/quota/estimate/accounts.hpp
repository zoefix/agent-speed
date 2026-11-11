#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/json.hpp"

// QUOTAS 10.1 and 10.2: account keys, the fingerprint of two sources, the Desktop merge rule, plan strings and the Codex
// account chains; REPORTING-EXTENSIONS 11.2: an account's tag for one reporting service.
namespace agentspeed::quota::estimate {

using Salt = std::vector<unsigned char>;
inline constexpr std::size_t kInstallIdBytes = 32;
inline constexpr std::string_view kAccountSaltText = "agent-speed quota account v1";
inline constexpr std::string_view kReportTagDomain = "agent-speed quota account|";

// QUOTAS 10.1 accountSalt: the SHA-256 of kAccountSaltText, the same in every install.
const Salt& accountSalt();
// The first 16 lowercase hex digits of HMAC-SHA256(salt, "<vendor>:<id>"); empty when the hash fails.
std::string accountKey(const Salt& salt, std::string_view vendor, std::string_view id);
// QUOTAS 10.1: the key of an account without an id, the pair (machine, source) of one install: vendor `pair`, id
// `<install>:<machine>:<source>` (`machine` is `local` or the host's id).
std::string pairKey(const Salt& salt, std::string_view install, std::string_view machine, std::string_view source);
// REPORTING-EXTENSIONS 11.2 `account`: the first 16 lowercase hex digits of HMAC-SHA256(kReportTagDomain + origin, key),
// `origin` the service's api origin; empty when the hash fails.
std::string reportAccount(std::string_view origin, std::string_view key);
// Lowercase hex (REMOTE 11.1 `salt`: 64 digits).
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
