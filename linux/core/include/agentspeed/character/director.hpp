#pragma once

#include <optional>
#include <string>
#include <vector>

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

// Turns the speed level into parameter values (motions/director.py): a groove per band on a beat clock, one action
// at a time from the band's pool, hand acting, breathing, glances, blinks and wind, then springs. Side steps move the
// character around its spot, away from the orb, and she stays where she lands.
class Director {
public:
    Director(const std::vector<Param>& params, std::uint64_t seed = 7, Side out = Side::Right, Side book = Side::Left,
             double orb = -1, bool celebrate = true, MotionProfile profile = MotionProfile());

    // Which arm swings free and which holds the object, for a new pose (of `poseBand`, for its hand set).
    void setSides(Side out, Side book, std::optional<Band> poseBand = std::nullopt);
    void setLevel(int level);
    // Advances one frame; the parameters land in `values` (read the rig's own through `space`).
    void update(double dt);

    // The value of a parameter after the last update, or nullopt when the director does not know it.
    std::optional<double> value(const std::string& name) const;
    // Whether a gesture is playing, and its name.
    std::optional<std::string> activeGesture() const { return gesture_ ? lastGesture_ : std::nullopt; }

    const ParamSpace& space() const { return space_; }
    const MotionProfile& profile() const { return profile_; }
    const std::vector<double>& values() const { return values_; }
    std::optional<Band> band() const { return band_; }
    Band poseBand() const { return poseBand_; }
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
    void write(int slot, double v) {
        values_[static_cast<std::size_t>(slot)] = v;
        present_[static_cast<std::size_t>(slot)] = true;
    }
    void addIdle(int slot, double v);
    void applyGesture(double dt);
    void applySprings(double dt);
    double blink(double dt);
    bool restIdle() const { return profile_.idle == "rest"; }

    ParamSpace space_;
    Slots slots_;
    MotionProfile profile_;
    Band poseBand_ = Band::Idle;
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
};

}  // namespace agentspeed::character
