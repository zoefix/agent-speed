#pragma once

#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/character/behavior.hpp"
#include "agentspeed/character/hand_actor.hpp"
#include "agentspeed/character/motion.hpp"
#include "agentspeed/character/motion_profile.hpp"
#include "agentspeed/character/param_space.hpp"
#include "agentspeed/character/rhythm.hpp"
#include "agentspeed/character/rng.hpp"
#include "agentspeed/character/springs.hpp"

namespace agentspeed::character {

// Something the director wants the scene to show, e.g. a puff of petals at a jump's take-off.
enum class DirectorEvent { Petals };

// The face patches of one kind (`expr-*`, `mouth-*` or `gaze-*`) a pose has: names, slots (-1 for none) and the
// weights shown.
struct PatchGroup {
    std::vector<std::string> names;
    std::vector<int> params;  // not `slots`: a Qt keyword in the app
    std::vector<double> weights;

    bool empty() const { return names.empty(); }
    bool contains(std::optional<std::string_view> name) const;
};

// A pose's variants as the director uses them: arm gestures by id, the ids of face edits, its face patches and the
// open mouth shapes it can talk with (a mask over Talk::shapes).
struct PoseFaces {
    std::map<std::string, RigDocument::Variant> meta;
    std::set<std::string> faceIds;
    PatchGroup expressions, mouths, gazes;
    int talkShapes = 0;

    // Hides every patch again, as a fresh pose shows them.
    void reset();
};

// Turns the speed level into parameter values (motions/director.py): a groove per band on a beat clock, one action
// at a time from the band's pool, hand acting, breathing, glances, blinks and wind, then springs. Side steps move the
// character around its spot, away from the orb, and she stays where she lands.
//
// A living pack (face patches or mood poses) also takes the app's inputs with every update: the behavior picks the
// mood, and with it the actions, the face patches, the talking mouth and where she looks (director_living.cpp).
// Without inputs, or for any other pack, the director moves exactly as before. `variants` must outlive the director
// (the pack's rigs do).
class Director {
public:
    Director(const std::vector<Param>& params, std::uint64_t seed = 7, Side out = Side::Right, Side book = Side::Left,
             double orb = -1, bool celebrate = true, MotionProfile profile = MotionProfile(),
             const std::vector<RigDocument::Variant>* variants = nullptr, const std::vector<std::string>& states = {});

    // Which arm swings free and which holds the object, for a new pose (of `poseBand`, for its hand set).
    void setSides(Side out, Side book, std::optional<Band> poseBand = std::nullopt);
    // As above for a pose by name (a band or a mood pose). With `variants` the new pose's variants replace the old
    // pose's: an action playing an old variant stops (the swap is edge-on) and the face patches take the new pose's
    // at once.
    void setSides(Side out, Side book, const std::string& pose, const std::vector<RigDocument::Variant>* variants);
    void setLevel(int level);
    // Advances one frame; the parameters land in `values` (read the rig's own through `space`). `inputs` drive the
    // moods of a living pack; without them nothing changes.
    void update(double dt, const BehaviorInputs* inputs = nullptr);

    // The value of a parameter after the last update, or nullopt when the director does not know it.
    std::optional<double> value(const std::string& name) const;
    // Whether a gesture is playing, and its name.
    std::optional<std::string> activeGesture() const { return gesture_ ? lastGesture_ : std::nullopt; }

    const ParamSpace& space() const { return space_; }
    const MotionProfile& profile() const { return profile_; }
    const std::vector<double>& values() const { return values_; }
    std::optional<Band> band() const { return band_; }
    // The pose on screen (a band, or a mood pose of a living pack), and its band (nullopt for a mood pose).
    const std::string& poseName() const { return poseName_; }
    std::optional<Band> poseBand() const { return bandNamed(poseName_); }
    int level() const { return level_; }
    double intensity() const { return intensity_; }
    double time() const { return time_; }
    double stageX() const { return stageX_; }
    Side out() const { return out_; }
    Side book() const { return book_; }
    int motionStarts() const { return motionStarts_; }

    // Skips the actions that leave the ground (DESIGN 2a.6, Reduce Motion).
    bool grounded = false;
    std::vector<DirectorEvent> events;

    // ---- the living pack (spec/CHARACTERS.md, Behavior)

