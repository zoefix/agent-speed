// The living pack (spec/CHARACTERS.md, Behavior; motions/director.py): moods from the inputs, face patches swapped while
// she blinks, lip flaps through the mouth patches, and the head and eyes turned toward where she looks.
#include <algorithm>

#include "agentspeed/character/arm_gestures.hpp"
#include "agentspeed/character/director.hpp"

namespace agentspeed::character {

namespace {

// Face patch rates (seconds for a full swap); a blink over a patch shows `gaze-closed` while the eyes are half shut.
constexpr double kPatchFade = 0.05;
constexpr double kBlinkFade = 0.03;
// A dozing blink holds the eyes shut this long.
constexpr double kDozeLow = 0.5, kDozeHigh = 1.2;

bool same(const std::optional<std::string>& a, std::optional<std::string_view> b) {
    return a.has_value() == b.has_value() && (!a || *a == *b);
}

void assign(std::optional<std::string>& to, std::optional<std::string_view> from) {
    if (same(to, from)) return;
    if (from) {
        to.emplace(*from);
    } else {
        to.reset();
    }
}

}  // namespace

bool PatchGroup::contains(std::optional<std::string_view> name) const {
    if (!name) return false;
    return std::find(names.begin(), names.end(), *name) != names.end();
}

void PoseFaces::reset() {
    std::fill(expressions.weights.begin(), expressions.weights.end(), 0.0);
    std::fill(mouths.weights.begin(), mouths.weights.end(), 0.0);
    std::fill(gazes.weights.begin(), gazes.weights.end(), 0.0);
}

PoseFaces& Director::faces() {
    static PoseFaces empty;
    return faces_ >= 0 ? *faceCache_[static_cast<std::size_t>(faces_)].second : empty;
}

std::optional<std::string_view> Director::mouthShape() const {
    if (!active_ || !behavior_) return std::nullopt;
    return behavior_->talk().shape();
}

bool Director::livingBusy(bool following) const {
    if (!active_ || !behavior_) return false;
    if (facePending_) return true;
    const auto& talk = behavior_->talk();
    if (talk.on() && talk.stage() &&
        (*talk.stage() == Talk::Stage::Open || *talk.stage() == Talk::Stage::Gap || *talk.stage() == Talk::Stage::Pause)) {
        return true;
    }
    const double w = behavior_->lookWeight();
    if (w > 0 && w < 1) return true;
    return following && behavior_->lookMode() == LookMode::User && w > 0;
}

std::optional<MoodPose> Director::poseWanted() const {
    if (!active_ || !behavior_) return std::nullopt;
    return behavior_->pose();
}

const RigDocument::Variant* Director::arm(const std::string& gid) {
    const auto& meta = faces().meta;
    const auto it = meta.find(gid);
    return it != meta.end() && it->second.kind == "arm" ? &it->second : nullptr;
}

void Director::setVariants(const std::vector<RigDocument::Variant>& variants) {
    if (variants.empty()) {
        faces_ = -1;
        return;
    }
    for (std::size_t i = 0; i < faceCache_.size(); ++i) {
        if (faceCache_[i].first == &variants) {
            faceCache_[i].second->reset();
            faces_ = static_cast<int>(i);
            return;
        }
    }
    faceCache_.emplace_back(&variants, compile(variants));
    faces_ = static_cast<int>(faceCache_.size() - 1);
}

std::unique_ptr<PoseFaces> Director::compile(const std::vector<RigDocument::Variant>& variants) const {
    auto out = std::make_unique<PoseFaces>();
    for (const auto& v : variants) {
        out->meta.insert_or_assign(v.id, v);
        if (v.kind == "face") out->faceIds.insert(v.id);
        const auto dash = v.id.find('-');
        if (v.kind != "patch" || dash == std::string::npos) continue;
        const auto prefix = v.id.substr(0, dash);
        PatchGroup* group = prefix == "expr" ? &out->expressions : prefix == "mouth" ? &out->mouths : prefix == "gaze" ? &out->gazes : nullptr;
        if (!group) continue;
        const auto name = v.id.substr(dash + 1);
        const int slot = space_.slotOrMinus(v.param);
        const auto it = std::find(group->names.begin(), group->names.end(), name);
        if (it != group->names.end()) {
            group->params[static_cast<std::size_t>(it - group->names.begin())] = slot;
        } else {
            group->names.push_back(name);
            group->params.push_back(slot);
        }
    }
    for (PatchGroup* g : {&out->expressions, &out->mouths, &out->gazes}) g->weights.assign(g->names.size(), 0.0);
    for (std::size_t i = 0; i < Talk::shapes().size(); ++i) {
        if (out->mouths.contains(Talk::shapes()[i].name)) out->talkShapes |= 1 << i;
    }
    return out;
}

void Director::refreshBehaviorOptions() {
    if (!behavior_) return;
    behavior_->clearHoverOptions();
    for (const auto& gid : Behavior::hoverGestures()) {
        if (arm_gestures::arms().count(gid) && profile_.allows(gid) && arm(gid)) behavior_->addHoverOption(gid);
    }
    behavior_->hasWink = faces().expressions.contains("wink");
}

void Director::feel(double dt, const BehaviorInputs* inputs) {
    active_ = behavior_ && inputs;
    if (!active_) return;
    behavior_->update(dt, *inputs, band_.value_or(Band::Idle));
    if (behavior_->mood() != mood_) {
        mood_ = behavior_->mood();
        const auto gap = currentGaps();
        nextGesture_ = time_ + rng_.uniform(gap.first * profile_.gap, gap.second * profile_.gap);
    }
}

void Director::look(int slot, double target) {
    const double w = behavior_->lookWeight();
    if (!(w > 0)) return;
    const double k = w * idleShare(slot);
    write(slot, values_[static_cast<std::size_t>(slot)] * (1 - k) + target * k);
}

double Director::slewed(double w, double target, double dt, double seconds) const {
    return snapFaces_ ? target : w + std::max(-dt / seconds, std::min(dt / seconds, target - w));
}

double Director::livingFace(double dt, const Motion* gesture) {
    Behavior& b = *behavior_;
    PoseFaces& f = faces();
    PatchGroup& expressions = f.expressions;
    PatchGroup& gazes = f.gazes;
    PatchGroup& mouths = f.mouths;
    const bool yawning = gesture && gesture->name == "yawn";
    bool talk = (b.talking() && level_ >= 1) || (gesture && gesture->talk);
    talk = talk && profile_.talk && f.talkShapes != 0 && !yawning;

    // While she speaks (a group of phrases until its silence) the face is drawn: no expression, no gaze.
    const bool speaking = talk && b.talk().speaking();
    const std::optional<std::string_view> eventFace = time_ < eventUntil_ ? eventFace_ : std::nullopt;
    std::optional<std::string_view> wanted = speaking ? std::nullopt : b.face(gesture, eventFace, band_.value_or(Band::Idle));
    if (!expressions.contains(wanted)) wanted.reset();
    const std::optional<std::string_view> gaze =
        !speaking && !wanted && gazes.contains(b.gaze()) ? b.gaze() : std::nullopt;
    const bool change = !same(expression_, wanted) || !same(gazePatch_, gaze);
    const double openness = dozeBlink(dt, change, b.mood() == Mood::Rest);
    if (snapFaces_ || (change && (openness <= 0.02 || blinkSkipped_))) {
        assign(expression_, wanted);
        assign(gazePatch_, gaze);
    }

    const bool covered = expression_ || gazePatch_;
    const bool blinking = covered && !(expression_ && (*expression_ == "laugh" || *expression_ == "yawn")) && openness < 0.5;
    for (std::size_t i = 0; i < expressions.names.size(); ++i) {
        const double target = expression_ && expressions.names[i] == *expression_ ? 1 : 0;
        const double w = slewed(expressions.weights[i], target, dt, kPatchFade);
        expressions.weights[i] = w;
        writeSlot(expressions.params[i], w);
    }
    for (std::size_t i = 0; i < gazes.names.size(); ++i) {
        const bool closed = gazes.names[i] == "closed";
        const double target = closed ? (blinking ? 1 : 0) : (gazePatch_ && gazes.names[i] == *gazePatch_ ? 1 : 0);
        const double w = slewed(gazes.weights[i], target, dt, closed ? kBlinkFade : kPatchFade);
        gazes.weights[i] = w;
        writeSlot(gazes.params[i], w);
    }

    bool clear = !expression_;
    for (double w : expressions.weights) clear = clear && w <= 0;
    auto shape = b.speak(dt, talk, level_, f.talkShapes, clear);
    if (!shape && talk && b.talk().stage() == Talk::Stage::Gap) shape = "closed";
    for (std::size_t i = 0; i < mouths.names.size(); ++i) {
        const double w = shape && mouths.names[i] == *shape ? 1.0 : 0.0;
        mouths.weights[i] = w;
        writeSlot(mouths.params[i], w);
    }

    snapFaces_ = false;
    bool pending = !same(expression_, wanted) || !same(gazePatch_, gaze);
    for (double w : expressions.weights) pending = pending || (w > 0 && w < 1);
    for (std::size_t i = 0; i < gazes.names.size(); ++i) {
        pending = pending || (gazes.names[i] != "closed" && gazes.weights[i] > 0 && gazes.weights[i] < 1);
    }
    facePending_ = pending;
    return openness;
}

double Director::dozeBlink(double dt, bool force, bool doze) {
    blinkSkipped_ = false;
    blinkAt_ -= dt;
    if (!blinkT_ && (blinkAt_ <= 0 || force)) {
        blinkT_ = 0;
        blinkHold_ = doze ? behavior_->dozeHold(kDozeLow, kDozeHigh) : 0.04;
    }
    if (!blinkT_) return 1;
    const double bt = *blinkT_ + dt;
    blinkT_ = bt;
    const double hold = blinkHold_;
    blinkSkipped_ = bt - dt < 0.07 && bt >= 0.07 + hold;
    if (bt < 0.07) return 1 - bt / 0.07;
    if (bt < 0.07 + hold) return 0;
    if (bt < 0.16 + hold) return (bt - 0.07 - hold) / 0.09;
    blinkT_.reset();
    blinkAt_ = rng_.random() > 0.15 ? rng_.uniform(2.0, 5.0) : 0.25;
    return 1;
}

}  // namespace agentspeed::character
