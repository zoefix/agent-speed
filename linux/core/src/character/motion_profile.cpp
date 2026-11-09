#include "agentspeed/character/motion_profile.hpp"

#include <algorithm>
#include <cmath>

#include "agentspeed/character/hand_actor.hpp"

namespace agentspeed::character {

namespace {

std::optional<std::vector<std::string>> stringList(const json::Value& v) {
    if (!v.isArray()) return std::nullopt;
    std::vector<std::string> out;
    for (const auto& e : v.elements()) {
        auto s = e.string();
        if (!s) return std::nullopt;
        out.push_back(*s);
    }
    return out;
}

bool contains(const std::vector<std::string>& v, const std::string& s) {
    return std::find(v.begin(), v.end(), s) != v.end();
}

}  // namespace

MotionProfile MotionProfile::decode(const json::Value& c) {
    MotionProfile p;
    if (!c.isObject()) return p;
    auto number = [&](std::string_view key, double fallback) {
        auto v = c[key];
        if (!v.isNumber()) return fallback;
        auto d = v.finiteNumber();
        if (!d) return fallback;
        return std::min(std::max(*d, 0.0), 4.0);
    };
    p.tempo = std::max(0.25, number("tempo", 1));
    p.energy = number("energy", 1);
    p.sway = number("sway", 1);
    p.bounce = number("bounce", 1);
    p.gap = std::max(0.25, number("gap", 1));
    p.idle = c["idle"].string().value_or("read");
    p.seated = c["seated"].isBool() ? c["seated"].isTrue() : false;
    p.talk = c["talk"].isBool() ? c["talk"].isTrue() : true;
    for (const auto& s : stringList(c["signature"]).value_or(std::vector<std::string>{})) {
        if (actionBands().count(s)) p.signature.push_back(s);
    }
    p.exclude = stringList(c["exclude"]).value_or(std::vector<std::string>{});
    for (const auto& s : stringList(c["hands"]).value_or(std::vector<std::string>{})) {
        if (contains(HandActor::sets(), s)) p.hands.push_back(s);
    }
    auto poseHands = c["poseHands"];
    if (poseHands.isObject()) {
        std::map<std::string, std::string> raw;
        bool ok = true;
        for (const auto& [k, v] : poseHands.members()) {
            auto s = v.string();
            if (!s) {
                ok = false;
                break;
            }
            raw[k] = *s;
        }
        if (ok) {
            for (const auto& [k, v] : raw) {
                if (bandNamed(k) && contains(HandActor::sets(), v)) p.poseHands[k] = v;
            }
        }
    }
    p.particles = c["particles"].string();
    return p;
}

const std::map<std::string, std::pair<Band, Band>>& MotionProfile::actionBands() {
    using B = Band;
    static const std::map<std::string, std::pair<Band, Band>> m = {
        {"yawn", {B::Idle, B::Cruise}}, {"stretch", {B::Idle, B::Cruise}}, {"ponder", {B::Idle, B::Cruise}},
        {"bow", {B::Idle, B::Boost}}, {"curtsy", {B::Idle, B::Boost}}, {"nod", {B::Idle, B::Boost}},
        {"tilt", {B::Idle, B::Boost}}, {"lookaround", {B::Idle, B::Boost}}, {"shake", {B::Idle, B::Boost}},
        {"foottap", {B::Idle, B::Boost}}, {"floatbob", {B::Idle, B::Boost}}, {"hum", {B::Cruise, B::Boost}},
        {"sway", {B::Cruise, B::Boost}}, {"tiptoe", {B::Cruise, B::Boost}}, {"peek", {B::Cruise, B::Boost}},
        {"giggle", {B::Cruise, B::Overdrive}}, {"doubletake", {B::Cruise, B::Overdrive}},
        {"step", {B::Idle, B::Overdrive}}, {"swagger", {B::Cruise, B::Overdrive}}, {"hop", {B::Cruise, B::Redline}},
        {"hops", {B::Boost, B::Redline}}, {"spin", {B::Boost, B::Redline}}, {"pounce", {B::Boost, B::Redline}},
        {"run", {B::Overdrive, B::Redline}}, {"jump", {B::Overdrive, B::Redline}},
        {"jumpjoy", {B::Overdrive, B::Redline}}, {"tailswish", {B::Idle, B::Redline}},
        {"wingflap", {B::Idle, B::Redline}}, {"eartwitch", {B::Idle, B::Redline}},
    };
    return m;
}

const std::set<std::string>& MotionProfile::seatedSkip() {
    static const std::set<std::string> s = {"hop", "jump", "hops", "spin", "step", "run", "jumpjoy", "tiptoe",
                                            "foottap", "curtsy", "pounce", "floatbob"};
    return s;
}

const std::vector<std::string>& MotionProfile::seatedFreeze() {
    static const std::vector<std::string> s = {"PosX", "PosY", "Squash", "Lean", "Hip", "FootLift", "FootPoint",
                                               "BodySway", "SkirtFlare"};
    return s;
}

const std::set<std::string>& MotionProfile::twoHanded() {
    static const std::set<std::string> s = {"flute", "grip"};
    return s;
}

const std::set<std::string>& MotionProfile::atMouth() {
    static const std::set<std::string> s = {"flute"};
    return s;
}

bool MotionProfile::allows(const std::string& action) const {
    if (contains(exclude, action)) return false;
    if (seated && seatedSkip().count(action)) return false;
    if (!talk && (action == "hum" || action == "giggle")) return false;
    return true;
}

std::vector<std::string> MotionProfile::pool(const std::vector<std::string>& base, Band band) const {
    std::vector<std::string> out;
    for (const auto& a : base) {
        if (allows(a)) out.push_back(a);
    }
    for (const auto& a : signature) {
        auto it = actionBands().find(a);
        if (it == actionBands().end() || !allows(a)) continue;
        const auto [lo, hi] = it->second;
        if (!(lo <= band && band <= hi)) continue;
        const int copies = contains(base, a) ? 2 : 3;
        for (int i = 0; i < copies; ++i) out.push_back(a);
    }
    return out;
}

double MotionProfile::grooveScale(const std::string& n) const {
    if (n == "PosY" || n == "Squash" || n == "FootPoint" || n == "FootLift" || n == "SkirtFlare") return bounce;
    if (n == "Hip" || n == "BodyAngleZ" || n == "Lean" || n == "AngleZ") return sway;
    if (n.starts_with("Arm.") || n.starts_with("Forearm.") || n.starts_with("Shoulder.")) return energy;
    return 1;
}

std::optional<std::string> MotionProfile::poseHand(Band band) const {
    auto it = poseHands.find(name(band));
    if (it == poseHands.end()) return std::nullopt;
    return it->second;
}

}  // namespace agentspeed::character
