#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <utility>
#include <vector>

#include "agentspeed/history.hpp"
#include "agentspeed/quota/estimate/engine.hpp"
#include "agentspeed/quota/estimate/sign_in.hpp"

// The app's quota estimate (QUOTAS 10): the engine's inputs as the history, the live engine, the quota readers and the
// remote hosts feed them, rebuilt on demand, and the install id and estimate.json on disk. Thread-safe: every thread
// feeds it, one builds.
namespace agentspeed::quota::estimate {

// A response of the history or of the live engine in the ledger's form; `machine` is kLocal or the host's id.
Response responseOf(const history::EstimateResponse& response, const std::string& machine = std::string(kLocal));
// A token count line's rate limits, as the history keeps them (compact JSON) or as a parser hands them over.
std::optional<CodexLine> codexLineOf(const std::string& agentId, std::int64_t ts, std::string_view rateLimits);
std::optional<CodexLine> codexLineOf(const std::string& agentId, std::int64_t ts, const json::Value& rateLimits);

class Estimator {
public:
    // `folder`: `<data>/quota`; the install id is read there, or created, now. Without one there is no estimate.
    explicit Estimator(std::string folder);

    bool available() const { return install_.has_value(); }
    // QUOTAS 10.1 accountSalt, which keys every account.
    const Salt& salt() const { return accountSalt(); }
    // REMOTE 11.1 `salt`: the account salt in 64 lowercase hex digits, "" without an estimate. Never logged.
    std::string saltText() const;

    // QUOTAS 10.3: the history's spend and Codex lines; what ended before `cutMs` comes from it, the rest from the live
    // engine (this computer's; the hosts' always from the live engine).
    void setHistory(const history::EstimateFile& spend, std::int64_t cutMs);
    // The live engine's responses, by their key (an update replaces the earlier one).
    void putLive(std::vector<std::pair<std::string, Response>> responses);
    // Dropped while Codex's quota is not read (QUOTAS 9.1).
    void addCodexLine(CodexLine line);
    void setCodexEnabled(bool enabled);
    // QUOTAS 10.1 key timeline: a sign-in as read now (nullopt: signed out, which changes nothing).
    void setCodexAccount(std::int64_t at, const std::optional<SignedInAccount>& account);
    void setClaudeAccount(std::int64_t at, const std::optional<SignedInAccount>& account);
    // QUOTAS 10.4: a new vendor value; a run of equal values keeps its first and last.
    void addSnapshot(SnapshotEvent snapshot);
    void setPriceVersion(int version);

    // Builds at `nowMs` and returns, for each shown (machine, source), the entries of the account it spends on, keyed
    // `<source>` (this computer) or `<source>@<machine>`.
    using Shown = std::map<std::string, std::vector<Entry>>;
    Shown build(std::int64_t nowMs, const std::set<std::pair<std::string, std::string>>& shown);
    // REPORTING-EXTENSIONS 11.2 of the last build for the service of api origin `origin`: the shown sources' accounts,
    // one entry per account and window, in report order, at most 16.
    std::vector<Entry> entries(std::int64_t nowMs, std::string_view origin) const;
    // estimate.json (QUOTAS 10.4): at most once a minute, or now with `force` (at quit).
    void save(std::int64_t nowMs, bool force = false);

    static std::string shownKey(const std::string& machine, const std::string& source);

private:
    void prune(std::int64_t nowMs);

    std::string folder_;
    std::optional<std::string> install_;
    mutable std::mutex mutex_;
    int priceVersion_ = 1;
    std::vector<Response> history_;
    std::vector<CodexLine> historyLines_;
    std::int64_t cutMs_ = 0;
    std::map<std::string, Response> live_;
    std::vector<CodexLine> liveLines_;
    bool codexEnabled_ = false;
    std::vector<std::pair<std::int64_t, std::string>> codexKeys_;
    std::vector<ClaudeProfile> profiles_;
    std::optional<std::string> lastCodexKey_;
    std::optional<SignedInAccount> lastClaude_;
    struct Run {
        std::size_t index = 0;
        bool repeated = false;
    };
    std::vector<SnapshotEvent> snapshots_;
    std::map<std::pair<std::string, std::string>, Run> lastSnapshot_;
    Store stored_;
    std::shared_ptr<const Engine> built_;
    std::set<std::string> shownAccounts_;
    std::optional<std::int64_t> savedMs_;
};

}  // namespace agentspeed::quota::estimate
