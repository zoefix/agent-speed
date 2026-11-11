#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "agentspeed/quota/estimate/accounts.hpp"
#include "agentspeed/quota/estimate/entry.hpp"
#include "agentspeed/quota/estimate/ledger.hpp"
#include "agentspeed/quota/estimate/plan_inference.hpp"
#include "agentspeed/quota/estimate/segments.hpp"
#include "agentspeed/quota/estimate/vote.hpp"
#include "agentspeed/quota/window.hpp"

// The estimate pipeline (QUOTAS 10.1–10.8, REPORTING-EXTENSIONS 11.3): raw events to accounts, the ledger,
// observations, instances, cap classes, estimates, report entries and the store. A port of
// spec/tools/quota_estimate_pipeline.py, plus remote hosts (QUOTAS 10.1 Remote hosts): a host's sources take the key
// its relay sent, shared with this computer's account of the same key unless their fingerprint contradicts it.
namespace agentspeed::quota::estimate {

inline constexpr std::string_view kLocal = "local";

// One response (METRICS 4) as the ledger takes it.
struct Response {
    std::string source;
    std::string machine = std::string(kLocal);
    // The agent the response belongs to (one per Codex rollout): token count lines match responses of the same file.
    std::string file;
    std::optional<std::int64_t> startMs;
    std::int64_t endMs = 0;
    std::string model;
    std::optional<std::int64_t> cost;
    std::int64_t tokens = 0;
    std::int64_t requests = 1;
    std::optional<std::string> provider;
    // A Claude model id in a Bedrock or Vertex form (METRICS 9.2 cloud step).
    bool cloudRoute = false;
    bool operator==(const Response&) const = default;
};

// A Codex token count line with rate limits (QUOTAS 3): its windows (codexWindows) and its cleaned `plan_type`.
struct CodexLine {
    std::string file;
    std::int64_t ts = 0;
    std::vector<Window> windows;
    std::optional<std::string> plan;
    bool operator==(const CodexLine&) const = default;
};

// A key timeline entry (QUOTAS 10.1): from `at` on, the source spends on `key`.
struct KeyEntry {
    std::int64_t at = 0;
    std::string key;
    std::optional<std::string> plan, tier;
    auto operator<=>(const KeyEntry&) const = default;
};

// `/login` and the first sight of the Claude CLI's account: its key, its organization's Desktop key, plan and tier.
struct ClaudeProfile {
    std::int64_t at = 0;
    std::string key, orgKey;
    std::optional<std::string> plan, tier;
};

// A quota snapshot (QUOTAS 2): Claude Desktop's carries its organization's key; a host's the relay's account, plan and
// tier (REMOTE 11.1).
struct SnapshotEvent {
    std::string source;
    std::string machine = std::string(kLocal);
    std::int64_t observedMs = 0;
    std::vector<Window> windows;
    std::optional<std::string> orgKey;
    std::optional<std::string> account, plan, tier;
};

// <data>/quota/estimate.json (QUOTAS 10.4).
struct StoredInstance {
    CapClass cls;
    std::string account;
    std::int64_t R = 0;
    double lo = 0;
    std::optional<double> hi;
    double pMax = 0;
    std::int64_t tLast = 0;
};
struct StoredKey {
    std::string source;
    KeyEntry entry;
    std::optional<std::string> orgKey;
};
struct Store {
    int version = kStoreVersion;
    std::vector<Observation> observations;
    std::vector<StoredInstance> instances;
    std::vector<StoredKey> keys;
};

// One window instance of one (account, window, plan, tier) with its interval and cap class.
struct InstanceRow {
    std::string account, window;
    std::optional<std::string> plan, tier;
    CapClass cls;
    Instance inst;
    std::optional<Interval> bounds;
    std::optional<std::string> name;
    bool closed = false;
};

// Each build starts from the events added so far; the results stay until the next build. `install` (QUOTAS 10.1
// installId, hex) keys the accounts without an id.
class Engine {
public:
    explicit Engine(Salt salt, int priceVersion = 8, std::string install = {});

