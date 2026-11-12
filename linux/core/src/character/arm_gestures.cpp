#include "agentspeed/character/arm_gestures.hpp"

#include <array>
#include <cmath>

namespace agentspeed::character::arm_gestures {

namespace {

using keys::cat;
using keys::oscillate;

std::array<double, 7> timeline(double hold) {
    const double t1 = 0.22;
    const double t2 = t1 + 0.1;
    const double t3 = t2 + 0.32;
    const double t4 = t3 + hold;
    const double t5 = t4 + 0.24;
    const double t6 = t5 + 0.1;
    const double t7 = t6 + 0.36;
    return {t1, t2, t3, t4, t5, t6, t7};
}

}  // namespace

const std::map<std::string, ArmSpec>& arms() {
    static const std::map<std::string, ArmSpec> m = {
        {"wave", {1.6, "wave", "smile", true}},       {"peace", {1.3, "bob", "happy", true}},
        {"fist", {1.2, "pump", "determined", false}}, {"idea", {1.4, "pop", "surprised", true}},
        {"chin", {2.4, "think", std::nullopt, false}}, {"hairtuck", {1.6, "slow", "smile", true}},
        {"present", {1.8, "offer", "talk", false}},   {"hip", {1.8, "lean", "determined", false}},
        {"hug", {2.4, "sway", "smile", true}},        {"raise", {1.4, "pump", "happy", false}},
        {"open", {3.4, "read", std::nullopt, false}}, {"thumbsup", {1.4, "bob", "smile", false}},
        {"shh", {1.6, "slow", "smile", false}},       {"cheek", {1.8, "slow", "shy", false}},
        {"salute", {1.2, "bob", "smile", false}},     {"reach", {1.6, "slow", "sleepy", true}},
    };
    return m;
}

const std::map<std::string, ComboSpec>& combos() {
    static const std::map<std::string, ComboSpec> m = {
        {"cheer", {"raise", "fist", 1.4, "happy"}},     {"hooray", {"raise", "wave", 1.6, "happy"}},
        {"eureka", {"open", "idea", 1.8, "surprised"}}, {"ponder", {"open", "chin", 2.4, std::nullopt}},
        {"cuddle", {"hug", "peace", 1.6, "happy"}},     {"proud", {"hug", "hip", 1.8, "determined"}},
    };
    return m;
}

std::optional<MotionSpec> armGesture(const RigDocument::Variant& meta, double hold, const std::string& flourish,
                                     const std::optional<std::string>& expression, bool tiptoe) {
    if (!meta.arm || !meta.forearm || !meta.side) return std::nullopt;
    const std::string& arm = *meta.arm;
    const std::string& fore = *meta.forearm;
    const std::string& side = *meta.side;
    const double sign = side == "right" ? 1.0 : -1.0;
    const auto [t1, t2, t3, t4, t5, t6, t7] = timeline(hold);
    CurveSet c;
    c.set(meta.param, {{0, 0}, {t1, 0}, {t2, 1}, {t5, 1}, {t6, 0}, {t7, 0}});
    c.set("Arm." + side, {{0, 0}, {t7, 0}});
    c.set("Forearm." + side, {{0, 0}, {t7, 0}});
    if (meta.lead) {
        for (const auto& [pid, lead] : *meta.lead) {
            const double dip = std::fabs(lead) > 0.2 ? -0.12 * (std::signbit(lead) ? -1.0 : 1.0) : 0.0;
            c.set(pid, {{0, 0}, {0.08, dip}, {t1 - 0.04, lead}, {t6 + 0.02, lead}, {t7, 0}});
        }
    }
    for (const std::string* pid : {&arm, &fore}) {
        double a = 0;
        if (meta.approach) {
            if (auto it = meta.approach->find(*pid); it != meta.approach->end()) a = it->second;
        }
        c.set(*pid, {{0, a}, {t1, a}, {t3, 0}, {t4, 0}, {t5 - 0.04, a}, {t7, a}});
    }
    auto during = [&c](const std::string& pid, const KeyList& middle) {
        const auto* old = c.find(pid);
        if (!old || old->size() < 6) return;
        std::vector<CurveKey> k(old->begin(), old->begin() + 3);
        for (const auto& [t, v] : middle) k.push_back({t, v});
        k.insert(k.end(), old->end() - 3, old->end());
        c.assign(pid, std::move(k));
    };

    const double lift = meta.front.value_or(false) ? 0.6 : 0.25;
    CurveSet body;
    body.set("Shoulder." + side, {{0, 0}, {t1, 0.3 * lift}, {t3, lift}, {t4, 0.9 * lift}, {t6, 0.3 * lift}, {t7, 0}});
    body.set("Hip", {{0, 0}, {t3, -0.3 * sign}, {t4, -0.3 * sign}, {t7, 0}});
    body.set("BodyAngleZ", {{0, 0}, {t3, 0.2 * sign}, {t4, 0.2 * sign}, {t7, 0}});
    if (tiptoe) {
        body.set("FootPoint", {{0, 0}, {t2, 0}, {t3, 0.8}, {t4, 0.8}, {t5, 0}, {t7, 0}});
        body.set("FootLift", {{0, 0}, {t2, 0}, {t3, 0.25}, {t4, 0.25}, {t5, 0}, {t7, 0}});
        body.set("PosY", {{0, 0}, {t2, 0}, {t3, 0.025}, {t4, 0.025}, {t5, 0}, {t7, 0}});
    }

    CurveSet head;
    if (flourish == "wave") {
        during(fore, oscillate(t3, t4, 0.42, 0.45));
        head.set("AngleZ", {{0, 0}, {t3, 0.25}, {t4, 0.25}, {t7, 0}});
        head.set("AngleX", {{0, 0}, {t3, -0.3}, {t4, -0.3}, {t7, 0}});
    } else if (flourish == "bob") {
        during(arm, oscillate(t3, t4, 0.5, 0.06));
        head.set("AngleZ", {{0, 0}, {t3, 0.45}, {t4, 0.45}, {t7, 0}});
    } else if (flourish == "pump") {
        during(arm, oscillate(t3, t4, 0.34, 0.12));
        head.set("AngleY", {{0, 0}, {t3, 0.4}, {t4, 0.4}, {t7, 0}});
        head.set("BodyAngleZ", {{0, 0}, {t3, -0.4 * sign}, {t4, -0.4 * sign}, {t7, 0}});
        body.set("PosY", cat({{{0, 0}, {t2, 0}}, oscillate(t3, t4, 0.68, 0.03, 0.03), {{t5, 0}, {t7, 0}}}));
    } else if (flourish == "pop") {
        during(fore, {{t3 + 0.12, -0.2}, {t3 + 0.3, 0}});
        head.set("AngleY", {{0, 0}, {t3, 0.5}, {t4, 0.45}, {t7, 0}});
        head.set("EyeBallY", {{0, 0}, {t3, -0.7}, {t4, -0.7}, {t7, 0}});
    } else if (flourish == "think") {
        head.set("AngleZ", {{0, 0}, {t3, 0.35}, {t4, 0.4}, {t7, 0}});
        head.set("AngleY", {{0, 0}, {t3, 0.25}, {t4, 0.2}, {t7, 0}});
        head.set("EyeBallY", {{0, 0}, {t3, -0.8}, {t4, -0.8}, {t7, 0}});
        head.set("EyeBallX", {{0, 0}, {t3, 0.5}, {t4, 0.6}, {t7, 0}});
    } else if (flourish == "slow") {
        during(fore, {{t3 + hold * 0.5, 0.15}});
        head.set("AngleZ", {{0, 0}, {t3, 0.4}, {t4, 0.4}, {t7, 0}});
        head.set("EyeBallX", {{0, 0}, {t3, 0.4}, {t4, 0.4}, {t7, 0}});
    } else if (flourish == "offer") {
        during(arm, {{t3 + hold * 0.5, -0.05}});
        head.set("AngleX", {{0, 0}, {t3, 0.45}, {t4, 0.45}, {t7, 0}});
        head.set("EyeBallX", {{0, 0}, {t3, 0.7}, {t4, 0.7}, {t7, 0}});
        body.set("Lean", {{0, 0}, {t3, 0.25 * sign}, {t4, 0.25 * sign}, {t7, 0}});
    } else if (flourish == "lean") {
        head.set("BodyAngleZ", {{0, 0}, {t3, 0.55 * sign}, {t4, 0.55 * sign}, {t7, 0}});
        head.set("AngleZ", {{0, 0}, {t3, -0.3 * sign}, {t4, -0.3 * sign}, {t7, 0}});
        body.set("Hip", {{0, 0}, {t3, 0.55 * sign}, {t4, 0.55 * sign}, {t7, 0}});
    } else if (flourish == "sway") {
        head.set("AngleZ", {{0, 0}, {t3, -0.3}, {t3 + hold / 2, 0.2}, {t4, -0.3}, {t7, 0}});
        head.set("BodyAngleZ", oscillate(0, t7, 1.6, 0.25));
        body.set("Hip", oscillate(0, t7, 1.6, 0.3, 0, math::kPi));
    } else if (flourish == "read") {
        head.set("AngleY", {{0, 0}, {t3, -0.6}, {t4, -0.6}, {t7, 0}});
        head.set("EyeBallY", {{0, 0}, {t3, 0.6}, {t4, 0.6}, {t7, 0}});
        head.set("EyeBallX", cat({{{0, 0}, {t3, -0.6}}, oscillate(t3, t4, 1.1, 0.5, 0), {{t7, 0}}}));
    }
    if (flourish != "wave" && flourish != "pop" && flourish != "slow") during(fore, oscillate(t3, t4, 1.3, 0.04));
    for (std::size_t i = 0; i < body.names().size(); ++i) c.assign(body.names()[i], body.keys()[i]);
    for (std::size_t i = 0; i < head.names().size(); ++i) c.assign(head.names()[i], head.keys()[i]);
    return MotionSpec(meta.id, t7, std::move(c), false, 0.12, expression);
}

std::optional<MotionSpec> combo(const std::string& name, const RigDocument::Variant& book, const RigDocument::Variant& free,
                                double hold, const std::optional<std::string>& expression) {
    const auto a = arms().find(book.id), b = arms().find(free.id);
    if (a == arms().end() || b == arms().end()) return std::nullopt;
    auto first = armGesture(book, hold, a->second.flourish, std::nullopt);
    if (!first) return std::nullopt;
    auto second = armGesture(free, hold, b->second.flourish, expression);
    if (!second) return std::nullopt;
    return keys::merge(name, {&*first, &*second}, {0, 0.1}, expression);
}

}  // namespace agentspeed::character::arm_gestures
