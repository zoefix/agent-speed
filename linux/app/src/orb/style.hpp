#pragma once

#include <QString>
#include <memory>

#include "orb/frame.hpp"
#include "orb/readout.hpp"

namespace orb {

// The instrument an orb draws (DESIGN 2a). Unknown keys read as `led`.
enum class StyleKind { Led, Needle, Liquid, Reactor, Minimal };

QString styleKey(StyleKind kind);
StyleKind styleFromKey(const QString& key);
inline constexpr StyleKind kStyles[] = {StyleKind::Led, StyleKind::Needle, StyleKind::Liquid, StyleKind::Reactor, StyleKind::Minimal};

// What each style contributes to an orb (the macOS OrbStyleRenderer). The window draws the shared aura, response wave
// and new-peak rays around `build` for the styles that use them, and the readout after it.
class Style {
public:
    virtual ~Style() = default;
    virtual StyleKind kind() const = 0;
    virtual const ReadoutLayout& readoutLayout() const { return ReadoutLayout::led; }
    virtual const LinePlacement& linePlacement() const { return LinePlacement::led; }
    // Layers of the shared foundation the window adds (`led` draws its own v1.4 aura, shockwaves and rays).
    virtual bool sharedAura() const { return true; }
    virtual bool sharedWave() const { return true; }
    virtual bool sharedRays() const { return true; }

    // A responseCompleted of the orb's sources (strength 0.3...1, heat of the response).
    virtual void pulse(double time, double strength, double heat) = 0;
    // A celebrated new peak.
    virtual void celebrate(double time) = 0;
    // Advances the style's own simulation (once per rendered frame, before build).
    virtual void step(const Frame& frame) = 0;
    // Adds the instrument between the body and the readout.
    virtual void build(Scene& scene, const Frame& frame) = 0;
    // Adds the readout; `liquid` splits it at its surface.
    virtual void buildReadout(Scene& scene, const Frame& frame, const ReadoutContent& content, const MeasureText& measure) {
        orb::buildReadout(scene, frame, content, measure, readoutLayout(), linePlacement());
    }
    // Whether anything of the style still moves (springs settling, particles alive).
    virtual bool isBusy(const Frame& frame) const = 0;
    // 0: static, 15, 30 or 60 frames per second at rest in this state (DESIGN 2a, Frame policy).
    virtual int ambientFps(const Frame& frame) const = 0;
    // Duration of the idle sign of life (0: none).
    virtual double signOfLifeDuration() const { return 0.9; }

    static std::unique_ptr<Style> make(StyleKind kind);
};

}  // namespace orb
