#pragma once

#include <vector>

#include "orb/style.hpp"

namespace orb {

// `needle` — the analog racing tachometer (DESIGN 2a.2): a graduated dial with a red zone, a heavy needle on an
// underdamped spring that trembles at speed and pins against the peg beyond the redline, a lit trace, shift lights that
// flash at the redline, a wake behind a fast needle and sparks where it grinds against the peg.
class NeedleStyle : public Style {
public:
    StyleKind kind() const override { return StyleKind::Needle; }
    const ReadoutLayout& readoutLayout() const override;
    const LinePlacement& linePlacement() const override { return LinePlacement::needle; }

    void pulse(double time, double strength, double heat) override;
    void celebrate(double) override {}
    void step(const Frame& frame) override;
    void build(Scene& scene, const Frame& frame) override;
    void buildReadout(Scene& scene, const Frame& frame, const ReadoutContent& content, const MeasureText& measure) override;
    bool isBusy(const Frame& frame) const override;
    int ambientFps(const Frame& frame) const override;

private:
    struct Spark {
        double x, y, vx, vy, age, life, width;  // diameters from the center, y down
    };

    double displayed(const Frame& frame) const;
    double flare(double time) const;
    bool sweeping(const Frame& frame) const;
    double target(const Frame& frame) const;
    void stepSparks(double dt, double rate, int pool);
    void addDial(Scene& scene, const Frame& frame) const;
    void addRedZone(Scene& scene, const Frame& frame, bool active) const;
    void addGraduations(Scene& scene, const Frame& frame, double needle, bool active) const;
    void addTrace(Scene& scene, const Frame& frame, double needle) const;
    void addPins(Scene& scene, const Frame& frame) const;
    void addLamps(Scene& scene, const Frame& frame, double needle, bool active) const;
    void addWake(Scene& scene, const Frame& frame, double needle) const;
    void addNeedle(Scene& scene, const Frame& frame, double needle, bool active) const;
    void addSparks(Scene& scene, const Frame& frame) const;
    void addHub(Scene& scene, const Frame& frame) const;

    double position_ = 0, velocity_ = 0;  // the spring, dial positions
    bool initialized_ = false;
    bool springy_ = true;
    double angularSpeed_ = 0;  // degrees per second, spring only
    double tremble_ = 0;
    double phases_[3] = {0, 0, 0};
    double redPhase_ = 0, lampPhase_ = 0;
    std::vector<Spark> sparks_;
    double sparkCarry_ = 0;
    led::Random random_{0x51A7C0DE77E10B11ULL};
    double flareStart_ = -1e9, flareStrength_ = 0;
};

}  // namespace orb
