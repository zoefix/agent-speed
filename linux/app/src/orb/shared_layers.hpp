#pragma once

#include <vector>

#include "orb/frame.hpp"

// Layers every style but `led` takes from the shared foundation (DESIGN 2a): aura, response wave and new-peak rays.
namespace orb::shared {

inline constexpr double kPulseDuration = 0.7;

struct Pulse {
    double start = 0, strength = 0, heat = 0;
};

void addAura(Scene& scene, const Frame& frame);
void addPulses(Scene& scene, const Frame& frame, const std::vector<Pulse>& pulses);
void addPeakRays(Scene& scene, const Frame& frame);

// Peak strength of a blurred stroke `width` wide (Gaussian σ) relative to the analytic glow, which is full strength at
// the edge whatever the size: thin strokes glow only faintly.
double strokeGlow(double width, double sigma);
// The same for a dot of `radius`.
double dotGlow(double radius, double sigma);

}  // namespace orb::shared
