#pragma once

#include <memory>
#include <optional>
#include <vector>

#include "agentspeed/character/director.hpp"
#include "agentspeed/character/hud.hpp"
#include "agentspeed/character/hud_wings.hpp"
#include "agentspeed/character/pack.hpp"
#include "agentspeed/character/physics.hpp"
#include "agentspeed/character/pose_switch.hpp"

namespace agentspeed::character {

// What happened in a performer's frame, for effects the renderer adds.
struct PerformerEvent {
    enum class Kind { Petals, PoseSwapped };
    Kind kind;
    Band band = Band::Idle;  // PoseSwapped: the band's pose shown now
    bool operator==(const PerformerEvent&) const = default;
};

// How a pose sits on screen relative to the idle pose (spec/CHARACTERS.md, Pose swap): its feet on the idle pose's
// feet, scaled to match.
struct PosePlacement {
    double scale = 1;  // canvas pixels of this pose per canvas pixel of the idle pose
    double feetX = 0, feetY = 0;  // the pose's feet in its own canvas
    bool operator==(const PosePlacement&) const = default;

    std::pair<double, double> screen(double x, double y, double feetOnScreenX, double feetOnScreenY,
                                     double pointsPerPixel) const {
        const double k = pointsPerPixel * scale;
        return {feetOnScreenX + (x - feetX) * k, feetOnScreenY + (y - feetY) * k};
    }
};

// One character on stage: its poses, one director over the union of their parameters, physics per pose, the pose
// swap and the placement of each pose (characters/reference/stage.py). Step it once per frame with the speed level;
// then read `activePose`, `world` and `opacities` to draw. No heap allocation per frame except when an action starts.
class Performer {
public:
    static constexpr double kPosX = 3.0;
    static constexpr double kPosY = 2.0;
    static constexpr double kCrossfadeDuration = 0.3;

    explicit Performer(std::shared_ptr<const Pack> pack, std::uint64_t seed = 7, double orb = 1);

    // Face center to feet, face box diagonal, shoulders to hips (nullopt in packs built without it).
    static std::array<std::optional<double>, 3> measures(const Rig& rig);
    // The middle of the three ratios, so one that the pose throws off is outvoted; face to feet alone otherwise.
    static double poseScale(const std::array<std::optional<double>, 3>& base, const std::array<std::optional<double>, 3>& pose);

    // The pose index a band shows: its own, else the nearest lower band's.
    int poseIndex(Band band) const;

    const Pack& pack() const { return *pack_; }
    const std::shared_ptr<const Pack>& packPointer() const { return pack_; }
    const Director& director() const { return director_; }
    int activePose() const { return activePose_; }
    const Rig& activeRig() const { return pack_->poses[static_cast<std::size_t>(activePose_)].rig; }
    const PosePlacement& activePlacement() const { return placements_[static_cast<std::size_t>(activePose_)]; }
    const std::vector<PosePlacement>& placements() const { return placements_; }
    double idleHeight() const { return idleHeight_; }
    const std::vector<double>& values() const { return values_; }
    const std::vector<Affine2>& world() const { return world_; }
    const std::vector<double>& opacities() const { return opacities_; }
    const std::vector<PerformerEvent>& events() const { return events_; }
    bool exitReached() const { return exitReached_; }
    std::optional<int> fadingPose() const { return fadingPose_; }
    const std::vector<Affine2>& fadingWorld() const { return fadingWorld_; }
    const std::vector<double>& fadingOpacities() const { return fadingOpacities_; }
    double fadeProgress() const { return fadeProgress_; }
    std::optional<Band> band() const { return director_.band(); }
    double intensity() const { return director_.intensity(); }
    // True while she acts out something or turns to swap poses: the frames that need the full 60 fps.
    bool isActing() const { return director_.activeGesture().has_value() || poseSwitch_.isRunning() || fadingPose_.has_value(); }
    // The character's sideways and upward travel this frame, in idle canvas pixels (y down).
    std::pair<double, double> bodyOffset() const;

    // Reduce Motion: no airborne actions, and pose swaps crossfade instead of turning.
    bool calm() const { return calm_; }
    void setCalm(bool calm);
    // How much of the director's motion shows, 0…1 (DESIGN 2a.6 effects: Off 0, Soft 0.5).
    double amplitude = 1.0;

    void requestExit() { pendingExit_ = true; }
    void enterEdgeOn(int level);
    void enterAtRest(int level);
    // Advances one frame at speed level 0...13.
    void step(double dt, int level);

    // The floating readout's geometry for this frame (characters/reference/stage.py).
    HudLayout hudLayout(double feetX, double feetY, double pointsPerPixel, double unit) const;
    // Traces her drawn outline this frame into `silhouette` (cleared first) and returns the anchors of the cards.
    // `scratch` holds skinned positions and only grows.
    HudAnchors trace(HudSilhouette& silhouette, double feetX, double feetY, double pointsPerPixel,
                     std::vector<double>& scratch) const;
    // The anchors of the active pose at rest (face, chin and waist landmarks), moved with her side steps.
    HudAnchors restAnchors(double feetX, double feetY, double pointsPerPixel) const;

private:
    static constexpr int kExitTarget = -1;

    void damp();
    void applySides(int pose);

    std::shared_ptr<const Pack> pack_;
    Director director_;
    int activePose_ = 0;
    std::vector<PosePlacement> placements_;
    double idleHeight_ = 0;
    std::vector<double> values_;
    std::vector<double> rigValues_;
    std::vector<Affine2> world_;
    std::vector<double> opacities_;
    std::vector<PerformerEvent> events_;
    bool exitReached_ = false;
    bool calm_ = false;
    std::optional<int> fadingPose_;
    std::vector<Affine2> fadingWorld_;
    std::vector<double> fadingOpacities_;
    double fadeProgress_ = 1;
    std::vector<Physics> physics_;
    std::vector<std::vector<int>> slotMaps_;
    PoseSwitch poseSwitch_;
    bool pendingExit_ = false;
    Slots slots_;
    double faceWidth_ = 10;
};

// The character an orb shows and the turn between two characters (spec/CHARACTERS.md, Which character): the current
// one turns edge-on, the next one takes over at that instant and turns back.
class Stage {
public:
    explicit Stage(Performer performer) : current_(std::move(performer)) {}

    // Starts the turn to another character; a newer request replaces one still waiting.
    void switchTo(Performer next);
    void step(double dt, int level);

    const Performer& current() const { return current_; }
    Performer& current() { return current_; }
    const std::optional<Performer>& incoming() const { return incoming_; }
    const std::optional<Performer>& outgoing() const { return outgoing_; }
    double outgoingFade() const { return outgoingFade_; }
    bool swapped() const { return swapped_; }

    // Reduce Motion: a character change crossfades instead of turning; passed on to the performers with `amplitude`.
    bool calm = false;
    double amplitude = 1.0;

private:
    Performer current_;
    std::optional<Performer> incoming_;
    std::optional<Performer> outgoing_;
    double outgoingFade_ = 1;
    bool swapped_ = false;
};

}  // namespace agentspeed::character