    // The moods of a living pack; null for any other pack.
    const Behavior* behavior() const { return behavior_ ? &*behavior_ : nullptr; }
    bool living() const { return behavior_.has_value(); }
    // The mood of the last update that had inputs.
    std::optional<Mood> mood() const { return mood_; }
    // The expression patch shown by name (`smile` for `expr-smile`), and the gaze patch (`left` for `gaze-left`).
    const std::optional<std::string>& expression() const { return expression_; }
    const std::optional<std::string>& gazePatch() const { return gazePatch_; }
    // The open mouth shape this frame (`a`, `o`, `u`), nullopt while closed or silent.
    std::optional<std::string_view> mouthShape() const;
    // Whether she speaks (a group of phrases until its silence) this frame.
    bool talking() const { return active_ && behavior_ && behavior_->talk().on(); }
    // Whether the living face or look moves this frame (DESIGN 2a.6, Frame policy): a face or gaze swap, a phrase being
    // spoken, the look turning on or off, or (`following`) the head following a cursor that just moved.
    bool livingBusy(bool following) const;
    // The mood pose to show in a living pack fed inputs, else nullopt: the band pose.
    std::optional<MoodPose> poseWanted() const;

private:
    struct GrooveLayer {
        Band band;
        double w;
        double target;
    };
    struct Playing {
        Motion motion;
        std::vector<bool> hard;
        double t = 0;
        double w = 0;
        std::size_t fired = 0;
    };

    bool can(std::initializer_list<const char*> names) const;
    std::optional<MotionSpec> spec(const std::string& gid);
    bool startGesture(const std::string& gid);
    void playGroove(Band band);
    void blendGrooves(double dt);
    // The share of a parameter the playing action leaves to the layers below it.
    double idleShare(int slot) const;
    // The gap range between actions: the band's, or the mood's in a living pack.
    std::pair<double, double> currentGaps() const;
    // The actions to pick from into `pool_`, without the last three played.
    void fillPool();
    // Plays the action under way, or starts one; returns the motion that played this frame.
    const Motion* applyGesture(double dt);

    // director_living.cpp
    PoseFaces& faces();
    const RigDocument::Variant* arm(const std::string& gid);
    void setVariants(const std::vector<RigDocument::Variant>& variants);
    std::unique_ptr<PoseFaces> compile(const std::vector<RigDocument::Variant>& variants) const;
    void refreshBehaviorOptions();
    void feel(double dt, const BehaviorInputs* inputs);
    void look(int slot, double target);
    double slewed(double w, double target, double dt, double seconds) const;
    void writeSlot(int slot, double v) {
        if (slot >= 0) write(slot, v);
    }
    double livingFace(double dt, const Motion* gesture);
    double dozeBlink(double dt, bool force, bool doze);
    void write(int slot, double v) {
        values_[static_cast<std::size_t>(slot)] = v;
        present_[static_cast<std::size_t>(slot)] = true;
    }
    void addIdle(int slot, double v);
    void applySprings(double dt);
    double blink(double dt);
    bool restIdle() const { return profile_.idle == "rest"; }

    ParamSpace space_;
    Slots slots_;
    MotionProfile profile_;
    std::string poseName_ = "idle";
    std::vector<double> grooveScale_;
    std::vector<int> frozen_;
    std::vector<int> armSlots_;
    bool armsHeld_ = false;
    std::vector<int> headSlots_;
    bool headHeld_ = false;
    Side out_, book_;
    double orb_;
    bool celebrate_;
    Rng rng_;
    Beat beat_;
    std::vector<GrooveLayer> grooves_;
    std::optional<Playing> gesture_;
    double nextGesture_ = 2.0;
    std::optional<std::string> lastGesture_;
    std::vector<std::string> recent_;
    double stageX_ = 0;
    std::optional<Band> band_;
    int level_ = 0;
    double intensity_ = 0;
    double blinkAt_ = 1.2;
    std::optional<double> blinkT_;
    double gazeX_ = 0, gazeY_ = 0, gazeTargetX_ = 0, gazeTargetY_ = 0;
    double gazeAt_ = 0.8;
    double breathPhase_ = 0;
    double time_ = 0;
    std::vector<HandActor> hands_;
    int motionStarts_ = 0;
    std::vector<double> values_;
    std::vector<bool> present_;
    groove::Out grooveOut_;
    std::vector<double> grooveSum_, grooveWeight_;
    std::vector<int> grooveTouched_;
    std::vector<bool> grooveSeen_;
    std::vector<std::optional<SecondOrder>> springs_;
    std::vector<bool> springLive_;
    std::vector<const std::string*> pool_;

    // The living pack: each pose's variants compiled once (by the address of its list), the face shown, the blink.
    std::vector<std::pair<const std::vector<RigDocument::Variant>*, std::unique_ptr<PoseFaces>>> faceCache_;
    int faces_ = -1;
    std::optional<Behavior> behavior_;
    std::optional<Mood> mood_;
    std::optional<std::string> expression_, gazePatch_;
    bool active_ = false;
    bool snapFaces_ = false;
    bool facePending_ = false;
    double blinkHold_ = 0.04;
    bool blinkSkipped_ = false;
    std::optional<std::string_view> eventFace_;
    double eventUntil_ = 0;
};

}  // namespace agentspeed::character
