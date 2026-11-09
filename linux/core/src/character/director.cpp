#include "agentspeed/character/director.hpp"

#include <algorithm>
#include <cmath>
#include <map>

#include "agentspeed/character/moves.hpp"

namespace agentspeed::character {

namespace {

const std::vector<std::string>& poolOf(Band b) {
    static const std::map<Band, std::vector<std::string>> pools = {
        {Band::Idle, {"open", "chin", "hairtuck", "hug", "yawn", "stretch", "lookaround", "tilt", "foottap", "ponder",
                      "step", "bow"}},
        {Band::Cruise, {"wave", "hug", "present", "hairtuck", "hum", "nod", "tilt", "giggle", "sway", "tiptoe", "curtsy",
                        "step", "peek", "cuddle"}},
        {Band::Boost, {"idea", "peace", "hip", "wave", "hop", "sway", "tiptoe", "step", "spin", "giggle", "doubletake",
                       "hooray", "eureka", "hops"}},
        {Band::Overdrive, {"fist", "present", "hip", "idea", "hops", "run", "spin", "step", "doubletake", "cheer", "proud",
                           "hop"}},
        {Band::Redline, {"fist", "raise", "peace", "jumpjoy", "run", "spin", "hops", "cheer", "hooray", "jump"}},
    };
    return pools.at(b);
}

std::pair<double, double> gapOf(Band b) {
    switch (b) {
        case Band::Idle: return {2.4, 4.4};
        case Band::Cruise: return {0.9, 2.2};
        case Band::Boost: return {0.7, 1.8};
        case Band::Overdrive: return {0.4, 1.3};
        case Band::Redline: return {0.3, 1.0};
    }
    return {2.4, 4.4};
}

const std::vector<std::string> kCelebrations = {"jumpjoy", "spin", "cheer", "hop"};
const std::vector<std::string> kHard = {"Var.", "Lift.", "Bend.", "Spin", "PosX"};
constexpr double kStage = 0.55;
const std::vector<std::string> kAirborne = {"hop", "jump", "hops", "spin", "step", "run", "jumpjoy", "pounce"};
const std::vector<std::string> kArmParams = {"Arm.", "Forearm.", "Shoulder.", "Lift.", "Bend."};
const std::vector<std::string> kHeadParams = {"AngleX", "AngleY", "AngleZ"};

bool contains(const std::vector<std::string>& v, const std::string& s) {
    return std::find(v.begin(), v.end(), s) != v.end();
}

}  // namespace

Director::Director(const std::vector<Param>& params, std::uint64_t seed, Side out, Side book, double orb, bool celebrate,
                   MotionProfile profile)
    : space_(params), slots_(space_), profile_(std::move(profile)), out_(out), book_(book), orb_(orb),
      celebrate_(celebrate), rng_(seed), beat_(profile_.tempo) {
    const auto n = static_cast<std::size_t>(space_.count());
    for (const auto& name : space_.names()) grooveScale_.push_back(profile_.grooveScale(name));
    if (profile_.seated) {
        for (const auto& f : MotionProfile::seatedFreeze()) {
            if (space_.isParam(f)) frozen_.push_back(*space_.slot(f));
        }
    }
    for (std::size_t i = 0; i < n; ++i) {
        if (!space_.params()[i]) continue;
        const auto& nm = space_.names()[i];
        if (std::any_of(kArmParams.begin(), kArmParams.end(), [&](const std::string& p) { return nm.starts_with(p); })) {
            armSlots_.push_back(static_cast<int>(i));
        }
    }
    const auto idleHand = profile_.poseHand(Band::Idle);
    armsHeld_ = idleHand && MotionProfile::twoHanded().count(*idleHand);
    for (const auto& h : kHeadParams) {
        if (space_.isParam(h)) headSlots_.push_back(*space_.slot(h));
    }
    headHeld_ = idleHand && MotionProfile::atMouth().count(*idleHand);
    values_.assign(n, 0);
    present_.assign(n, false);
    grooveSum_.assign(n, 0);
    grooveWeight_.assign(n, 0);
    grooveSeen_.assign(n, false);
    for (const auto& s : space_.springs()) {
        springs_.push_back(s ? std::optional<SecondOrder>(SecondOrder(*s)) : std::nullopt);
    }
    springLive_.assign(n, false);
    grooveOut_.reserve(32);
    grooveTouched_.reserve(32);
    events.reserve(8);
    int k = 0;
    for (Side side : {Side::Left, Side::Right}) {
        if (space_.isParam(std::string("Finger.") + name(side) + ".0")) {
            HandActor hand(side, side == book, static_cast<double>(k) * 1.7, rng_);
            hand.freeSets = profile_.hands;
            hand.setMode(idleHand);
            hands_.push_back(std::move(hand));
        }
        ++k;
    }
}

void Director::setSides(Side out, Side book, std::optional<Band> poseBand) {
    out_ = out;
    book_ = book;
    if (poseBand) poseBand_ = *poseBand;
    const auto hand = profile_.poseHand(poseBand_);
    for (auto& h : hands_) {
        h.holds = h.side == book;
        h.setMode(hand);
    }
    armsHeld_ = hand && MotionProfile::twoHanded().count(*hand);
    headHeld_ = hand && MotionProfile::atMouth().count(*hand);
    for (auto& g : grooves_) g.target = 0;
    grooves_.push_back({band_.value_or(Band::Idle), 0, 1});
}

bool Director::can(std::initializer_list<const char*> names) const {
    return std::all_of(names.begin(), names.end(), [&](const char* n) { return space_.isParam(std::string(n)); });
}

std::optional<MotionSpec> Director::spec(const std::string& gid) {
    if (contains(gestures::arm(), gid) || contains(gestures::combos(), gid)) return std::nullopt;
    if (grounded && contains(kAirborne, gid)) return std::nullopt;
    if (gid == "jumpjoy") return can({"PosY", "Squash"}) ? std::optional(moves::jump(out_, book_, true)) : std::nullopt;
    if (gid == "step") {
        if (!can({"PosX", "PosY"})) return std::nullopt;
        const double away = -orb_ * kStage;
        std::vector<double> targets;
        for (double t : {0.0, 0.45 * away, away}) {
            if (std::fabs(t - stageX_) > 0.1) targets.push_back(t);
        }
        return moves::sidestep(out_, book_, rng_.choice(targets) - stageX_);
    }
    if (gid == "peek") return can({"Lean"}) ? std::optional(moves::peek(out_, book_, orb_)) : std::nullopt;
    if (gid == "pounce") return can({"PosY", "Squash"}) ? std::optional(moves::pounce(out_, book_, orb_)) : std::nullopt;
    if (auto it = moves::profile().find(gid); it != moves::profile().end()) {
        const bool any = std::any_of(it->second.begin(), it->second.end(), [&](const std::string& p) { return space_.isParam(p); });
        return any ? moves::profileMove(gid, out_, book_) : std::nullopt;
    }
    if (contains(moves::names(), gid)) return can({"PosY", "Squash"}) ? moves::make(gid, out_, book_) : std::nullopt;
    if (contains(gestures::head(), gid)) return gestures::headGesture(gid);
    if (contains(gestures::face(), gid) && contains(profile_.signature, gid)) return gestures::faceGesture(gid);
    return std::nullopt;
}

bool Director::startGesture(const std::string& gid) {
    if (!profile_.allows(gid)) return false;
    auto s = spec(gid);
    if (!s) return false;
    Motion motion = s->bind([this](const std::string& n) { return space_.slot(n); });
    std::vector<bool> hard;
    hard.reserve(motion.entries.size());
    for (const auto& e : motion.entries) {
        hard.push_back(std::any_of(kHard.begin(), kHard.end(), [&](const std::string& p) { return e.name.starts_with(p); }));
    }
    gesture_ = Playing{std::move(motion), std::move(hard)};
    motionStarts_ += 1;
    lastGesture_ = gid;
    recent_.push_back(gid);
    if (recent_.size() > 3) recent_.erase(recent_.begin(), recent_.end() - 3);
    return true;
}

void Director::playGroove(Band band) {
    for (auto& g : grooves_) g.target = 0;
    grooves_.push_back({band, grooves_.empty() ? 1.0 : 0.0, 1});
}

void Director::setLevel(int level) {
    const Band band = bandOf(level);
    if (!band_) {
        band_ = band;
        playGroove(band);
    } else if (band != *band_) {
        const bool rising = band > *band_;
        band_ = band;
        playGroove(band);
        if (rising && celebrate_ && (!gesture_ || gesture_->t > 0.5 * gesture_->motion.duration)) {
            for (const auto& gid : rng_.sample(kCelebrations, kCelebrations.size())) {
                if (startGesture(gid)) break;
            }
        }
        nextGesture_ = time_ + 1.5;
    }
    level_ = level;
}

void Director::blendGrooves(double dt) {
    for (auto& g : grooves_) g.w += std::max(-dt / 0.5, std::min(dt / 0.5, g.target - g.w));
    grooves_.erase(std::remove_if(grooves_.begin(), grooves_.end(),
                                  [](const GrooveLayer& g) { return !(g.w > 1e-3 || g.target > 0); }),
                   grooves_.end());
    grooveTouched_.clear();
    const auto& o = slots_.side(out_);
    const auto& k = slots_.side(book_);
    for (const auto& layer : grooves_) {
        groove::fill(layer.band, beat_.phase, time_, slots_, o, k, restIdle(), grooveOut_);
        for (const auto& [slot, raw] : grooveOut_) {
            const auto s = static_cast<std::size_t>(slot);
            const double v = raw * grooveScale_[s];
            if (!grooveSeen_[s]) {
                grooveSeen_[s] = true;
                grooveTouched_.push_back(slot);
                grooveSum_[s] = 0;
                grooveWeight_[s] = 0;
            }
            grooveSum_[s] += v * layer.w;
            grooveWeight_[s] += layer.w;
        }
    }
    for (int slot : grooveTouched_) {
        const auto s = static_cast<std::size_t>(slot);
        const double wsum = grooveWeight_[s];
        const double w = std::min(1.0, wsum);
        write(slot, values_[s] * (1 - w) + grooveSum_[s] / std::max(wsum, 1e-6) * w);
        grooveSeen_[s] = false;
    }
}

void Director::addIdle(int slot, double v) {
    const auto& p = space_.params()[static_cast<std::size_t>(slot)];
    if (!p) return;
    double share = 1.0;
    if (gesture_) {
        const auto& entries = gesture_->motion.entries;
        if (std::any_of(entries.begin(), entries.end(), [slot](const Motion::Entry& e) { return e.slot == slot; })) {
            share = 1 - gesture_->w;
        }
    }
    write(slot, std::min(std::max(values_[static_cast<std::size_t>(slot)] + share * v, p->min), p->max));
}

void Director::applyGesture(double dt) {
    if (!gesture_) {
        if (time_ >= nextGesture_) {
            std::vector<std::string> pool;
            for (const auto& g : profile_.pool(poolOf(*band_), *band_)) {
                if (!contains(recent_, g)) pool.push_back(g);
            }
            rng_.shuffle(pool);
            for (const auto& gid : pool) {
                if (startGesture(gid)) break;
            }
            nextGesture_ = time_ + 1.0;
        }
        return;
    }
    Playing& g = *gesture_;
    g.t += dt;
    const Motion& m = g.motion;
    g.w = std::min(std::min(1.0, g.t / m.fade), std::max(0.0, (m.duration - g.t) / m.fade));
    const double local = m.localTime(g.t);
    for (std::size_t i = 0; i < m.entries.size(); ++i) {
        const auto& entry = m.entries[i];
        const double v = entry.curve(local);
        const auto s = static_cast<std::size_t>(entry.slot);
        write(entry.slot, g.hard[i] ? v : values_[s] * (1 - g.w) + v * g.w);
    }
    while (g.fired < m.events.size() && m.events[g.fired].time <= g.t) {
        if (m.events[g.fired].name == "petals") events.push_back(DirectorEvent::Petals);
        g.fired += 1;
    }
    if (g.t >= m.duration) {
        stageX_ += m.travel;
        gesture_.reset();
        const auto gap = gapOf(*band_);
        nextGesture_ = time_ + rng_.uniform(gap.first * profile_.gap, gap.second * profile_.gap);
    }
}

void Director::update(double dt) {
    constexpr double pi = math::kPi;
    time_ += dt;
    intensity_ += (static_cast<double>(level_) / 13 - intensity_) * std::min(1.0, dt * 2.5);
    const double s = intensity_, t = time_;
    beat_.update(dt, band_.value_or(Band::Idle));
    const auto n = static_cast<std::size_t>(space_.count());
    for (std::size_t i = 0; i < n; ++i) {
        if (const auto& p = space_.params()[i]) {
            values_[i] = p->def;
            present_[i] = true;
        } else {
            values_[i] = 0;
            present_[i] = false;
        }
    }
    blendGrooves(dt);
    applyGesture(dt);
    auto v = [this](int slot) { return values_[static_cast<std::size_t>(slot)]; };
    write(slots_.posX, v(slots_.posX) + stageX_);
    if (armsHeld_) {
        for (int slot : armSlots_) write(slot, space_.params()[static_cast<std::size_t>(slot)]->def);
    }
    for (auto& hand : hands_) {
        const auto& side = slots_.side(hand.side);
        double arm = 0;
        arm += v(side.arm);
        arm += v(side.forearm);
        arm += v(side.lift);
        arm += v(side.bend);
        const bool idle = !hand.action;
        hand.update(dt, time_, band_.value_or(Band::Idle), 60 / beat_.bpm, arm, rng_);
        if (idle && hand.action) motionStarts_ += 1;
        for (std::size_t i = 0; i < 5; ++i) write(side.fingers[i], v(side.fingers[i]) + hand.fingers[i]);
        write(side.spread, v(side.spread) + hand.spread);
        write(side.wrist, v(side.wrist) + hand.wrist);
    }

    breathPhase_ += dt / (4.2 - 2.4 * s);
    write(slots_.breath, std::max(v(slots_.breath), 0.5 - 0.5 * std::cos(2 * pi * breathPhase_)));
    const double amp = 0.06 + 0.12 * s;
    write(slots_.angleX, v(slots_.angleX) + amp * (std::sin(t * 0.83) + 0.5 * std::sin(t * 1.91 + 1.3)));
    write(slots_.angleY, v(slots_.angleY) + amp * 0.7 * std::sin(t * 1.17 + 0.4));
    write(slots_.angleZ, v(slots_.angleZ) + amp * 0.8 * std::sin(t * 0.61 + 2.1));
    write(slots_.bodySway, v(slots_.bodySway) + (0.15 + 0.3 * s) * std::sin(t * (0.9 + 1.6 * s)));
    write(slots_.wind, std::min(1.0, std::pow(s, 1.2) * 1.05));
    if (space_.isParam(slots_.tailSwish)) {
        addIdle(slots_.tailSwish, (0.18 + 0.3 * s) * profile_.sway * std::sin(pi * beat_.phase + 0.5));
    }
    if (space_.isParam(slots_.wingFlap)) addIdle(slots_.wingFlap, 0.12 * std::sin(2 * pi * breathPhase_));
    for (int slot : frozen_) write(slot, space_.params()[static_cast<std::size_t>(slot)]->def);
    if (headHeld_) {
        for (int slot : headSlots_) write(slot, space_.params()[static_cast<std::size_t>(slot)]->def);
    }
    applySprings(dt);

    gazeAt_ -= dt;
    if (gazeAt_ <= 0) {
        const double gx = rng_.uniform(-0.5, 0.5);
        const double gy = rng_.uniform(-0.35, 0.35);
        gazeTargetX_ = gx;
        gazeTargetY_ = gy;
        gazeAt_ = rng_.uniform(0.9, 2.6) * (1.2 - 0.6 * s);
    }
    const double k = std::min(1.0, dt * 14);
    gazeX_ = gazeX_ + (gazeTargetX_ - gazeX_) * k;
    gazeY_ = gazeY_ + (gazeTargetY_ - gazeY_) * k;
    write(slots_.eyeBallX, std::max(-1.0, std::min(1.0, v(slots_.eyeBallX) + 0.35 * gazeX_)));
    write(slots_.eyeBallY, std::max(-1.0, std::min(1.0, v(slots_.eyeBallY) + 0.35 * gazeY_)));

    const double openness = blink(dt);
    for (int slot : slots_.eyeOpen) {
        const auto i = static_cast<std::size_t>(slot);
        if (present_[i]) values_[i] = std::min(values_[i], openness);
    }
}

void Director::applySprings(double dt) {
    const auto n = static_cast<std::size_t>(space_.count());
    for (std::size_t i = 0; i < n; ++i) {
        if (!present_[i] || !space_.params()[i] || !springs_[i]) continue;
        if (!springLive_[i]) {
            springs_[i]->snap(values_[i]);
            springLive_[i] = true;
        }
        values_[i] = springs_[i]->update(dt, values_[i]);
    }
}

double Director::blink(double dt) {
    blinkAt_ -= dt;
    if (!blinkT_ && blinkAt_ <= 0) blinkT_ = 0;
    if (!blinkT_) return 1;
    const double bt = *blinkT_ + dt;
    blinkT_ = bt;
    if (bt < 0.07) return 1 - bt / 0.07;
    if (bt < 0.11) return 0;
    if (bt < 0.2) return (bt - 0.11) / 0.09;
    blinkT_.reset();
    blinkAt_ = rng_.random() > 0.15 ? rng_.uniform(2.0, 5.0) : 0.25;
    return 1;
}

std::optional<double> Director::value(const std::string& n) const {
    auto s = space_.slot(n);
    if (!s) return std::nullopt;
    return values_[static_cast<std::size_t>(*s)];
}

}  // namespace agentspeed::character
