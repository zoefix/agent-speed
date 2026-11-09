#include "agentspeed/character/hand_actor.hpp"

#include <algorithm>
#include <cmath>
#include <map>

#include "agentspeed/character/motion_profile.hpp"

namespace agentspeed::character {

using keys::cat;
using keys::oscillate;
using K = KeyList;

namespace {

MotionSpec stagger(const std::string& name, double d, const K& perFinger, const std::vector<int>& order, double gap) {
    CurveSet c;
    for (std::size_t k = 0; k < order.size(); ++k) {
        const double dt = static_cast<double>(k) * gap;
        K keys = {{0, 0}};
        for (const auto& [t, v] : perFinger) keys.emplace_back(t + dt, v);
        keys.emplace_back(d + dt + 0.2, 0);
        c.set("F" + std::to_string(order[k]), keys);
    }
    return MotionSpec(name, d + gap * static_cast<double>(order.size() - 1) + 0.25, c);
}

MotionSpec tap(double beat) {
    const K one = {{0.08, -0.25}, {0.18, 0.3}, {0.3, 0.0}};
    K keys = one;
    for (const auto& [t, v] : one) keys.emplace_back(t + beat, v);
    return stagger("tap", 0.3 + beat, keys, {1, 2}, 0.12);
}

MotionSpec regrip() {
    auto m = stagger("regrip", 0.7, {{0.2, 0.32}, {0.45, 0.28}, {0.7, 0.0}}, {4, 3, 2, 1, 0}, 0.05);
    m.curves.set("W", {{0, 0}, {0.25, -0.12}, {0.8, 0}});
    return m;
}

MotionSpec lift() { return stagger("lift", 0.6, {{0.15, -0.22}, {0.35, -0.22}, {0.6, 0.0}}, {1, 2, 3, 4}, 0.07); }

MotionSpec flex() {
    auto m = stagger("flex", 1.2, {{0.25, 0.4}, {0.55, 0.35}, {0.85, -0.15}, {1.2, 0.0}}, {4, 3, 2, 1, 0}, 0.06);
    m.curves.set("S", {{0, 0}, {0.6, 0}, {0.95, 0.45}, {1.5, 0}});
    m.curves.set("W", {{0, 0}, {0.3, 0.2}, {0.9, -0.15}, {1.5, 0}});
    return m;
}

MotionSpec fan() {
    auto m = stagger("fan", 1.0, {{0.3, -0.18}, {0.7, -0.15}, {1.0, 0.0}}, {0, 1, 2, 3, 4}, 0.04);
    m.curves.set("S", {{0, 0}, {0.35, 0.6}, {0.75, 0.5}, {1.2, 0}});
    m.curves.set("W", {{0, 0}, {0.35, -0.2}, {1.2, 0}});
    return m;
}

MotionSpec count(double beat) {
    CurveSet c;
    for (int i = 0; i < 5; ++i) {
        const double t = 0.1 + static_cast<double>(i) * beat;
        c.set("F" + std::to_string(i), {{0, 0}, {t, 0}, {t + 0.12, 0.42}, {5 * beat + 0.2, 0.42}, {5 * beat + 0.55, 0.0}});
    }
    K w = {{0, 0}};
    for (int i = 0; i < 5; ++i) w.emplace_back(0.1 + static_cast<double>(i) * beat + 0.12, 0.06 * (i % 2 == 0 ? 1.0 : -1.0));
    w.emplace_back(5 * beat + 0.6, 0);
    c.set("W", w);
    return MotionSpec("count", 5 * beat + 0.6, c);
}

MotionSpec drum(double beat, int bars = 2) {
    std::map<int, K> keys = {{1, {{0, 0}}}, {2, {{0, 0}}}, {3, {{0, 0}}}};
    double t = 0.05;
    for (int n = 0; n < 4 * bars; ++n) {
        for (const auto& [i, dt] : std::vector<std::pair<int, double>>{{3, 0.0}, {2, 0.06}, {1, 0.12}}) {
            keys[i].insert(keys[i].end(), {{t + dt, -0.15}, {t + dt + 0.07, 0.3}, {t + dt + 0.16, 0.0}});
        }
        t += beat;
    }
    CurveSet c;
    for (int i : {1, 2, 3}) c.set("F" + std::to_string(i), cat({keys[i], {{t + 0.2, 0}}}));
    return MotionSpec("drum", t + 0.2, c);
}

MotionSpec flute(double beat) {
    const std::vector<std::vector<int>> pattern = {{1}, {1, 2}, {2, 3}, {1, 3}, {2}, {1, 2, 3}, {3}, {1, 2}};
    const double step = 0.5 * beat;
    std::map<int, K> keys = {{1, {{0, 0}}}, {2, {{0, 0}}}, {3, {{0, 0}}}};
    for (std::size_t k = 0; k < pattern.size(); ++k) {
        const double t = 0.05 + static_cast<double>(k) * step;
        for (int i : {1, 2, 3}) {
            const bool lifted = std::find(pattern[k].begin(), pattern[k].end(), i) != pattern[k].end();
            const double v = lifted ? -0.3 : 0.12;
            keys[i].insert(keys[i].end(), {{t + 0.06, v}, {t + step - 0.04, v}});
        }
    }
    const double end = 0.05 + static_cast<double>(pattern.size()) * step + 0.1;
    CurveSet c;
    for (int i : {1, 2, 3}) c.set("F" + std::to_string(i), cat({keys[i], {{end, 0}}}));
    return MotionSpec("flute", end, c);
}

MotionSpec fanflutter(double beat) {
    const double d = 1.9;
    CurveSet c;
    c.set("W", cat({{{0, 0}}, oscillate(0.1, d - 0.1, 0.6, 0.22), {{d, 0}}}));
    c.set("S", {{0, 0}, {0.3, 0.15}, {d - 0.3, 0.15}, {d, 0}});
    return MotionSpec("fanflutter", d + 0.5 * beat, c);
}

MotionSpec abacus(double beat) {
    const double step = std::max(0.5 * beat, 0.24);
    K f1 = {{0, 0}}, f0 = {{0, 0}};
    for (int k = 0; k < 6; ++k) {
        const double t = 0.05 + static_cast<double>(k) * step;
        f1.insert(f1.end(), {{t, 0.0}, {t + 0.05, 0.5}, {t + 0.11, -0.12}, {t + 0.2, 0.0}});
        if (k % 2 == 0) f0.insert(f0.end(), {{t, 0.0}, {t + 0.06, 0.25}, {t + 0.16, 0.0}});
    }
    const double end = 0.05 + 6 * step + 0.1;
    CurveSet c;
    c.set("F1", cat({f1, {{end, 0}}}));
    c.set("F0", cat({f0, {{end, 0}}}));
    c.set("W", cat({{{0, 0}}, oscillate(0.05, end - 0.05, 2 * step, 0.05), {{end, 0}}}));
    return MotionSpec("abacus", end, c);
}

MotionSpec sleeveflop(double) {
    const double d = 2.4;
    const K droop = {{0, 0}, {0.4, 0.2}, {d - 0.4, 0.2}, {d, 0}};
    CurveSet c;
    c.set("W", cat({{{0, 0}}, oscillate(0.1, d - 0.1, 1.6, 0.3), {{d, 0}}}));
    for (int i : {1, 2, 3, 4}) c.set("F" + std::to_string(i), droop);
    return MotionSpec("sleeveflop", d, c);
}

MotionSpec ripple(double) {
    CurveSet c;
    c.set("S", {{0, 0}, {2.0, 0}});
    return MotionSpec("ripple", 2.0, c);
}

MotionSpec grip(double beat) {
    const double d = 2 * beat;
    CurveSet c;
    for (int i : {1, 2, 3, 4}) c.set("F" + std::to_string(i), {{0, 0}, {0.35 * d, 0.1}, {0.6 * d, 0.1}, {d, 0}});
    return MotionSpec("grip", d, c);
}

}  // namespace

HandActor::HandActor(Side s, bool h, double p, Rng& rng) : side(s), holds(h), phase(p) { next = rng.uniform(1.0, 3.0); }

const std::vector<std::string>& HandActor::sets() {
    static const std::vector<std::string> v = {"tap", "regrip", "lift", "flex", "fan", "count", "drum",
                                               "flute", "fanflutter", "abacus", "sleeveflop", "ripple", "grip"};
    return v;
}

MotionSpec HandActor::make(const std::string& n, double beat) {
    if (n == "tap") return tap(beat);
    if (n == "regrip") return regrip();
    if (n == "lift") return lift();
    if (n == "flex") return flex();
    if (n == "fan") return fan();
    if (n == "count") return count(beat);
    if (n == "drum") return drum(beat);
    if (n == "flute") return flute(beat);
    if (n == "fanflutter") return fanflutter(beat);
    if (n == "abacus") return abacus(beat);
    if (n == "sleeveflop") return sleeveflop(beat);
    if (n == "grip") return grip(beat);
    return ripple(beat);
}

Motion HandActor::bind(const MotionSpec& spec) {
    return spec.bind([](const std::string& n) -> std::optional<int> {
        if (n == "W") return kWristSlot;
        if (n == "S") return kSpreadSlot;
        if (n.size() < 2) return std::nullopt;
        return std::stoi(n.substr(1));
    });
}

const char* HandActor::modeSides(const std::string& n) {
    if (n == "flute" || n == "sleeveflop" || n == "ripple" || n == "grip") return "both";
    if (n == "fanflutter" || n == "tap" || n == "regrip" || n == "lift") return "holder";
    return "free";
}

bool HandActor::modeOn() const {
    if (!mode) return false;
    const std::string sides = modeSides(*mode);
    return sides == "both" || (sides == "holder") == holds;
}

void HandActor::setMode(const std::optional<std::string>& m) {
    mode = m;
    if (modeOn() && !action) next = std::min(next, 0.0);
}

Motion HandActor::choose(Band band, double beat, Rng& rng) {
    if (modeOn() && mode) return bind(make(*mode, beat));
    if (holds) {
        switch (rng.index(3)) {
            case 0: return bind(tap(beat));
            case 1: return bind(regrip());
            default: return bind(lift());
        }
    }
    if (!freeSets.empty()) {
        std::vector<MotionSpec> options;
        for (const auto& s : freeSets) options.push_back(make(s, beat));
        return bind(options[static_cast<std::size_t>(rng.index(static_cast<int>(options.size())))]);
    }
    const bool busy = band == Band::Boost || band == Band::Overdrive || band == Band::Redline;
    switch (rng.index(busy ? 4 : 2)) {
        case 0: return bind(flex());
        case 1: return bind(fan());
        case 2: return bind(count(beat));
        default: return bind(drum(beat));
    }
}

double HandActor::busyness(Band band) {
    switch (band) {
        case Band::Idle: return 0.5;
        case Band::Cruise: return 0.8;
        case Band::Boost: return 1.1;
        case Band::Overdrive: return 1.4;
        case Band::Redline: return 1.7;
    }
    return 0.5;
}

void HandActor::update(double dt, double time, Band band, double beat, double arm, Rng& rng) {
    constexpr double pi = math::kPi;
    const double rest = holds ? 0.06 : 0.14;
    const double amp = holds ? 0.04 : 0.07;
    for (int i = 0; i < 5; ++i) {
        fingers[static_cast<std::size_t>(i)] = rest + amp * std::sin(2 * pi * time / 3.4 - 0.55 * static_cast<double>(i) + phase);
    }
    spread = 0.08 * std::sin(2 * pi * time / 4.6 + 1.1 + phase);
    const bool twoHanded = mode && MotionProfile::twoHanded().count(*mode);
    const double sway = twoHanded ? 0.0 : (holds ? 0.03 : 0.09);
    wrist = sway * std::sin(2 * pi * time / 5.3 + phase);

    if (previousArm) {
        const double vel = (arm - *previousArm) / std::max(dt, 1e-6);
        trail += (std::max(-1.0, std::min(1.0, -0.35 * vel)) - trail) * std::min(1.0, dt * 10);
    }
    previousArm = arm;
    wrist += trail * (holds ? 0.25 : 0.6);
    for (auto& f : fingers) f -= std::fabs(trail) * 0.25;

    next -= dt * busyness(band);
    if (!action && next <= 0) {
        modeAction = modeOn();
        action = choose(band, beat, rng);
        t = 0;
    }
    if (action) {
        t += dt;
        const double local = action->localTime(t);
        const double wristScale = modeAction || !holds ? 1.0 : 0.3;
        for (const auto& entry : action->entries) {
            const double v = entry.curve(local);
            switch (entry.slot) {
                case kWristSlot: wrist += v * wristScale; break;
                case kSpreadSlot: spread += v; break;
                default: fingers[static_cast<std::size_t>(entry.slot)] += v; break;
            }
        }
        if (t >= action->duration) {
            action.reset();
            next = modeOn() ? 0 : rng.uniform(1.6, 4.2);
        }
    }
}

}  // namespace agentspeed::character
