#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <tuple>
#include <vector>

#include "agentspeed/quota/estimate/ledger.hpp"
#include "agentspeed/quota/estimate/segments.hpp"

// QUOTAS 10.6 and 10.7: weights, the interval vote, the estimate, cap classes and the live percent.
namespace agentspeed::quota::estimate {

enum class Confidence : std::uint8_t { None, Low, Medium, High };
std::string_view confidenceId(Confidence confidence);
std::optional<Confidence> confidenceFromId(std::string_view id);

// 7 days for windows of 10 000 minutes or more, else 2 days.
std::int64_t halfLifeMs(std::int64_t minutes);
double weight(double dp, std::int64_t t, std::int64_t nowMs, std::int64_t halfLife);

struct Weighted {
    double lo = 0, hi = kInfinity, w = 0;
    std::int64_t id = 0;  // the instance (its reset)
};
struct VoteResult {
    double low = 0, high = kInfinity;
    double inside = 0;     // the weight of the instances holding the region
    int insideWindows = 0;  // those of them at most 8 % wide
    double loss = 0;
};
std::optional<VoteResult> vote(const std::vector<Weighted>& weighted);
// `above`: the weight of lower bounds above the region.
Confidence confidenceOf(double low, double high, double inside, int narrow, double above);

struct Estimate {
    Confidence confidence = Confidence::None;
    std::optional<double> cap, low, high;
    std::optional<double> regionLow, regionHigh;
    double loss = 0, inside = 0;
    int windows = 0;
    bool operator==(const Estimate&) const = default;
};
// A lowered cap: when the two newest narrow instances agree, older ones whose lower bound lies above both go.
std::vector<Interval> afterCut(const std::vector<Interval>& items);
Estimate estimate(const std::vector<Interval>& items, std::int64_t nowMs, std::int64_t halfLife);
// The newest `limit` by t.
std::vector<Interval> pruneClosed(std::vector<Interval> items, std::size_t limit = kInstancesPerClass);

// QUOTAS 10.2: estimates pool every account of one (vendor/plan, tier, window); without a plan, (account, window).
struct CapClass {
    bool byPlan = false;
    std::string id;  // "<vendor>/<plan>", or the account
    std::optional<std::string> tier;
    std::string window;
    static CapClass of(const std::string& account, const std::optional<std::string>& vendorPlan, const std::optional<std::string>& tier,
                       const std::string& window);
    // Python's str() of the reference's tuple: the order classes are written in.
    std::string repr() const;
    // The fixture's key: the parts joined by `|` (`None` for null).
    std::string joined() const;
    auto operator<=>(const CapClass&) const = default;
};

struct Live {
    double percent = 0;
    bool estimated = false;
    std::optional<std::int64_t> resetsMs;
};
// The percent of the current instance at `nowMs` (QUOTAS 10.7). `firstAfterReset`: the start of the first local
// response that started at or after R and has ended (five-hour windows).
Live livePercent(double p, std::int64_t t, std::int64_t R, std::int64_t S, std::int64_t minutes, std::string_view kind, std::optional<double> cap,
                 const Ledger& ledger, const std::string& account, const std::optional<std::string>& name, std::int64_t nowMs,
                 std::optional<std::int64_t> firstAfterReset);

}  // namespace agentspeed::quota::estimate
