#include "agentspeed/character/moves.hpp"

#include <cmath>
#include <map>

namespace agentspeed::character {

using keys::ballistic;
using keys::cat;
using keys::oscillate;
using K = KeyList;
constexpr double kPi = math::kPi;

namespace gestures {

const std::vector<std::string>& head() {
    static const std::vector<std::string> v = {"nod", "shake", "tilt", "lookaround", "giggle", "doubletake"};
    return v;
}
const std::vector<std::string>& face() {
    static const std::vector<std::string> v = {"yawn", "hum"};
    return v;
}
const std::vector<std::string>& arm() {
    static const std::vector<std::string> v = {"wave", "peace", "fist", "idea", "chin", "hairtuck",
                                               "present", "hip", "hug", "raise", "open"};
    return v;
}
const std::vector<std::string>& combos() {
    static const std::vector<std::string> v = {"cheer", "hooray", "eureka", "ponder", "cuddle", "proud"};
    return v;
}

MotionSpec headGesture(const std::string& name) {
    CurveSet c;
    if (name == "nod") {
        c.set("AngleY", {{0, 0}, {0.18, 0.4}, {0.38, -0.15}, {0.58, 0.35}, {0.8, -0.05}, {1.1, 0}});
        c.set("Squash", {{0, 0}, {0.18, -0.12}, {0.38, 0.04}, {0.58, -0.1}, {0.8, 0}, {1.1, 0}});
        c.set("EyeOpen.left", {{0, 1}, {0.2, 0.7}, {0.6, 0.7}, {1.1, 1}});
        c.set("EyeOpen.right", {{0, 1}, {0.2, 0.7}, {0.6, 0.7}, {1.1, 1}});
        return MotionSpec("nod", 1.1, c, false, 0.12, "smile");
    }
    if (name == "shake") {
        c.set("AngleX", cat({{{0, 0}}, oscillate(0.1, 1.1, 0.42, 0.4), {{1.3, 0}}}));
        c.set("BodyAngleZ", cat({{{0, 0}}, oscillate(0.15, 1.15, 0.42, 0.12), {{1.3, 0}}}));
        c.set("AngleY", {{0, 0}, {0.2, -0.15}, {1.1, -0.15}, {1.3, 0}});
        return MotionSpec("shake", 1.3, c);
    }
    if (name == "tilt") {
        c.set("AngleZ", {{0, 0}, {0.35, 0.7}, {1.8, 0.65}, {2.2, 0}});
        c.set("AngleX", {{0, 0}, {0.35, 0.25}, {1.8, 0.25}, {2.2, 0}});
        c.set("EyeBallX", {{0, 0}, {0.3, 0.5}, {1.8, 0.5}, {2.2, 0}});
        c.set("Hip", {{0, 0}, {0.45, 0.35}, {1.8, 0.35}, {2.2, 0}});
        c.set("BodyAngleZ", {{0, 0}, {0.45, -0.2}, {1.8, -0.2}, {2.2, 0}});
        return MotionSpec("tilt", 2.2, c, false, 0.2, "smile");
    }
    if (name == "lookaround") {
        c.set("EyeBallX", {{0, 0}, {0.2, -0.9}, {0.9, -0.9}, {1.1, 0.9}, {2.2, 0.9}, {2.8, 0}});
        c.set("AngleX", {{0, 0}, {0.45, -0.55}, {1.0, -0.55}, {1.6, 0.55}, {2.3, 0.5}, {2.8, 0}});
        c.set("AngleY", {{0, 0}, {0.45, 0.1}, {1.3, -0.05}, {2.3, 0.1}, {2.8, 0}});
        c.set("Hip", {{0, 0}, {0.5, -0.25}, {1.0, -0.25}, {1.7, 0.25}, {2.3, 0.2}, {2.8, 0}});
        c.set("Lean", {{0, 0}, {0.5, -0.15}, {1.0, -0.15}, {1.7, 0.15}, {2.3, 0.1}, {2.8, 0}});
        return MotionSpec("lookaround", 2.8, c, false, 0.2);
    }
    if (name == "giggle") {
        c.set("BodyAngleZ", cat({{{0, 0}}, oscillate(0.1, 1.4, 0.17, 0.12), {{1.6, 0}}}));
        c.set("Shoulder.left", cat({{{0, 0}}, oscillate(0.1, 1.4, 0.34, 0.25, 0.3), {{1.6, 0}}}));
        c.set("Shoulder.right", cat({{{0, 0}}, oscillate(0.1, 1.4, 0.34, 0.25, 0.3), {{1.6, 0}}}));
        c.set("Squash", cat({{{0, 0}}, oscillate(0.1, 1.4, 0.34, 0.08, -0.05), {{1.6, 0}}}));
        c.set("AngleY", {{0, 0}, {0.2, -0.25}, {1.4, -0.2}, {1.6, 0}});
        c.set("AngleZ", {{0, 0}, {0.2, 0.3}, {1.4, 0.3}, {1.6, 0}});
        c.set("Breath", {{0, 0}, {0.3, 1}, {1.6, 0.3}});
        return MotionSpec("giggle", 1.6, c, false, 0.12, "happy");
    }
    c.set("AngleX", {{0, 0}, {0.15, 0.35}, {0.35, 0.3}, {0.5, -0.1}, {0.65, 0.6}, {1.2, 0.5}, {1.5, 0}});
    c.set("AngleY", {{0, 0}, {0.6, 0.35}, {1.2, 0.3}, {1.5, 0}});
    c.set("EyeBallX", {{0, 0}, {0.6, 0.8}, {1.2, 0.8}, {1.5, 0}});
    c.set("PosY", {{0, 0}, {0.55, 0}, {0.66, 0.07}, {0.8, 0}, {1.5, 0}});
    c.set("Squash", {{0, 0}, {0.5, -0.25}, {0.62, 0.3}, {0.82, -0.15}, {1.0, 0}, {1.5, 0}});
    c.set("Shoulder.left", {{0, 0}, {0.62, 0.6}, {1.2, 0.3}, {1.5, 0}});
    c.set("Shoulder.right", {{0, 0}, {0.62, 0.6}, {1.2, 0.3}, {1.5, 0}});
    return MotionSpec("doubletake", 1.5, c, false, 0.1, "surprised");
}

MotionSpec faceGesture(const std::string& name, double duration) {
    CurveSet c;
    if (name == "yawn") {
        c.set("AngleY", {{0, 0}, {0.5, 0.45}, {1.6, 0.4}, {duration, 0}});
        c.set("AngleZ", {{0, 0}, {0.6, -0.35}, {duration, 0}});
        c.set("Breath", {{0, 0}, {0.7, 1.0}, {1.8, 0.2}, {duration, 0}});
        c.set("Squash", {{0, 0}, {0.7, 0.15}, {1.5, 0.13}, {duration, 0}});
        c.set("Shoulder.left", {{0, 0}, {0.7, 0.9}, {1.6, 0.8}, {duration, 0}});
        c.set("Shoulder.right", {{0, 0}, {0.7, 0.9}, {1.6, 0.8}, {duration, 0}});
        c.set("FootPoint", {{0, 0}, {0.7, 0.5}, {1.5, 0.5}, {duration, 0}});
        c.set("Lean", {{0, 0}, {0.8, 0.1}, {1.6, -0.08}, {duration, 0}});
        return MotionSpec("yawn", duration, c, false, 0.25, "sleepy");
    }
    const double d = duration + 1.0;
    c.set("AngleZ", oscillate(0, d, 1.2, 0.3));
    c.set("AngleX", {{0, 0}, {0.4, -0.25}, {d, 0}});
    c.set("Hip", oscillate(0, d, 1.2, 0.3, 0, kPi));
    c.set("BodyAngleZ", oscillate(0, d, 1.2, 0.15));
    return MotionSpec("hum", d, c, false, 0.25, "smile", true);
}

}  // namespace gestures

namespace moves {

namespace {

std::string sideKey(const char* prefix, Side s) { return std::string(prefix) + "." + name(s); }

void both(CurveSet& c, const char* prefix, const K& k) {
    c.set(std::string(prefix) + ".left", k);
    c.set(std::string(prefix) + ".right", k);
}

K swing(double d, double period, double amplitude, double base = 0, double phase = 0, double ease = 0.15) {
    return cat({{{0, 0}}, oscillate(ease, d - ease, period, amplitude, base, phase), {{d, 0}}});
}

K scaled(const K& hold, double k) {
    K out;
    for (const auto& [t, v] : hold) out.emplace_back(t, v * k);
    return out;
}

MotionSpec hops(Side out, Side book, int count = 3, double height = 0.22, double air = 0.3) {
    const std::string ao = sideKey("Arm", out), ab = sideKey("Arm", book);
    const std::vector<std::string> order = {"PosY", "Squash", "FootPoint", "SkirtFlare", "AngleY", "Hip", ao, ab};
    std::map<std::string, K> k;
    for (const auto& p : order) k[p] = {{0, 0}};
    double t = 0.12;
    k["Squash"].emplace_back(t, -0.4);
    for (int i = 0; i < count; ++i) {
        const double side = i % 2 == 0 ? 1 : -1;
        const double t1 = t + 0.06;
        const double apex = t1 + air / 2;
        k["Squash"].insert(k["Squash"].end(), {{t1, 0.35}, {apex, 0.05}});
        auto arc = ballistic(t1, air, height);
        k["PosY"].insert(k["PosY"].end(), arc.begin(), arc.end());
        k["SkirtFlare"].insert(k["SkirtFlare"].end(), {{apex, 0.5}, {t1 + air, 0.35}});
        k["FootPoint"].insert(k["FootPoint"].end(), {{t1 + 0.06, 1.0}, {t1 + air - 0.05, 0.8}, {t1 + air, 0.0}});
        k["AngleY"].insert(k["AngleY"].end(), {{apex, 0.25}, {t1 + air, -0.15}});
        k["Hip"].emplace_back(apex, 0.3 * side);
        k[ao].emplace_back(apex, 0.4);
        k[ab].emplace_back(apex, 0.2);
        t = t1 + air;
        k["Squash"].emplace_back(t + 0.06, -0.45);
        t += 0.06;
    }
    const double end = t + 0.28;
    CurveSet c;
    for (const auto& p : order) {
        K keysFor = k[p];
        keysFor.emplace_back(end, 0);
        c.set(p, keysFor);
    }
    return MotionSpec("hops", end, c, false, 0.12, "happy");
}

MotionSpec spin(Side out, Side book) {
    const double d = 0.95;
    CurveSet c;
    c.set("Spin", {{0, 0}, {0.12, 0}, {0.68, 1.0}, {d, 1.0}});
    c.set("PosY", cat({{{0, 0}, {0.12, 0}}, ballistic(0.14, 0.52, 0.22), {{d, 0}}}));
    c.set("Squash", {{0, 0}, {0.1, -0.35}, {0.16, 0.3}, {0.42, 0.15}, {0.64, 0.2}, {0.72, -0.4}, {d, 0}});
    c.set("SkirtFlare", {{0, 0}, {0.18, 0.6}, {0.42, 1.0}, {0.7, 0.8}, {d, 0}});
    c.set(sideKey("Arm", out), {{0, 0}, {0.16, 0.5}, {0.62, 0.5}, {d, 0}});
    c.set(sideKey("Arm", book), {{0, 0}, {0.16, 0.3}, {0.62, 0.3}, {d, 0}});
    c.set("AngleZ", {{0, 0}, {0.18, 0.3}, {0.62, 0.3}, {d, 0}});
    c.set("AngleY", {{0, 0}, {0.18, 0.25}, {0.62, 0.2}, {d, 0}});
    c.set("FootPoint", {{0, 0}, {0.16, 1.0}, {0.62, 1.0}, {0.7, 0}, {d, 0}});
    return MotionSpec("spin", d, c, false, 0.12, "happy", false, {{0.25, "petals"}, {0.5, "petals"}});
}

MotionSpec curtsy(Side out, Side book) {
    const double d = 2.0;
    const K hold = {{0, 0}, {0.5, 1.0}, {1.3, 1.0}, {d, 0}};
    CurveSet c;
    c.set("Squash", scaled(hold, -0.17));
    c.set("PosY", scaled(hold, -0.04));
    c.set("AngleY", scaled(hold, -0.6));
    c.set("AngleZ", scaled(hold, 0.25));
    c.set("SkirtFlare", scaled(hold, 0.5));
    c.set("Hip", scaled(hold, -0.2));
    c.set(sideKey("Arm", out), scaled(hold, 0.3));
    c.set(sideKey("Arm", book), scaled(hold, 0.1));
    both(c, "Shoulder", scaled(hold, -0.3));
    return MotionSpec("curtsy", d, c, false, 0.2, "smile");
}

MotionSpec tiptoe(Side out, Side book) {
    const double d = 2.0;
    CurveSet c;
    c.set("FootPoint", {{0, 0}, {0.25, 1.0}, {1.7, 1.0}, {d, 0}});
    c.set("FootLift", {{0, 0}, {0.25, 0.3}, {1.7, 0.3}, {d, 0}});
    c.set("PosY", cat({{{0, 0}, {0.2, 0.04}}, oscillate(0.25, 1.7, 0.5, 0.025, 0.045), {{d, 0}}}));
    c.set("Squash", {{0, 0}, {0.25, 0.1}, {1.7, 0.08}, {d, 0}});
    c.set("Hip", cat({{{0, 0}}, oscillate(0.2, 1.8, 1.0, 0.18), {{d, 0}}}));
    c.set(sideKey("Arm", out), {{0, 0}, {0.3, 0.25}, {1.6, 0.25}, {d, 0}});
    c.set(sideKey("Arm", book), {{0, 0}, {0.3, 0.15}, {1.6, 0.15}, {d, 0}});
    c.set("AngleY", {{0, 0}, {0.3, 0.2}, {1.6, 0.2}, {d, 0}});
    both(c, "Shoulder", {{0, 0}, {0.3, 0.3}, {1.6, 0.3}, {d, 0}});
    return MotionSpec("tiptoe", d, c, false, 0.12, "happy");
}

MotionSpec foottap(Side, Side, double beat = 0.5) {
    K taps = {{0, 0}};
    for (int k = 0; k < 4; ++k) {
        const double t = 0.3 + static_cast<double>(k) * beat;
        taps.insert(taps.end(), {{t, 0}, {t + 0.12, 0.45}, {t + 0.26, 0}});
    }
    const double d = 0.3 + 4 * beat + 0.3;
    CurveSet c;
    c.set("FootLift", cat({taps, {{d, 0}}}));
    c.set("FootPoint", cat({scaled(taps, 0.6), {{d, 0}}}));
    c.set("AngleY", oscillate(0, d, beat * 2, 0.08));
    c.set("Hip", {{0, 0}, {0.3, 0.25}, {d - 0.3, 0.25}, {d, 0}});
    c.set("BodyAngleZ", {{0, 0}, {0.3, -0.15}, {d - 0.3, -0.15}, {d, 0}});
    c.set("AngleZ", oscillate(0, d, beat * 2, 0.12));
    return MotionSpec("foottap", d, c);
}

MotionSpec sway(Side out, Side book, double period = 1.6) {
    const double d = 2 * period;
    const K flare = cat({{{0, 0}}, oscillate(0.4, d - 0.3, period / 2, 0.15, 0.3), {{d, 0}}});
    K bounce = {{0, 0}};
    for (int k = 0; k < 4; ++k) {
        auto arc = ballistic(0.05 + static_cast<double>(k) * period / 2, period / 2 - 0.1, 0.035);
        bounce.insert(bounce.end(), arc.begin(), arc.end());
    }
    CurveSet c;
    c.set("Hip", oscillate(0, d, period, 0.6));
    c.set("BodyAngleZ", oscillate(0, d, period, 0.35, 0, kPi));
    c.set("AngleZ", oscillate(0, d, period, 0.35));
    c.set(sideKey("Arm", out), swing(d, period, 0.35, 0.1));
    c.set(sideKey("Arm", book), oscillate(0, d, period, 0.2, 0, kPi));
    c.set(sideKey("Shoulder", out), oscillate(0, d, period, 0.4));
    c.set(sideKey("Shoulder", book), oscillate(0, d, period, 0.4, 0, kPi));
    c.set("PosY", cat({bounce, {{d, 0}}}));
    c.set("SkirtFlare", flare);
    return MotionSpec("sway", d, c, false, 0.2, "smile", true);
}

MotionSpec run(Side out, Side book, double step = 0.32, int steps = 7) {
    const double d = 0.1 + static_cast<double>(steps) * step + 0.2;
    K pos = {{0, 0}}, squash = {{0, 0}}, foot = {{0, 0}};
    for (int k = 0; k < steps; ++k) {
        const double t = 0.1 + static_cast<double>(k) * step;
        auto arc = ballistic(t, step * 0.8, 0.06);
        pos.insert(pos.end(), arc.begin(), arc.end());
        squash.insert(squash.end(), {{t, -0.25}, {t + step * 0.4, 0.12}});
        if (k % 2 == 0) foot.insert(foot.end(), {{t + 0.04, 0}, {t + 0.14, 0.6}, {t + step - 0.02, 0}});
    }
    pos.emplace_back(d, 0);
    squash.emplace_back(d, 0);
    foot.emplace_back(d, 0);
    CurveSet c;
    c.set("PosY", pos);
    c.set("Squash", squash);
    c.set("FootLift", foot);
    c.set(sideKey("Arm", out), swing(d, 2 * step, 0.4, 0.15));
    c.set(sideKey("Forearm", out), swing(d, 2 * step, 0.3, -0.45, kPi / 2));
    c.set(sideKey("Arm", book), swing(d, 2 * step, 0.3, 0.15, kPi));
    c.set(sideKey("Shoulder", out), oscillate(0, d, 2 * step, 0.3));
    c.set(sideKey("Shoulder", book), oscillate(0, d, 2 * step, 0.3, 0, kPi));
    c.set("Hip", oscillate(0, d, 2 * step, 0.2));
    c.set("Lean", {{0, 0}, {0.3, 0.1}, {d - 0.3, 0.1}, {d, 0}});
    c.set("AngleY", {{0, 0}, {0.3, 0.15}, {d - 0.3, 0.15}, {d, 0}});
    c.set("SkirtFlare", {{0, 0}, {0.3, 0.4}, {d - 0.3, 0.4}, {d, 0}});
    return MotionSpec("run", d, c, false, 0.15, "determined");
}

MotionSpec stretch(Side out, Side book) {
    const double d = 2.8;
    CurveSet c;
    c.set("Squash", {{0, 0}, {0.9, 0.2}, {1.9, 0.18}, {d, 0}});
    c.set("AngleY", {{0, 0}, {0.9, 0.45}, {1.9, 0.4}, {d, 0}});
    c.set("Lean", {{0, 0}, {1.0, 0.2}, {1.5, -0.2}, {2.0, 0}, {d, 0}});
    c.set("BodyAngleZ", {{0, 0}, {1.0, 0.3}, {1.5, -0.3}, {2.0, 0}, {d, 0}});
    c.set("FootPoint", {{0, 0}, {0.9, 0.6}, {1.9, 0.6}, {d, 0}});
    c.set("PosY", {{0, 0}, {0.9, 0.03}, {1.9, 0.03}, {d, 0}});
    c.set(sideKey("Arm", out), {{0, 0}, {0.9, 0.35}, {1.9, 0.35}, {d, 0}});
    c.set(sideKey("Arm", book), {{0, 0}, {0.9, 0.2}, {1.9, 0.2}, {d, 0}});
    both(c, "Shoulder", {{0, 0}, {0.9, 0.9}, {1.9, 0.85}, {d, 0}});
    return MotionSpec("stretch", d, c, false, 0.25, "sleepy");
}

MotionSpec bow(Side, Side) {
    const double d = 1.8;
    CurveSet c;
    c.set("AngleY", {{0, 0}, {0.5, -0.85}, {1.2, -0.85}, {d, 0}});
    c.set("Squash", {{0, 0}, {0.5, -0.15}, {1.2, -0.15}, {d, 0}});
    both(c, "Shoulder", {{0, 0}, {0.5, -0.2}, {1.2, -0.2}, {d, 0}});
    return MotionSpec("bow", d, c, false, 0.2, "smile");
}

MotionSpec tailswish() {
    const double d = 1.6;
    CurveSet c;
    c.set("TailSwish", cat({{{0, 0}}, oscillate(0.1, d - 0.15, 0.7, 0.9), {{d, 0}}}));
    c.set("Hip", cat({{{0, 0}}, oscillate(0.1, d - 0.15, 0.7, 0.18, 0, kPi), {{d, 0}}}));
    c.set("AngleZ", {{0, 0}, {0.3, 0.15}, {d - 0.3, 0.15}, {d, 0}});
    return MotionSpec("tailswish", d, c);
}

MotionSpec wingflap() {
    const double d = 1.5;
    CurveSet c;
    c.set("WingFlap", {{0, 0}, {0.2, -0.25}, {0.42, 1.0}, {0.62, -0.1}, {0.84, 0.9}, {1.06, -0.05}, {1.3, 0.3}, {d, 0}});
    c.set("PosY", {{0, 0}, {0.42, 0.03}, {0.62, 0.0}, {0.84, 0.035}, {1.06, 0.0}, {d, 0}});
    c.set("Squash", {{0, 0}, {0.2, -0.15}, {0.42, 0.12}, {0.62, -0.05}, {0.84, 0.1}, {d, 0}});
    both(c, "Shoulder", {{0, 0}, {0.42, 0.3}, {0.84, 0.25}, {d, 0}});
    return MotionSpec("wingflap", d, c, false, 0.12, std::nullopt, false, {{0.42, "petals"}});
}

MotionSpec eartwitch() {
    const double d = 1.0;
    auto flick = [d](double t0) -> K {
        return {{0, 0}, {t0, 0}, {t0 + 0.06, 1.0}, {t0 + 0.14, -0.35}, {t0 + 0.24, 0.15}, {t0 + 0.36, 0}, {d, 0}};
    };
    CurveSet c;
    c.set("EarTwitch.left", flick(0.08));
    c.set("EarTwitch.right", flick(0.3));
    c.set("AngleZ", {{0, 0}, {0.2, 0.12}, {0.6, -0.08}, {d, 0}});
    c.set("EyeBallX", {{0, 0}, {0.2, 0.5}, {0.6, -0.3}, {d, 0}});
    return MotionSpec("eartwitch", d, c);
}

MotionSpec floatbob(Side out, Side book) {
    const double d = 3.4;
    CurveSet c;
    c.set("PosY", cat({{{0, 0}}, oscillate(0.2, d - 0.3, 1.6, 0.035, 0.03), {{d, 0}}}));
    c.set("Lean", swing(d, 1.7, 0.18));
    c.set("AngleZ", swing(d, 3.2, 0.3));
    c.set("Hip", swing(d, 1.7, 0.22, 0, kPi));
    c.set(sideKey("Arm", out), swing(d, 1.6, 0.15, 0.3, 0, 0.5));
    c.set(sideKey("Arm", book), swing(d, 1.6, 0.1, 0.2, kPi, 0.5));
    c.set("SkirtFlare", {{0, 0}, {0.5, 0.3}, {d - 0.5, 0.3}, {d, 0}});
    c.set("TailSwish", swing(d, 1.7, 0.5));
    c.set("AngleY", {{0, 0}, {0.6, 0.15}, {d - 0.6, 0.15}, {d, 0}});
    return MotionSpec("floatbob", d, c, false, 0.25, "smile");
}

MotionSpec swagger(Side out, Side) {
    const double d = 1.9;
    CurveSet c;
    c.set("Hip", {{0, 0}, {0.4, 0.6}, {1.4, 0.6}, {d, 0}});
    c.set("BodyAngleZ", {{0, 0}, {0.4, -0.35}, {1.4, -0.3}, {d, 0}});
    c.set("Lean", {{0, 0}, {0.4, -0.15}, {1.4, -0.12}, {d, 0}});
    c.set("AngleZ", {{0, 0}, {0.45, 0.35}, {1.4, 0.3}, {d, 0}});
    c.set("AngleY", {{0, 0}, {0.45, 0.2}, {1.4, 0.2}, {d, 0}});
    c.set(sideKey("Shoulder", out), {{0, 0}, {0.4, 0.5}, {0.8, 0.2}, {1.2, 0.5}, {d, 0}});
    c.set(sideKey("Arm", out), {{0, 0}, {0.4, 0.2}, {1.4, 0.2}, {d, 0}});
    c.set("EyeBallX", {{0, 0}, {0.45, 0.6}, {1.4, 0.6}, {d, 0}});
    return MotionSpec("swagger", d, c, false, 0.12, "determined");
}

}  // namespace

const std::vector<std::string>& names() {
    static const std::vector<std::string> v = {"hop", "jump", "hops", "spin", "curtsy", "tiptoe",
                                               "foottap", "sway", "run", "stretch", "bow"};
    return v;
}

std::optional<MotionSpec> make(const std::string& n, Side out, Side book) {
    if (n == "hop") return hop(out, book);
    if (n == "jump") return jump(out, book);
    if (n == "hops") return hops(out, book);
    if (n == "spin") return spin(out, book);
    if (n == "curtsy") return curtsy(out, book);
    if (n == "tiptoe") return tiptoe(out, book);
    if (n == "foottap") return foottap(out, book);
    if (n == "sway") return sway(out, book);
    if (n == "run") return run(out, book);
    if (n == "stretch") return stretch(out, book);
    if (n == "bow") return bow(out, book);
    return std::nullopt;
}

MotionSpec hop(Side out, Side book, double height, double air, const std::string& n, const std::string& expression,
               bool arms) {
    const double t0 = 0.14, t1 = 0.22;
    const double t2 = t1 + air;
    const double t3 = t2 + 0.08;
    const double t4 = t3 + 0.26;
    const double apex = t1 + air / 2;
    CurveSet c;
    c.set("PosY", cat({{{0, 0}}, ballistic(t1, air, height), {{t4, 0}}}));
    c.set("Squash", {{0, 0}, {t0, -0.55}, {t1, 0.45}, {apex, 0.08}, {t2 - 0.02, 0.3}, {t3, -0.6}, {t3 + 0.1, 0.12},
                     {t4, 0}});
    c.set("SkirtFlare", {{0, 0}, {t1, 0.05}, {apex, 0.6}, {t2, 0.45}, {t3, 0.75}, {t4, 0}});
    c.set("FootPoint", {{0, 0}, {t1, 0.2}, {t1 + 0.08, 1.0}, {t2 - 0.06, 0.9}, {t2, 0}, {t4, 0}});
    c.set("FootLift", {{0, 0}, {t1, 0}, {t1 + 0.1, 0.5}, {t2 - 0.08, 0.3}, {t2, 0}, {t4, 0}});
    c.set("AngleY", {{0, 0}, {t0, -0.3}, {t1 + 0.05, 0.35}, {t2, 0.0}, {t3, -0.35}, {t4, 0}});
    both(c, "Shoulder", {{0, 0}, {t0, -0.4}, {t1 + 0.05, 0.7}, {t2, 0.2}, {t3, -0.3}, {t4, 0}});
    if (arms) {
        c.set(sideKey("Arm", out), {{0, 0}, {t0, -0.1}, {t1 + 0.08, 0.35}, {t2, 0.15}, {t3, 0}, {t4, 0}});
        c.set(sideKey("Arm", book), {{0, 0}, {t0, -0.05}, {t1 + 0.08, 0.25}, {t2, 0.1}, {t3, 0}, {t4, 0}});
    }
    return MotionSpec(n, t4, c, false, 0.12, expression, false, {{t1, "petals"}});
}

MotionSpec jump(Side out, Side book, bool arms) { return hop(out, book, 0.45, 0.48, "jump", "happy", arms); }

MotionSpec sidestep(Side out, Side book, double dx) {
    const double d = 1.15;
    const double s = std::signbit(dx) ? -1.0 : 1.0;
    CurveSet c;
    c.set("PosX", {{0, 0}, {0.1, 0}, {0.98, dx}, {d, dx}});
    c.set("PosY", cat({{{0, 0}, {0.12, 0}}, ballistic(0.15, 0.34, 0.08), ballistic(0.57, 0.34, 0.08), {{d, 0}}}));
    c.set("Squash", {{0, 0}, {0.12, -0.25}, {0.18, 0.15}, {0.5, -0.22}, {0.6, 0.15}, {0.92, -0.22}, {d, 0}});
    c.set("Lean", {{0, 0}, {0.15, 0.4 * s}, {0.8, 0.25 * s}, {d, 0}});
    c.set("Hip", {{0, 0}, {0.3, -0.3 * s}, {0.72, 0.3 * s}, {d, 0}});
    c.set("SkirtFlare", {{0, 0}, {0.3, 0.3}, {0.9, 0.2}, {d, 0}});
    c.set("FootLift", {{0, 0}, {0.22, 0.5}, {0.48, 0}, {0.64, 0.5}, {0.9, 0}, {d, 0}});
    c.set("AngleX", {{0, 0}, {0.15, 0.35 * s}, {0.8, 0.35 * s}, {d, 0}});
    c.set(sideKey("Arm", out), {{0, 0}, {0.3, 0.3}, {0.72, -0.05}, {d, 0}});
    c.set(sideKey("Arm", book), {{0, 0}, {0.3, -0.05}, {0.72, 0.2}, {d, 0}});
    return MotionSpec("step", d, c, false, 0.12, std::nullopt, false, {}, dx);
}

MotionSpec peek(Side, Side, double orb) {
    const double d = 2.4;
    const K hold = {{0, 0}, {0.45, 1.0}, {1.9, 1.0}, {d, 0}};
    CurveSet c;
    c.set("Lean", scaled(hold, 0.45 * orb));
    c.set("AngleX", scaled(hold, 0.6 * orb));
    c.set("EyeBallX", scaled(hold, 0.9 * orb));
    c.set("Hip", scaled(hold, -0.3 * orb));
    c.set("AngleZ", scaled(hold, 0.25 * orb));
    c.set("BodyAngleZ", scaled(hold, 0.2 * orb));
    c.set("PosY", {{0, 0}, {0.3, 0}, {0.42, 0.04}, {0.55, 0}, {d, 0}});
    return MotionSpec("peek", d, c, false, 0.2, "surprised");
}

MotionSpec pounce(Side out, Side book, double orb) {
    const double d = 1.25;
    CurveSet c;
    c.set("Squash", {{0, 0}, {0.3, -0.6}, {0.5, -0.62}, {0.56, 0.4}, {0.76, 0.1}, {0.86, -0.45}, {1.0, 0.1}, {d, 0}});
    c.set("PosY", cat({{{0, 0}, {0.5, 0}}, ballistic(0.54, 0.32, 0.22), {{d, 0}}}));
    c.set("Lean", {{0, 0}, {0.3, 0.3 * orb}, {0.5, 0.35 * orb}, {0.7, 0.1 * orb}, {d, 0}});
    c.set(sideKey("Arm", out), {{0, 0}, {0.3, -0.1}, {0.6, 0.55}, {0.9, 0.2}, {d, 0}});
    c.set(sideKey("Forearm", out), {{0, 0}, {0.3, 0.2}, {0.6, -0.4}, {d, 0}});
    c.set(sideKey("Arm", book), {{0, 0}, {0.6, 0.3}, {d, 0}});
    c.set("AngleY", {{0, 0}, {0.3, -0.3}, {0.6, 0.25}, {0.9, -0.1}, {d, 0}});
    c.set("FootPoint", {{0, 0}, {0.54, 0.2}, {0.6, 1.0}, {0.84, 0.8}, {0.86, 0}, {d, 0}});
    c.set("SkirtFlare", {{0, 0}, {0.7, 0.5}, {0.9, 0.6}, {d, 0}});
    c.set("TailSwish", {{0, 0}, {0.3, -0.6}, {0.6, 0.9}, {d, 0}});
    return MotionSpec("pounce", d, c, false, 0.12, "happy", false, {{0.56, "petals"}});
}

const std::map<std::string, std::vector<std::string>>& profile() {
    static const std::map<std::string, std::vector<std::string>> m = {
        {"tailswish", {"TailSwish"}}, {"wingflap", {"WingFlap"}}, {"eartwitch", {"EarTwitch.left", "EarTwitch.right"}},
        {"floatbob", {"PosY"}}, {"swagger", {"Hip"}},
    };
    return m;
}

std::optional<MotionSpec> profileMove(const std::string& n, Side out, Side book) {
    if (n == "tailswish") return tailswish();
    if (n == "wingflap") return wingflap();
    if (n == "eartwitch") return eartwitch();
    if (n == "floatbob") return floatbob(out, book);
    if (n == "swagger") return swagger(out, book);
    return std::nullopt;
}

}  // namespace moves
}  // namespace agentspeed::character
