#include "agentspeed/quota/codex.hpp"

#include <algorithm>
#include <cmath>
#include <limits>

namespace agentspeed::quota {

namespace {

constexpr double kInt64Edge = 9223372036854775808.0;  // 2^63

// Python's int() of a finite double, saturated.
std::int64_t truncated(double value) {
    if (value >= kInt64Edge) return std::numeric_limits<std::int64_t>::max();
    if (value < -kInt64Edge) return std::numeric_limits<std::int64_t>::min();
    return static_cast<std::int64_t>(std::trunc(value));
}

std::int64_t saturatedProduct(std::int64_t value, std::int64_t factor) {
    std::int64_t out = 0;
    if (__builtin_mul_overflow(value, factor, &out)) {
        return (value < 0) != (factor < 0) ? std::numeric_limits<std::int64_t>::min() : std::numeric_limits<std::int64_t>::max();
    }
    return out;
}

std::int64_t saturatedSum(std::int64_t a, std::int64_t b) {
    std::int64_t out = 0;
    if (__builtin_add_overflow(a, b, &out)) return b < 0 ? std::numeric_limits<std::int64_t>::min() : std::numeric_limits<std::int64_t>::max();
    return out;
}

// 290–310 minutes is the 5-hour window (older versions wrote 299), 10000–10160 the weekly one.
Kind kindOf(std::int64_t minutes) {
    if (minutes >= 290 && minutes <= 310) return Kind::FiveHour;
    if (minutes >= 10000 && minutes <= 10160) return Kind::SevenDay;
    return Kind::Other;
}

}  // namespace

std::vector<Window> codexWindows(const json::Value& rateLimits, std::int64_t lineMs) {
    std::vector<Window> found;
    if (!rateLimits.isObject()) return found;
    const auto limitId = rateLimits["limit_id"];
    if (limitId.valid() && !limitId.isNull() && !limitId.equals("codex")) return found;
    for (const auto slotName : {"primary", "secondary"}) {
        const auto slot = rateLimits[slotName];
        if (!slot.isObject()) continue;
        const auto used = slot["used_percent"].finiteNumber();
        const auto length = slot["window_minutes"].finiteNumber();
        if (!used || !length) continue;
        const std::int64_t minutes = truncated(*length);
        if (minutes <= 0) continue;
        const Kind kind = kindOf(minutes);
        std::optional<std::int64_t> resets;
        if (const auto at = slot["resets_at"].finiteNumber()) {
            resets = saturatedProduct(truncated(*at), 1000);
        } else if (const auto after = slot["resets_in_seconds"].finiteNumber()) {
            resets = saturatedSum(lineMs, truncated(*after * 1000));
        }
        const bool taken = std::any_of(found.begin(), found.end(), [&](const Window& w) {
            return w.kind == kind && (kind != Kind::Other || w.minutes == minutes);
        });
        if (taken) continue;
        found.push_back(makeWindow(kind, *used, resets, std::nullopt,
                                   kind == Kind::Other ? std::optional<std::int64_t>(minutes) : std::nullopt));
    }
    return found;
}

void CodexCollector::observe(const json::Value& rateLimits, std::int64_t timestampMs) {
    auto windows = codexWindows(rateLimits, timestampMs);
    if (windows.empty()) return;
    const std::lock_guard<std::mutex> lock(mutex_);
    if (!enabled_ || (snapshot_ && timestampMs <= snapshot_->observedMs)) return;
    snapshot_ = Snapshot{SourceKind::Codex, timestampMs, std::move(windows)};
    ++version_;
}

CodexCollector::Observer CodexCollector::observer() {
    return [this](const json::Value& rateLimits, std::int64_t timestampMs) { observe(rateLimits, timestampMs); };
}

std::optional<Snapshot> CodexCollector::snapshot() const {
    const std::lock_guard<std::mutex> lock(mutex_);
    return snapshot_;
}

std::uint64_t CodexCollector::version() const {
    const std::lock_guard<std::mutex> lock(mutex_);
    return version_;
}

void CodexCollector::setEnabled(bool enabled) {
    const std::lock_guard<std::mutex> lock(mutex_);
    enabled_ = enabled;
    if (!enabled && snapshot_) {
        snapshot_.reset();
        ++version_;
    }
}

void CodexCollector::clear() {
    const std::lock_guard<std::mutex> lock(mutex_);
    if (snapshot_) {
        snapshot_.reset();
        ++version_;
    }
}

}  // namespace agentspeed::quota
