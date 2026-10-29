#include "orb/shared_layers.hpp"

#include <algorithm>
#include <cmath>

namespace orb::shared {

void addAura(Scene& scene, const Frame& f) {
    const double h = f.heat;
    const double alpha =
        f.tokens.auraOpacity * h * f.breath * (1 + 0.7 * f.auraFlare) + (f.dark ? 0.05 : 0.03) * f.auraFlare;
    if (alpha <= 0.004) return;
    scene.add(Instance::radial(f.center, 0.5 * f.s, f.reach * f.s, f.tokens.heat(std::max(h, 0.12)), std::min(1.0, alpha)),
              f.glowBlend());
}

void addPulses(Scene& scene, const Frame& f, const std::vector<Pulse>& pulses) {
    for (const auto& pulse : pulses) {
        const double p = std::clamp((f.time - pulse.start) / kPulseDuration, 0.0, 1.0);
        const double eased = led::easeOut(p);
        const Rgba color = f.dark ? heatColor(pulse.heat).mixLinear(kWhiteHot, 0.18) : heatColor(pulse.heat, false);
        const double radius = (0.5 + (f.reach - 0.51) * eased) * f.s;
        const double alpha = 0.6 * (1 - eased) * (0.45 + 0.55 * pulse.strength);
        const double width = std::max(0.3, 2 * f.unit * (1 - eased) * (0.55 + 0.45 * pulse.strength));
        scene.add(Instance::ring(f.center, radius, width, color.opacity(alpha), 1, 3 * f.unit, 0.35), f.glowBlend());
    }
}

void addPeakRays(Scene& scene, const Frame& f) {
    const double elapsed = f.peakAge;
    if (!f.effects.celebrate || elapsed < 0 || elapsed >= 0.95) return;
    const double p = elapsed / 0.95;
    const Rgba gold = f.dark ? kGold.mixLinear(kWhiteHot, 0.25) : kGold.darkened(0.1);
    const double travel = led::easeOut(p);
    const double alpha = std::pow(1 - p, 1.3);
    for (int index = 0; index < 24; ++index) {
        const bool longRay = index % 2 == 0;
        const double angle = index * 15 + 3.75;
        const double reach = longRay ? 0.23 : 0.16;
        const double head = 0.515 + reach * travel;
        const double tail = 0.505 + reach * std::max(0.0, travel - 0.42) * 1.25;
        scene.add(Instance::capsule(f.point(angle, tail * f.s), f.point(angle, head * f.s), (longRay ? 1.7 : 1.2) * f.unit,
                                    gold.opacity(alpha), gold.opacity(alpha * 0.25), 1, 2.2 * f.unit, 0.45),
                  f.glowBlend());
    }
}

double strokeGlow(double width, double sigma) {
    if (sigma <= 0) return 1;
    return std::erf(width / (2 * 1.41421356 * sigma));
}

double dotGlow(double radius, double sigma) {
    if (sigma <= 0) return 1;
    return 1 - std::exp(-radius * radius / (2 * sigma * sigma));
}

}  // namespace orb::shared
