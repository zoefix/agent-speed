#pragma once

#include <array>
#include <cmath>
#include <limits>
#include <optional>
#include <vector>

#include "agentspeed/character/springs.hpp"

namespace agentspeed::character {

// The cards of the character's floating readout (DESIGN 2a.6, *Readouts*): the speed on her right, the model above
// her head, and the column of facts on her left, top to bottom.
enum class HudCard { Speed = 0, Model = 1, Performance = 2, Network = 3, Cost = 4, Agents = 5 };

constexpr int kHudCardCount = 6;
constexpr std::array<HudCard, 6> kHudCards = {HudCard::Speed, HudCard::Model, HudCard::Performance,
                                              HudCard::Network, HudCard::Cost, HudCard::Agents};
// The left column, top to bottom.
constexpr std::array<HudCard, 4> kHudColumn = {HudCard::Performance, HudCard::Network, HudCard::Cost, HudCard::Agents};

inline int index(HudCard c) { return static_cast<int>(c); }

// Her outline this frame as horizontal bands of `step` points from `top`: each band's leftmost and rightmost x of her
// drawn parts. Built by the renderer from the skinned vertices, so the cards clear an arm held out, a raised staff or
// open wings as they move.
class HudSilhouette {
public:
    HudSilhouette(double top, double bottom, double step);

    double top() const { return top_; }
    double step() const { return step_; }
    std::size_t bands() const { return left_.size(); }

    // Empties every band, keeping the storage.
    void clear();
    // Adds a point of her outline (any vertex of a drawn part).
    void add(double x, double y);
    // Adds a segment, so a long triangle edge marks every band it crosses.
    void add(double x0, double y0, double x1, double y1);

    struct Extent {
        double left, right;
    };
    // Her leftmost and rightmost x over the bands that `y0…y1` touches, nullopt when she has nothing there.
    std::optional<Extent> extent(double y0, double y1) const;

private:
    double top_, step_;
    std::vector<double> left_, right_;
};

// Where the readout cards are anchored on her this frame, in window points (y down).
struct HudAnchors {
    double centerX = 0, headTop = 0, chest = 0, shoulders = 0;
    bool operator==(const HudAnchors&) const = default;
};

struct HudBounds {
    double minX, minY, maxX, maxY;
};

// What the cards are laid out around this frame, in window points (y down).
struct HudFrame {
    HudBounds bounds;
    HudSilhouette silhouette;
    double footprint;  // `D` (2 S): every distance of the layout is a share of it
    double centerX, headTop, chest, shoulders;

    HudFrame(HudBounds b, HudSilhouette s, double d, double cx, double head, double chestY, double shouldersY)
        : bounds(b), silhouette(std::move(s)), footprint(d), centerX(cx), headTop(head), chest(chestY),
          shoulders(shouldersY) {}
    HudFrame(HudBounds b, HudSilhouette s, double d, const HudAnchors& a)
        : HudFrame(b, std::move(s), d, a.centerX, a.headTop, a.chest, a.shoulders) {}
};

struct CardSize {
    double w = 0, h = 0;
};

// Places the readout cards beside her (DESIGN 2a.6): each has its own anchor, kept clear of her outline, never behind
// her; anchors trail her on soft springs and every card floats on its own slow figure of eight. When her left side
// has no room for the whole column, its last cards continue under the speed on her right; a card changing sides fades
// out and back in at its new place instead of crossing her.
class HudWings {
public:
    // Where a card is drawn this frame: its center, scale, tilt (radians, clockwise on screen) and opacity.
    struct Place {
        double x = 0, y = 0, scale = 1, spin = 0, alpha = 0;
        bool operator==(const Place&) const = default;
    };
    struct Float {
        double dx, dy, spin, scale;
    };

    static constexpr double kGap = 0.035;
    static constexpr double kMargin = 0.025;
    static constexpr double kColumnGap = 0.022;
    static constexpr double kHeadGap = 0.03;
    static constexpr double kFadeIn = 0.35;
    static constexpr double kFadeOut = 0.5;
    static constexpr double kHopOut = 0.18;
    static constexpr double kHopIn = 0.3;
    static constexpr double kSpillAfter = 0.6;
    static constexpr double kReturnAfter = 4.0;
    static constexpr double kSettleWindow = 30.0;
    static constexpr double kSettledReturn = 12.0;

    HudWings();

    // Advances the layout one frame. `sizes` are the cards' current sizes in points and `shown` which cards should
    // show (switched on and not quiet), both indexed by `HudCard`.
    void update(double dt, double time, const HudFrame& f, const std::array<CardSize, 6>& sizes,
                const std::array<bool, 6>& shown);

    // A card's float at time `t`: offset in shares of `D`, tilt in radians and breathing scale.
    static Float floatOf(HudCard card, double t);

    const std::array<Place, 6>& places() const { return places_; }
    const std::array<double, 6>& presence() const { return presence_; }
    const std::array<CardSize, 6>& sizes() const { return sizes_; }
    int split() const { return split_; }
    bool isAtHerRight(HudCard card) const { return card == HudCard::Speed || right_[static_cast<std::size_t>(index(card))]; }
    // A card is fading in or out or changing sides (frames are needed even when nothing else moves).
    bool isFading() const;

    // No float, tilt or breathing (Reduce Motion, effects Off): the cards still keep clear of her.
    bool still = false;

private:
    struct Fit {
        double shift, over;
    };
    struct Best {
        int split;
        bool hugging;
    };

    bool intrudes(HudCard card, double x, double y, const HudFrame& f) const;
    std::pair<double, double> speedAnchor(const HudFrame& f, CardSize size) const;
    std::pair<double, double> modelAnchor(const HudFrame& f, CardSize size) const;
    void columnAnchors(double dt, const HudFrame& f);
    std::optional<int> roomier(const HudFrame& f);
    Best best(const HudFrame& f);
    Fit fit(const HudFrame& f, int split, double room, double reach, bool aligned);
    double layout(const HudFrame& f, int split, double shift, bool aligned, double room, bool write);

    std::array<Place, 6> places_{};
    std::array<double, 6> presence_{};
    std::array<CardSize, 6> sizes_{};
    std::array<std::pair<SecondOrder, SecondOrder>, 6> anchors_;
    std::array<std::pair<SecondOrder, SecondOrder>, 6> growth_;
    std::array<std::pair<double, double>, 6> targets_{};
    std::array<bool, 6> right_{};
    std::array<double, 6> hop_{};
    int split_ = 4;
    double strain_ = 0;
    double ease_ = 0;
    double sinceChange_ = std::numeric_limits<double>::infinity();
    bool hugging_ = false;
    bool placed_ = false;
};

}  // namespace agentspeed::character
