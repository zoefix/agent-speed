#include "orb/gauge.hpp"

#include <algorithm>
#include <cmath>

#include "heat.hpp"

namespace orb {

namespace {

constexpr double kOmega = 10.0;
constexpr double kHold = 0.8;
constexpr double kIdleAfter = 3.0;
constexpr double kTitleFractions[Gauge::kTitles] = {0, 0.02, 0.03, 0.04, 0.06, 0.08, 0.12,
                                                    0.17, 0.24, 0.34, 0.5, 0.7, 1.0};

}  // namespace

void Gauge::Spring::step(double dt) {
    const double e = x - target;
    const double decay = std::exp(-kOmega * dt);
    const double k = v + kOmega * e;
    x = target + (e + k * dt) * decay;
    v = (v - kOmega * k * dt) * decay;
    if (std::abs(x - target) < 1e-4 && std::abs(v) < 1e-3) {
        x = target;
        v = 0;
    }
}

double Gauge::enterTier(Tier tier) const {
    switch (tier) {
        case Tier::Cruise: return 0.5;
        case Tier::Boost: return 0.15 * redline_;
        case Tier::Overdrive: return 0.375 * redline_;
        case Tier::Redline: return 0.75 * redline_;
        case Tier::Idle: break;
    }
    return 0;
}

double Gauge::enterTitle(int index) const { return index == 0 ? 0.5 : kTitleFractions[index] * redline_; }

Tier Gauge::desiredTier(double now) const {
    const double v = value_;
    if (v < 0.5) {
        if (tier_ == Tier::Idle) return Tier::Idle;
        if (belowSince_ && now - *belowSince_ >= kIdleAfter) return Tier::Idle;
    }
    auto t = static_cast<int>(std::max(tier_, Tier::Cruise));
    if (tier_ == Tier::Idle && v < 0.5) return Tier::Idle;
    const int start = t;
    while (t < static_cast<int>(Tier::Redline) && v >= enterTier(static_cast<Tier>(t + 1))) ++t;
    if (t == start) {
        while (t > static_cast<int>(Tier::Cruise) && v < 0.9 * enterTier(static_cast<Tier>(t))) --t;
    }
    return static_cast<Tier>(t);
}

int Gauge::desiredTitle() const {
    const double v = value_;
    int t = std::max(title_, 0);
    const int start = t;
    while (t < kTitles - 1 && v >= enterTitle(t + 1)) ++t;
    if (t == start) {
        while (t > 0 && v < 0.9 * enterTitle(t)) --t;
    }
    return t;
}

void Gauge::update(double value, double now) {
    value_ = std::isfinite(value) ? std::max(0.0, value) : 0.0;
    heat_.target = heat::of(value_, redline_);
    overdrive_.target = std::clamp((value_ - redline_) / redline_, 0.0, 1.0);
    if (value_ < 0.5) {
        if (!belowSince_) belowSince_ = now;
    } else {
        belowSince_.reset();
    }
    if (!primed_) {
        primed_ = true;
        heat_.x = heat_.target;
        overdrive_.x = overdrive_.target;
        tier_ = pendingTier_ = value_ < 0.5 ? Tier::Idle : desiredTier(now);
        title_ = previousTitle_ = pendingTitle_ = tier_ == Tier::Idle ? -1 : desiredTitle();
        return;
    }

    const Tier wanted = desiredTier(now);
    if (wanted == tier_) {
        pendingTier_ = tier_;
    } else if (wanted != pendingTier_) {
        pendingTier_ = wanted;
        pendingTierSince_ = now;
    } else if (now - pendingTierSince_ >= kHold || wanted == Tier::Idle) {
        tierRose_ = wanted > tier_;
        tier_ = wanted;
        tierChangedAt_ = now;
    }

    const int wantedTitle = tier_ == Tier::Idle ? -1 : desiredTitle();
    if (wantedTitle == title_) {
        pendingTitle_ = title_;
    } else if (wantedTitle != pendingTitle_) {
        pendingTitle_ = wantedTitle;
        pendingTitleSince_ = now;
    } else if (now - pendingTitleSince_ >= kHold || wantedTitle == -1 || title_ == -1) {
        previousTitle_ = title_;
        title_ = wantedTitle;
        titleChangedAt_ = now;
    }
}

void Gauge::step(double now) {
    const double dt = lastStep_ < 0 ? 0 : std::clamp(now - lastStep_, 0.0, 1.0 / 15);
    lastStep_ = now;
    if (dt <= 0) return;
    heat_.step(dt);
    overdrive_.step(dt);
}

void Gauge::settle() {
    heat_.x = heat_.target;
    heat_.v = 0;
    overdrive_.x = overdrive_.target;
    overdrive_.v = 0;
}

bool Gauge::isSettled() const {
    return heat_.x == heat_.target && heat_.v == 0 && overdrive_.x == overdrive_.target && overdrive_.v == 0;
}

}  // namespace orb