    void add(Response response);
    void add(CodexLine line);
    void addCodexKey(std::int64_t at, std::string key);
    void add(ClaudeProfile profile);
    void add(SnapshotEvent snapshot);
    // A store of another version is ignored.
    void load(const Store& store);

    void build(std::int64_t nowMs);

    std::int64_t builtAt() const { return nowMs_; }
    int priceVersion() const { return priceVersion_; }
    // Per added response, in order: its account, nullopt when it is no subscription spend.
    const std::vector<std::optional<std::string>>& responseAccounts() const { return responseAccounts_; }
    const Ledger& ledger() const { return *ledger_; }
    std::shared_ptr<const Ledger> sharedLedger() const { return ledger_; }
    const std::vector<Observation>& observations() const { return obs_; }
    const std::vector<InstanceRow>& instances() const { return insts_; }
    const std::map<CapClass, Estimate>& estimates() const { return estimates_; }
    // QUOTAS 10.2: the plans inferred for the accounts Claude Desktop spends on as its own, by account.
    const std::map<std::string, InferredPlan>& inferredPlans() const { return inferred_; }
    // One input per account-window (its newest instance), in report order but for the account tag.
    const std::vector<EntryInput>& current() const { return current_; }
    // REPORTING-EXTENSIONS 11.2 for the service of api origin `origin`: the entries of `accounts` (all without it) in
    // report order, the account tag last, at most 16.
    std::vector<Entry> entries(std::int64_t nowMs, std::string_view origin, const std::set<std::string>* accounts = nullptr) const;
    Store store() const;
    // The account a displayed source spends on now: Codex its newest line's, the Claude sources their timeline's, a
    // host's source its relay's key (shared or not), any other the pair key of (machine, source).
    std::optional<std::string> accountOf(const std::string& machine, const std::string& source) const;

private:
    struct Line {
        CodexLine line;
        std::vector<Window> windows;  // those with a reset
        std::vector<std::string> tags;
        std::int64_t tLo = 0;
        std::optional<std::string> plan;
        std::string account;
    };
    using Timeline = std::vector<KeyEntry>;

    static const KeyEntry* inForce(const Timeline& timeline, std::int64_t at);
    void timelines();
    Timeline desktopTimeline(const Timeline& cli) const;
    std::vector<std::pair<WindowPoint, std::optional<std::string>>> windowPoints(const std::string& source) const;
    void codex();
    std::string hostAccount(const std::string& machine, const std::string& source, const std::optional<std::string>& key);
    const std::string& pairAccount(const std::string& machine, const std::string& source);
    void instancesAt();
    void classEstimates();
    void inferPlans();
    void currentInputs();

    Salt salt_;
    int priceVersion_ = 8;
    std::string install_;
    std::vector<Response> responses_;
    std::vector<CodexLine> lines_;
    std::vector<std::pair<std::int64_t, std::string>> codexAuth_;
    std::vector<ClaudeProfile> profiles_;
    std::vector<SnapshotEvent> snapshots_;
    Store stored_;

    std::int64_t nowMs_ = 0;
    std::map<std::string, Timeline> tl_;
    Timeline desktopOrgs_;
    std::map<KeyEntry, std::string> orgKeys_;
    std::map<std::pair<std::string, std::string>, Timeline> hostTl_;
    std::map<std::tuple<std::string, std::string, std::string>, std::string> hostShared_;
    std::map<std::pair<std::string, std::string>, std::string> pairKeys_;
    std::vector<Line> parsedLines_;
    std::vector<std::optional<std::size_t>> responseLine_;
    std::vector<std::optional<std::string>> responseAccounts_;
    std::shared_ptr<Ledger> ledger_ = std::make_shared<Ledger>();
    std::vector<Observation> obs_;
    std::vector<InstanceRow> insts_;
    std::map<CapClass, Estimate> estimates_;
    std::map<std::string, InferredPlan> inferred_;
    std::vector<EntryInput> current_;
};

}  // namespace agentspeed::quota::estimate
