#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/quota/estimate/ledger.hpp"
#include "agentspeed/quota/estimate/vote.hpp"
#include "agentspeed/report_json.hpp"

// REPORTING-EXTENSIONS 11.2 and 11.3: one account-window's report entry and the send cadence of `quotas`. The same
// entry is what the dashboard and the 額度 card show (QUOTAS 10.8).
namespace agentspeed::quota::estimate {

// What an entry is computed from: the account-window's newest vendor value and its instance.
struct EntryInput {
    std::string source;
    std::vector<std::string> sources;
    std::optional<std::string> plan, tier;
    std::string kind;
    std::optional<std::string> name;
    std::int64_t minutes = 0;
    double p = 0;
    std::int64_t t = 0, R = 0, S = 0;
    std::string account;
    Estimate estimate;
    std::optional<std::int64_t> firstAfterReset;
};

struct Entry {
    std::string source;
    std::vector<std::string> sources;
    std::optional<std::string> plan, tier;
    std::string kind;
    std::optional<std::string> name;
    std::int64_t minutes = 0;
    std::optional<double> usedPercent;
    std::optional<std::int64_t> percentAgeMs, resetsInMs;
    double livePercent = 0;
    bool liveEstimated = false;
    std::int64_t usedMicroUsd = 0;
    std::optional<std::int64_t> observedUsedMicroUsd;
    std::int64_t unpricedRequests = 0;
    std::optional<std::int64_t> capMicroUsd, lowMicroUsd, highMicroUsd;
    Confidence confidence = Confidence::None;
    std::optional<double> coverage;
    std::optional<std::int64_t> accountUsedMicroUsd;
    int windows = 0;
    std::vector<MixShare> mix;
    int priceVersion = 1;
    // Not reported: the unrounded live percent, the reset instant and the account, for the display.
    double liveExact = 0;
    std::optional<std::int64_t> resetsAtMs;
    std::string account;

    report::Json json() const;
    bool operator==(const Entry&) const = default;
};

Entry reportEntry(const EntryInput& input, std::int64_t nowMs, const Ledger& ledger, int priceVersion);

// Whether a report carries `quotas`: the run's first, a changed set, a new vendor percent, confidence or reset state,
// 60 s since the last one sent, or leaving.
bool shouldSend(const std::vector<Entry>& entries, const std::vector<Entry>* lastSent, std::int64_t lastSentMs, std::int64_t nowMs,
                bool leaving = false);

// The cadence across one run's reports. `step` says what this report carries (nullopt: absent; an empty list: `[]`);
// a section dropped for size is passed to `dropped` and counts as unsent.
class Reporter {
public:
    void newRun() { *this = Reporter(); }
    std::optional<std::vector<Entry>> step(std::int64_t nowMs, const std::vector<Entry>& entries, bool switchOn, bool leaving = false);
    void dropped();

private:
    std::optional<std::vector<Entry>> last_, previous_;
    std::int64_t lastMs_ = 0, previousMs_ = 0;
    bool cleared_ = true, previousCleared_ = true;
};

}  // namespace agentspeed::quota::estimate
