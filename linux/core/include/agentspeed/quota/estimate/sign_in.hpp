#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

#include "agentspeed/quota/estimate/accounts.hpp"
#include "agentspeed/quota/files.hpp"

// QUOTAS 10.1 and 10.2 on this computer: the account keys of the sign-in files of QUOTAS 6 and of Claude Desktop's usage
// entry. The ids are parsed in memory and wiped; only the salted keys, the plan and the tier outlive a read, and none of
// them is logged.
namespace agentspeed::quota::estimate {

struct SignedInAccount {
    std::string key;
    // Claude Code: its organization's `claudeDesktop` key, `organizationType` and `organizationRateLimitTier`.
    std::string orgKey;
    std::optional<std::string> plan, tier;
    bool operator==(const SignedInAccount&) const = default;
};

enum class AccountSource : std::uint8_t {
    Codex,       // auth.json `tokens.account_id`, vendor `codex`
    ClaudeCode,  // .claude.json `oauthAccount`: `<organizationUuid>:<accountUuid>`, vendor `claude`
};
std::optional<SignedInAccount> signedInAccount(AccountSource source, std::string_view bytes, const Salt& salt);

// `https://claude.ai/api/organizations/<uuid>/usage…`: the `claudeDesktop` key of the organization.
std::optional<std::string> desktopOrgKey(std::string_view usageUrl, const Salt& salt);

// One sign-in file, read like SignInMarker: at most once a minute and when its size or modification time changes; the
// bytes are wiped after every read. Not thread-safe.
class AccountFile {
public:
    AccountFile(AccountSource source, std::string file) : source_(source), file_(std::move(file)) {}
    // `enabled` false: nothing is read and the account goes. True when the account changed.
    bool refresh(std::int64_t nowMs, bool enabled, const Salt& salt);
    const std::optional<SignedInAccount>& account() const { return account_; }

private:
    bool replace(std::optional<SignedInAccount> next);

    AccountSource source_;
    std::string file_;
    std::optional<files::Stamp> stamp_;
    std::optional<std::int64_t> readMs_;
    std::optional<SignedInAccount> account_;
};

}  // namespace agentspeed::quota::estimate
