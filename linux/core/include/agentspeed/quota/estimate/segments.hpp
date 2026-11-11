#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/quota/estimate/ledger.hpp"
#include "agentspeed/quota/estimate/model.hpp"

// QUOTAS 10.4 and 10.5: resolution, window instances, segments and one interval per instance.
namespace agentspeed::quota::estimate {

// 1 for integers, 0.1 for one decimal, else 0.01.
double resolution(double p);

// Observations of one account and window whose resets lie within 2 minutes of the first; `S` starts the lower-bound
// sums and `Shi` the upper-bound ones (an hour earlier for a five-hour window reset on a whole hour). The observations
// are those inside [Shi − 1 min, R + 1 min], by (t, tLo).
struct Instance {
    std::int64_t R = 0, S = 0, Shi = 0;
    std::vector<Observation> obs;
};
std::vector<Instance> instances(std::vector<Observation> observations);
// The finest resolution per source.
std::map<std::string, double> windowQ(const std::vector<Observation>& observations);

struct Segment {
    bool increment = false;
    double lo = 0, hi = kInfinity, dp = 0;
    std::int64_t t = 0;
};
// The cumulative and increment segments of one instance; spend after `nowMs` does not exist yet.
std::vector<Segment> segments(const Instance& instance, const Ledger& ledger, const std::string& account, const std::optional<std::string>& name,
                              std::int64_t lag = kLagMs, std::optional<std::int64_t> nowMs = std::nullopt);

// One interval per instance: the largest lower bound and the smallest upper bound not below it; the increments alone
// when a cumulative lower bound lies above an increment's upper bound (`offset`).
struct Interval {
    std::int64_t R = 0;
    double lo = 0, hi = kInfinity, dp = 0;
    std::int64_t t = 0;
    bool offset = false;
};
std::optional<Interval> instanceBounds(const std::vector<Segment>& segments, std::int64_t R);

}  // namespace agentspeed::quota::estimate
