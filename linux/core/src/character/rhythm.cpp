#include "agentspeed/character/rhythm.hpp"

#include <algorithm>
#include <cmath>

#include "agentspeed/character/numeric.hpp"

namespace agentspeed::character {

const char* name(Band b) {
    switch (b) {
        case Band::Idle: return "idle";
        case Band::Cruise: return "cruise";
        case Band::Boost: return "boost";
        case Band::Overdrive: return "overdrive";
        case Band::Redline: return "redline";
    }
    return "idle";
}

std::optional<Band> bandNamed(std::string_view n) {
    for (Band b : kBands) {
        if (n == name(b)) return b;
    }
    return std::nullopt;
}

double bpm(Band b) {
    switch (b) {
        case Band::Idle: return 56;
        case Band::Cruise: return 84;
        case Band::Boost: return 104;
        case Band::Overdrive: return 124;
        case Band::Redline: return 140;
    }
    return 56;
}

void Beat::update(double dt, Band band) {
    bpm += (character::bpm(band) * tempo - bpm) * std::min(1.0, dt * 1.2);
    phase += dt * bpm / 60;
}

namespace groove {

double up(double b) { return std::fabs(std::sin(math::kPi * b)); }

double hit(double b) { return std::pow(1 - std::fabs(std::sin(math::kPi * b)), 4.0); }

void fill(Band band, double b, double t, const Slots& s, const Slots::SideSlots& o, const Slots::SideSlots& k,
          bool rest, Out& out) {
    using std::cos;
    using std::sin;
    constexpr double pi = math::kPi;
    out.clear();
    auto add = [&](int slot, double v) { out.emplace_back(slot, v); };
    switch (band) {
        case Band::Idle: {
            const double shift = sin(2 * pi * t / 7.5);
            if (rest) {
                add(s.angleY, -0.08 + 0.05 * sin(t * 0.9));
                add(s.angleZ, 0.1 * sin(t * 0.5));
                add(s.angleX, -0.15);
                add(s.eyeBallX, 0.3 * sin(t * 0.7));
                add(s.eyeBallY, -0.1);
                add(k.arm, 0.06);
                add(s.hip, 0.25 * shift);
                add(s.bodyAngleZ, -0.12 * shift);
                add(s.lean, 0.04 * shift);
            } else {
                add(s.angleY, -0.55 + 0.05 * sin(t * 0.9));
                add(s.angleZ, -0.35 + 0.05 * sin(t * 0.6));
                add(s.angleX, -0.22);
                add(s.eyeBallX, -0.4 + 0.25 * sin(t * 1.3));
                add(s.eyeBallY, -0.7);
                add(k.arm, 0.15);
                add(k.forearm, -0.1);
                add(s.hip, 0.28 * shift);
                add(s.bodyAngleZ, -0.15 * shift);
                add(s.lean, 0.05 * shift);
            }
            break;
        }
        case Band::Cruise: {
            const double sn = sin(pi * b);
            add(s.hip, 0.35 * sn);
            add(s.bodyAngleZ, -0.22 * sn);
            add(s.angleZ, 0.12 + 0.14 * sin(pi * b + 0.7));
            add(s.angleX, -0.3);
            add(s.angleY, 0.04 * cos(2 * pi * b));
            add(s.eyeBallX, -0.35);
            add(o.arm, 0.08 * sn);
            add(k.arm, 0.04 * sn);
            add(s.skirtFlare, 0.05);
            break;
        }
        case Band::Boost: {
            const double sn = sin(pi * b), u = up(b), h = hit(b);
            add(s.posY, 0.035 * u);
            add(s.squash, 0.1 * u - 0.22 * h);
            add(s.hip, 0.45 * sn);
            add(s.bodyAngleZ, -0.3 * sn);
            add(s.angleZ, -0.25 * sn);
            add(s.angleY, 0.08 - 0.12 * h);
            add(s.angleX, -0.25);
            add(o.arm, 0.05 + 0.25 * sn);
            add(o.forearm, -0.12 - 0.1 * sn);
            add(k.arm, 0.12 * sn);
            add(o.shoulder, 0.25 * sn);
            add(k.shoulder, -0.25 * sn);
            add(s.skirtFlare, 0.12 + 0.12 * u);
            add(s.footPoint, 0.3 * u);
            break;
        }
        case Band::Overdrive: {
            const double sn = sin(pi * b), u = up(b), h = hit(b);
            add(s.posY, 0.06 * u);
            add(s.squash, 0.15 * u - 0.35 * h);
            add(s.hip, 0.5 * sn);
            add(s.bodyAngleZ, -0.4 * sn);
            add(s.lean, 0.12 * sn);
            add(s.angleZ, -0.35 * sn);
            add(s.angleY, 0.2 - 0.15 * h);
            add(s.angleX, -0.35);
            add(o.arm, 0.1 + 0.35 * sn);
            add(o.forearm, -0.3 - 0.2 * sn);
            add(k.arm, 0.08 + 0.2 * sn);
            add(o.shoulder, 0.35 * sn);
            add(k.shoulder, -0.35 * sn);
            add(s.skirtFlare, 0.25 + 0.15 * u);
            add(s.footPoint, 0.5 * u);
            add(s.footLift, 0.25 * u);
            break;
        }
        case Band::Redline: {
            const double sn = sin(pi * b), u = up(b), h = hit(b);
            add(s.posY, 0.1 * u);
            add(s.squash, 0.2 * u - 0.45 * h);
            add(s.hip, 0.35 * sn);
            add(s.bodyAngleZ, -0.35 * sn);
            add(s.lean, 0.1 * sn);
            add(s.angleZ, 0.35 * sn);
            add(s.angleY, 0.35 - 0.2 * h);
            add(s.angleX, -0.45);
            add(o.arm, 0.4 + 0.12 * sn);
            add(o.forearm, -0.55 - 0.1 * sn);
            add(k.arm, 0.25 + 0.08 * sn);
            add(o.shoulder, 0.35 + 0.2 * sn);
            add(k.shoulder, 0.25 - 0.2 * sn);
            add(s.skirtFlare, 0.4 + 0.25 * u);
            add(s.footPoint, 0.8 * u);
            add(s.footLift, 0.4 * u);
            break;
        }
    }
}

}  // namespace groove
}  // namespace agentspeed::character
