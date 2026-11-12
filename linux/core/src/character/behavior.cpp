#include "agentspeed/character/behavior.hpp"

#include <algorithm>
#include <cmath>
#include <limits>

namespace agentspeed::character {

namespace {

constexpr double kHold = 1.2;
constexpr double kThinkFace = 3.0, kThinkPose = 10.0;
constexpr double kFocusFace = 3.0, kFocusPose = 12.0;
constexpr double kWaitPose = 20.0, kWaitSmile = 6.0, kWaitYawn = 180.0;
constexpr double kRestIdle = 600.0, kRestNightIdle = 120.0;
constexpr int kNightEnd = 6;
constexpr double kWake = 60.0;
constexpr double kPokeSurprise = 0.4;
constexpr double kEntryDelay = 0.55;
constexpr double kLookIn = 0.4, kLookOut = 0.8;
constexpr double kHeadX = 0.7, kHeadY = 0.5, kHeadReach = 0.8, kEyeReach = 0.3;
constexpr double kGazeOn = 0.35, kGazeOff = 0.25, kGazeDwell = 0.5, kGazeAxis = 0.75;
constexpr double kSmileAfter = 0.8, kGestureAfter = 4.0, kGoodbyeAfter = 1.0, kAttention = 8.0, kWinkFor = 1.2;
constexpr LookTarget kUpTarget{0.15, 0.3, 0.3, -0.8};
constexpr std::array<double, 4> kEventLasts = {1.6, 3.2, 2.4, 2.5};
constexpr double kNever = -std::numeric_limits<double>::infinity();

const std::vector<std::string> kNone;
const std::vector<std::string> kDoubletake = {"doubletake"};
const std::vector<std::string> kChinShake = {"chin", "shake"};
const std::vector<std::string> kHop = {"hop"};
const std::vector<std::string> kJumpjoyHop = {"jumpjoy", "hop"};
const std::vector<std::string> kWaveBow = {"wave", "bow"};
const std::vector<std::string> kNod = {"nod"};
const std::vector<std::string> kYawn = {"yawn"};
const std::vector<std::string> kWaveNod = {"wave", "nod"};
const std::vector<std::string> kThinkPool = {"chin", "tilt", "ponder"};
const std::vector<std::string> kFocusPool = {"chin", "nod"};
const std::vector<std::string> kRestPool = {"yawn"};

double clamp1(double v) { return std::max(-1.0, std::min(1.0, v)); }

std::optional<std::string_view> moodFace(Mood mood) {
    switch (mood) {
        case Mood::Cheer: return "excited";
        case Mood::Greet: return "smile";
        case Mood::Trouble: return "worried";
        case Mood::Think: return "thinking";
        case Mood::Focus: return "focused";
        case Mood::Rest: return "sleepy";
        default: return std::nullopt;
    }
}

std::optional<std::string_view> bandFace(Band band) {
    switch (band) {
        case Band::Cruise:
        case Band::Boost: return "smile";
        case Band::Overdrive: return "focused";
        case Band::Redline: return "excited";
        default: return std::nullopt;
    }
}

// The expressions actions name, as face patches; `talk` leaves the face as it is (true with no face).
bool actionFace(std::string_view expression, std::optional<std::string_view>& face) {
    face.reset();
    if (expression == "smile") face = "smile";
    else if (expression == "happy") face = "excited";
    else if (expression == "surprised") face = "surprised";
    else if (expression == "determined") face = "focused";
    else if (expression == "sleepy") face = "sleepy";
    return face.has_value() || expression == "talk";
}

std::optional<std::string_view> ownFace(std::string_view action) {
    if (action == "yawn") return "yawn";
    if (action == "giggle") return "laugh";
    return std::nullopt;
}

}  // namespace

std::string_view moodName(Mood mood) {
    switch (mood) {
        case Mood::Poke: return "poke";
        case Mood::Cheer: return "cheer";
        case Mood::Greet: return "greet";
        case Mood::Trouble: return "trouble";
        case Mood::Think: return "think";
        case Mood::Work: return "work";
        case Mood::Focus: return "focus";
        case Mood::Rest: return "rest";
        case Mood::Wait: return "wait";
    }
    return "wait";
}

std::optional<Mood> moodNamed(std::string_view name) {
    for (Mood m : {Mood::Poke, Mood::Cheer, Mood::Greet, Mood::Trouble, Mood::Think, Mood::Work, Mood::Focus, Mood::Rest,
                   Mood::Wait}) {
        if (moodName(m) == name) return m;
    }
    return std::nullopt;
}

// ---- Talk

const std::array<Talk::Shape, 3>& Talk::shapes() {
    static const std::array<Shape, 3> s = {{{"a", 0.6}, {"o", 0.25}, {"u", 0.15}}};
    return s;
}

std::string_view Talk::pick(int shapes, Rng& rng) {
    double total = 0;
    std::string_view first;
    for (std::size_t i = 0; i < Talk::shapes().size(); ++i) {
        if ((shapes & (1 << i)) == 0) continue;
        total += Talk::shapes()[i].weight;
        if (first.empty()) first = Talk::shapes()[i].name;
    }
    double x = rng.random() * total;
    std::string_view last = first;
    for (std::size_t i = 0; i < Talk::shapes().size(); ++i) {
        if ((shapes & (1 << i)) == 0) continue;
        x -= Talk::shapes()[i].weight;
        if (x < 0) return Talk::shapes()[i].name;
        last = Talk::shapes()[i].name;
    }
    return last;
}

void Talk::phrase(int level, Rng& rng) {
    syllables_ = 3 + rng.index(6);
    rate_ = std::min(7.0, rng.uniform(5.0, 6.0) + 0.1 * level);
}

void Talk::syllable(int shapes, Rng& rng) {
    shape_ = pick(shapes, rng);
    const double span = 1.0 / rate_;
    const double opened = span * rng.uniform(0.55, 0.70);
    stage_ = Stage::Open;
    gap_ = span - opened;
    left_ += opened;
}

std::optional<std::string_view> Talk::update(double dt, bool talking, int level, int shapes, bool clear, Rng& rng) {
    if (!talking || shapes == 0) {
        on_ = false;
        shape_.reset();
        stage_.reset();
        left_ = 0;
        return std::nullopt;
    }
    if (!on_) {
        on_ = true;
        stage_ = Stage::Ready;
        left_ = 0;
        phrases_ = 2 + rng.index(3);
    }
    if (stage_ == Stage::Ready) {
        if (!clear) return std::nullopt;
        left_ = 0;
        phrase(level, rng);
        syllable(shapes, rng);
    }
    left_ -= dt;
    while (left_ <= 0) {
        switch (*stage_) {
            case Stage::Open:
                stage_ = Stage::Gap;
                shape_.reset();
                left_ += gap_;
                --syllables_;
                break;
            case Stage::Gap:
                if (syllables_ > 0) {
                    syllable(shapes, rng);
                    break;
                }
                --phrases_;
                if (phrases_ > 0) {
                    stage_ = Stage::Pause;
                    left_ += rng.uniform(0.4, 1.2);
                } else {
                    stage_ = Stage::Silence;
                    left_ += rng.uniform(2.0, 5.0);
                }
                break;
            case Stage::Pause:
                phrase(level, rng);
                syllable(shapes, rng);
                break;
            default:
                stage_ = Stage::Ready;
                left_ = 0;
                phrases_ = 2 + rng.index(3);
                return shape_;
        }
    }
    return shape_;
}

// ---- Behavior

const std::array<std::string, 6>& Behavior::stateNames() {
    static const std::array<std::string, 6> names = {"think", "wait", "cheer", "sleepy", "focus", "greet"};
    return names;
}

const std::array<std::string, 3>& Behavior::hoverGestures() {
    static const std::array<std::string, 3> names = {"wave", "peace", "thumbsup"};
    return names;
}

Behavior::Behavior(std::uint64_t seed, const std::vector<std::string>& states)
    : rng_(seed + 1), hoverQueue_(1), stateT_(kHold), woken_(kNever), attentionUntil_(kNever), faceUntil_(kNever),
      queue_(&kNone) {
    until_.fill(kNever);
    for (const auto& name : stateNames()) {
        if (std::find(states.begin(), states.end(), name) != states.end()) states_.push_back(name);
    }
}

void Behavior::addHoverOption(const std::string& gid) {
    if (hoverCount_ < static_cast<int>(hoverOptions_.size())) hoverOptions_[static_cast<std::size_t>(hoverCount_++)] = gid;
}

bool Behavior::hasState(std::string_view name) const {
    return std::find(states_.begin(), states_.end(), name) != states_.end();
}

LookMode Behavior::lookMode() const {
    if (mood_ == Mood::Think) return LookMode::Up;
    if (mood_ == Mood::Focus || mood_ == Mood::Rest) return LookMode::None;
    return LookMode::User;
}

// ---- moods

bool Behavior::resting(const BehaviorInputs& i) const {
    const bool night = 0 <= i.hour && i.hour < kNightEnd;
    const bool longIdle = i.idle >= kRestIdle || (night && i.idle >= kRestNightIdle);
    return i.phase == AgentActivity::Idle && longIdle && t_ - woken_ >= kWake;
}

Mood Behavior::stateWanted(const BehaviorInputs& i) const {
    switch (i.phase) {
        case AgentActivity::Generating: return i.wait && *i.wait >= kThinkFace ? Mood::Think : Mood::Work;
        case AgentActivity::Tools: return phaseT_ >= kFocusFace ? Mood::Focus : Mood::Work;
        default: return resting(i) ? Mood::Rest : Mood::Wait;
    }
}

void Behavior::start(Mood mood, int& fresh) {
    const auto e = static_cast<std::size_t>(mood);
    until_[e] = t_ + kEventLasts[e];
    started_[e] = t_;
    fresh |= 1 << e;
}

std::pair<const std::vector<std::string>*, double> Behavior::entry(Mood mood, Mood cameFrom) const {
    switch (mood) {
        case Mood::Poke: return {&kDoubletake, t_};
        case Mood::Trouble: return {&kChinShake, t_};
        case Mood::Cheer: return {hasState("cheer") ? &kHop : &kJumpjoyHop, t_ + kEntryDelay};
        case Mood::Greet: return {hasState("greet") ? &kNone : &kWaveBow, t_ + kEntryDelay};
        case Mood::Wait:
            if (cameFrom == Mood::Work || cameFrom == Mood::Think || cameFrom == Mood::Focus) return {&kNod, t_};
            return {&kNone, t_};
        case Mood::Rest: return {&kYawn, t_ + kEntryDelay};
        default: return {&kNone, t_};
    }
}

void Behavior::moods(double dt, const BehaviorInputs& i) {
    int fresh = 0;
    if (i.phase != phase_) {
        if (phase_ == AgentActivity::Idle && stateMood_ == Mood::Rest) start(Mood::Greet, fresh);
        phase_ = i.phase;
        phaseT_ = 0;
    } else {
        phaseT_ += dt;
    }
    if (i.newPeak) start(Mood::Cheer, fresh);
    if (i.trouble) start(Mood::Trouble, fresh);
    if (i.click) {
        if (stateMood_ == Mood::Rest) woken_ = t_;
        start(Mood::Poke, fresh);
    }
    const Mood previousState = stateMood_;
    stateT_ += dt;
    const Mood wanted = stateWanted(i);
    if (wanted != stateMood_ && stateT_ >= kHold) {
        stateMood_ = wanted;
        stateT_ = 0;
    }
    const Mood before = mood_;
    mood_ = stateMood_;
    for (std::size_t e = 0; e < until_.size(); ++e) {
        if (until_[e] > t_) {
            mood_ = static_cast<Mood>(e);
            break;
        }
    }
    if (mood_ != before) {
        moodT_ = 0;
        queue_ = &kNone;
        hoverQueued_ = false;
    } else {
        moodT_ += dt;
    }
    if ((fresh & (1 << static_cast<int>(mood_))) != 0 || (mood_ == stateMood_ && stateMood_ != previousState)) {
        const auto [queue, at] = entry(mood_, previousState);
        queue_ = queue;
        hoverQueued_ = false;
        queueAt_ = at;
    }
}

// ---- the cursor

void Behavior::hover(double dt, const BehaviorInputs& i, Band band) {
    const bool social = isSocial(mood_);
    if (i.over) {
        overT_ += dt;
        if (overT_ >= kSmileAfter) attentionUntil_ = t_ + kAttention;
        if (overT_ >= kGestureAfter && !hoverDone_ && social) {
            hoverDone_ = true;
            if (hoverCount_ > 0) {
                hoverQueue_[0] = hoverOptions_[static_cast<std::size_t>(rng_.index(hoverCount_))];
                hoverQueued_ = true;
                queueAt_ = t_;
            } else if (hasWink) {
                faceOverride_ = "wink";
                faceUntil_ = t_ + kWinkFor;
            }
        }
        return;
    }
    if (overT_ >= kGoodbyeAfter && social && (band == Band::Idle || band == Band::Cruise)) {
        queue_ = &kWaveNod;
        hoverQueued_ = false;
        queueAt_ = t_;
    }
    overT_ = 0;
    hoverDone_ = false;
}

std::string_view Behavior::direction(double dx, double dy) const {
    bool horizontal = std::fabs(dx) >= std::fabs(dy);
    if (gaze_ == "left" || gaze_ == "right") {
        horizontal = std::fabs(dx) >= kGazeAxis * std::fabs(dy);
    } else if (gaze_ == "up" || gaze_ == "down") {
        horizontal = kGazeAxis * std::fabs(dx) > std::fabs(dy);
    }
    if (horizontal) return dx > 0 ? "right" : "left";
    return dy > 0 ? "down" : "up";
}

void Behavior::look(double dt, const BehaviorInputs& i) {
    const LookMode mode = lookMode();
    const bool near = i.cursor && std::hypot(i.cursor->first, i.cursor->second) <= kLookRange;
    bool on = true;
    std::optional<std::string_view> wanted;
    if (mode == LookMode::Up) {
        lookTarget_ = kUpTarget;
        wanted = "up";
    } else if (mode == LookMode::User && near) {
        const auto [dx, dy] = *i.cursor;
        lookTarget_ = {kHeadX * clamp1(dx / kHeadReach), -kHeadY * clamp1(dy / kHeadReach), clamp1(dx / kEyeReach),
                       clamp1(dy / kEyeReach)};
        const double far = std::max(std::fabs(dx), std::fabs(dy));
        if (far >= (gaze_ ? kGazeOff : kGazeOn)) wanted = direction(dx, dy);
    } else if (mode == LookMode::User && isEvent(mood_)) {
        lookTarget_ = {};
    } else {
        on = false;
    }
    lookWeight_ = on ? std::min(1.0, lookWeight_ + dt / kLookIn) : std::max(0.0, lookWeight_ - dt / kLookOut);
    if (wanted != gaze_ && t_ - gazeSince_ >= kGazeDwell) {
        gaze_ = wanted;
        gazeSince_ = t_;
    }
}

void Behavior::update(double dt, const BehaviorInputs& inputs, Band band) {
    t_ += dt;
    inputs_ = inputs;
    moods(dt, inputs);
    hover(dt, inputs, band);
    look(dt, inputs);
}

// ---- what the director asks for

std::optional<MoodPose> Behavior::pose() const {
    std::string_view name;
    switch (mood_) {
        case Mood::Poke:
        case Mood::Trouble: return MoodPose{};
        case Mood::Cheer: name = stateNames()[2]; break;
        case Mood::Greet: name = stateNames()[5]; break;
        case Mood::Rest: name = stateNames()[3]; break;
        case Mood::Think:
            if (inputs_.wait && *inputs_.wait >= kThinkPose) name = stateNames()[0];
            break;
        case Mood::Focus:
            if (phaseT_ >= kFocusPose) name = stateNames()[4];
            break;
        case Mood::Wait:
            if (inputs_.idle >= kWaitPose && t_ >= attentionUntil_) name = stateNames()[1];
            break;
        default: break;
    }
    if (!name.empty() && hasState(name)) return MoodPose{name};
    return std::nullopt;
}

std::pair<const std::vector<std::string>*, std::optional<Band>> Behavior::pool(Band band, const std::vector<std::string>& bandPool,
                                                                               const std::vector<std::string>& idlePool,
                                                                               const std::vector<std::string>& idleEarlyPool) const {
    if (isEvent(mood_)) return {&kNone, std::nullopt};
    switch (mood_) {
        case Mood::Think: return {&kThinkPool, std::nullopt};
        case Mood::Focus: return {&kFocusPool, std::nullopt};
        case Mood::Rest: return {&kRestPool, std::nullopt};
        case Mood::Wait: return {inputs_.idle >= kWaitYawn ? &idlePool : &idleEarlyPool, Band::Idle};
        default: return {&bandPool, band};
    }
}

std::pair<double, double> Behavior::gaps(Band band, std::pair<double, double> (*bandGaps)(Band)) const {
    if (isEvent(mood_)) return bandGaps(band);
    switch (mood_) {
        case Mood::Think: return {2.0, 4.0};
        case Mood::Focus: return {3.0, 6.0};
        case Mood::Rest: return {20.0, 40.0};
        case Mood::Wait: return bandGaps(Band::Idle);
        default: return bandGaps(band);
    }
}

const std::vector<std::string>& Behavior::takeQueue() {
    const auto& queued = hoverQueued_ ? hoverQueue_ : *queue_;
    if (queued.empty() || t_ < queueAt_) return kNone;
    queue_ = &kNone;
    hoverQueued_ = false;
    return queued;
}

std::optional<std::string_view> Behavior::face(const Motion* action, std::optional<std::string_view> eventFace, Band band) const {
    if (mood_ == Mood::Poke) {
        return t_ - started_[static_cast<std::size_t>(Mood::Poke)] < kPokeSurprise ? "surprised" : "laugh";
    }
    if (action && action->name == "yawn") return "yawn";
    if (!isSocial(mood_)) return moodFace(mood_);
    if (t_ < faceUntil_) return faceOverride_;
    if (action && action->expression && !action->expression->empty()) {
        if (auto own = ownFace(action->name)) return own;
        std::optional<std::string_view> mapped;
        return actionFace(*action->expression, mapped) ? mapped : std::optional<std::string_view>(*action->expression);
    }
    if (mood_ == Mood::Work && eventFace && !eventFace->empty()) {
        std::optional<std::string_view> mapped;
        return actionFace(*eventFace, mapped) ? mapped : eventFace;
    }
    std::optional<std::string_view> f = mood_ == Mood::Work ? bandFace(band)
                                        : inputs_.idle < kWaitSmile ? std::optional<std::string_view>("smile")
                                                                    : std::nullopt;
    if (!f && overT_ >= kSmileAfter) f = "smile";
    return f;
}

}  // namespace agentspeed::character
