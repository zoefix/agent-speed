#pragma once

#include <optional>

// The orb's live gauge (DESIGN 1): heat and overdrive smoothed by a critically damped spring (settle ≈ 0.6 s), the
// effect tier and the speed title, both with hysteresis (entered at the threshold, left at 90 %) and a 0.8 s hold.
namespace orb {

enum class Tier { Idle = 0, Cruise, Boost, Overdrive, Redline };

class Gauge {
public:
    static constexpr int kTitles = 13;

    void setRedline(double redline) { redline_ = redline > 0 ? redline : 2000; }
    // Entry speed of title `index` (0 walk … 12 rocket) for a redline (DESIGN 1).
    static double titleThreshold(int index, double redline);
    double redline() const { return redline_; }
    // A new live value (each data tick). The first one sets everything without transitions.
    void update(double value, double now);
    // Advances the springs to `now`.
    void step(double now);
    // Jumps the springs to their targets (the first frame after being hidden).
    void settle();
    bool isSettled() const;

    double value() const { return value_; }
    double heat() const { return heat_.x; }
    double overdrive() const { return overdrive_.x; }
    Tier tier() const { return tier_; }
    double tierChangedAt() const { return tierChangedAt_; }
    bool tierRose() const { return tierRose_; }
    // -1 while idle, else 0 (walk) ... 12 (rocket).
    int title() const { return title_; }
    int previousTitle() const { return previousTitle_; }
    double titleChangedAt() const { return titleChangedAt_; }

private:
    struct Spring {
        double x = 0, v = 0, target = 0;
        void step(double dt);
    };

    double enterTier(Tier tier) const;
    double enterTitle(int index) const;
    Tier desiredTier(double now) const;
    int desiredTitle() const;

    double redline_ = 2000;
    double value_ = 0;
    bool primed_ = false;
    double lastStep_ = -1;
    Spring heat_, overdrive_;
    Tier tier_ = Tier::Idle;
    Tier pendingTier_ = Tier::Idle;
    double pendingTierSince_ = 0;
    double tierChangedAt_ = -1e9;
    bool tierRose_ = false;
    std::optional<double> belowSince_;
    int title_ = -1, previousTitle_ = -1, pendingTitle_ = -1;
    double pendingTitleSince_ = 0;
    double titleChangedAt_ = -1e9;
};

}  // namespace orb
