#pragma once

#include <cstdint>
#include <map>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/files.hpp"
#include "protocol.hpp"

// QUOTAS 10.1 and 10.2 on a host (REMOTE 11.1): the account key, plan and tier of Codex and Claude Code. The ids come
// from the sign-in files of QUOTAS 6, parsed in memory; only the salted key, the plan and the tier outlive a read, and
// neither they, the salt nor an id is ever logged.
namespace agentspeed::relay {

// The first 16 lowercase hex digits of HMAC-SHA256(salt, "<vendor>:<id>"); nullopt if the hash fails.
std::optional<std::string> accountKey(const Salt& salt, std::string_view vendor, std::string_view id);
bool isAccountKey(std::string_view text);
// QUOTAS 10.2: 1–64 characters of [a-z0-9_.-].
bool isPlanText(std::string_view text);
// A plan or tier as QUOTAS 10.2 keeps it: a string that is plan text, else nullopt.
std::optional<std::string> cleanPlan(const json::Value& value);

struct AccountFacts {
    std::optional<std::string> account, plan, tier;
    // The id keyed with a random salt of this process: tells accounts apart with or without the app's salt. Never sent.
    std::optional<std::string> identity;
    bool operator==(const AccountFacts&) const = default;
};

enum class AccountKind : std::uint8_t {
    Codex,       // auth.json `tokens.account_id`, vendor `codex`; the plan comes with the rate limits (CodexPlans)
    ClaudeCode,  // .claude.json `oauthAccount`: `<organizationUuid>:<accountUuid>`, vendor `claude`, with
                 // `organizationType` and `organizationRateLimitTier`
};
// One file's facts; the account needs both the salt and an id, the identity an id.
AccountFacts accountFacts(AccountKind kind, std::string_view bytes, const std::optional<Salt>& salt);

// One sign-in file, read like quota::SignInMarker: at most once a minute and when its size or modification time
// changes, and again when the salt changes; the bytes are wiped after every read. Not thread-safe.
class AccountFile {
public:
    AccountFile(AccountKind kind, std::string file) : kind_(kind), file_(std::move(file)) {}

    // `enabled` false (the source's quota off): nothing is read and the facts go.
    void refresh(std::int64_t nowMs, bool enabled, const std::optional<Salt>& salt);
    const AccountFacts& facts() const { return facts_; }
    int reads() const { return reads_; }

private:
    AccountKind kind_;
    std::string file_;
    std::optional<quota::files::Stamp> stamp_;
    std::optional<std::int64_t> readMs_;
    std::optional<Salt> salt_;
    AccountFacts facts_;
    int reads_ = 0;
};

// QUOTAS 10.2 for Codex on a host: the `plan_type` of the rate limits behind the collector's snapshot, else the newest
// non-null one seen for the same account in this session (newer Codex builds write null). Fed beside the collector with
// its rule: a line with a window and a newer time gives the snapshot. Thread-safe, like the collector.
class CodexPlans {
public:
    void observe(const json::Value& rateLimits, std::int64_t timestampMs);
    // The account in force by its identity (AccountFacts; nullopt: none); the lines that follow count for it.
    void setAccount(const std::optional<std::string>& identity);
    // Off, as the collector: lines are dropped and the snapshot's plan goes.
    void setEnabled(bool enabled);
    // The plan of the snapshot observed at `observedMs`.
    std::optional<std::string> plan(std::int64_t observedMs) const;

private:
    struct Seen {
        std::int64_t observedMs = 0;
        std::optional<std::string> plan;
    };

    mutable std::mutex mutex_;
    bool enabled_ = false;
    std::optional<std::string> account_;
    std::optional<Seen> newest_;
    std::map<std::optional<std::string>, Seen> latest_;
};

}  // namespace agentspeed::relay
