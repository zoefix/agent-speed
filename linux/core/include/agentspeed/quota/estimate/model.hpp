#pragma once

#include <cstddef>
#include <cstdint>
#include <limits>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// QUOTAS 10: the constants, the observation and the number rules every part of the estimate shares. The reference is
// spec/tools/quota_estimate.py; its float operations are mirrored in the same order so the results match bit for bit.
namespace agentspeed::quota::estimate {

inline constexpr std::int64_t kMinute = 60'000;
inline constexpr std::int64_t kHour = 60 * kMinute;
inline constexpr std::int64_t kDay = 24 * kHour;
inline constexpr std::int64_t kLagMs = 2 * kMinute;
inline constexpr std::int64_t kKeepMs = 8 * kDay;
inline constexpr std::int64_t kInstanceJoinMs = 2 * kMinute;
inline constexpr std::int64_t kFingerprintMs = kMinute;
inline constexpr std::int64_t kHourRoundedSlackMs = kHour;
inline constexpr std::int64_t kSendEveryMs = 60'000;
inline constexpr std::int64_t kLineMatchMs = 5'000;
inline constexpr double kHiPenalty = 0.25;
inline constexpr double kFullStep = 20.0;
inline constexpr double kMinWeight = 0.05;
inline constexpr double kIncrementStep = 10.0;
inline constexpr double kPrecisionFloor = 0.02;
inline constexpr double kHighWidth = 0.08;
inline constexpr std::size_t kSegmentsPerInstance = 64;
inline constexpr std::size_t kInstancesPerClass = 64;
inline constexpr std::size_t kObservationsPerInstance = 256;
inline constexpr std::size_t kMaxEntries = 16;
inline constexpr int kStoreVersion = 2;
inline constexpr double kInfinity = std::numeric_limits<double>::infinity();

// Python's `//` and the minute an instant falls in.
std::int64_t floorDiv(std::int64_t a, std::int64_t b);
inline std::int64_t minuteOf(std::int64_t ms) { return floorDiv(ms, kMinute) * kMinute; }

// `<kind>:<name>:<minutes>` (an empty name for none).
std::string windowId(std::string_view kind, const std::optional<std::string>& name, std::int64_t minutes);
// The minutes of a window id; 0 when it has none.
std::int64_t minutesOfWindowId(std::string_view id);

// Python's round(x, digits): the correctly rounded decimal, ties to even.
double pyRound(double x, int digits);
// Python's round() of a float to an integer (ties to even).
std::int64_t pyRoundToInt(double x);
// REPORTING-EXTENSIONS 11.2: half away from zero (with the reference's 1e-9 nudge).
double roundHalfAway(double x, int digits);
// Micro-USD and milliseconds: floor(x + 0.5).
std::int64_t micro(double x);
// Python's float `repr`: the shortest text that reads back as the same double, with `.0` for whole numbers.
std::string floatText(double x);

// One vendor value of one window (QUOTAS 10.4). `machine` is `local` or the remote host it came from.
struct Observation {
    std::string account;
    std::string source;
    std::string kind;
    std::optional<std::string> name;
    std::int64_t minutes = 0;
    double p = 0;
    std::int64_t t = 0;
    std::int64_t tLo = 0;
    std::int64_t R = 0;
    std::optional<std::string> plan, tier;
    std::string machine = "local";
    // A host's observation: the key its relay sent (nullopt from older relays), shared or not by the fingerprint.
    std::optional<std::string> hostKey;
    std::string window() const { return windowId(kind, name, minutes); }
    bool operator==(const Observation&) const = default;
};

}  // namespace agentspeed::quota::estimate
