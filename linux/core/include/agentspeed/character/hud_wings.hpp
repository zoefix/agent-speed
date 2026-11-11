#pragma once

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <optional>
#include <vector>

#include "agentspeed/character/hud_balance.hpp"
#include "agentspeed/character/hud_card.hpp"
#include "agentspeed/character/springs.hpp"

namespace agentspeed::character {

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

// The models card's widths (DESIGN 2a.6, Models, *Place and width*), in points: `fixed` plus its name column, the
// widest name cut to the name width (9 t, narrowed as far as 4 t), and never narrower than `floor` (the "+N" line);
// `dots` wide with the dots and speeds only.
struct ModelsShape {
    double t = 8;
    double fixed = 0, names = 0, floor = 0, dots = 0;
    double h = 0;
    bool operator==(const ModelsShape&) const = default;
};

// How the models card is drawn: its names cut to `nameWidth` points, or no names (the dots and speeds only).
struct ModelsForm {
    bool names = true;
    double nameWidth = 0;
    bool operator==(const ModelsForm&) const = default;
    double width(const ModelsShape& s) const { return names ? std::max(s.fixed + std::min(s.names, nameWidth), s.floor) : s.dots; }
};

// Where the models card is: under the speed, lower down past the spill-over cards' room, or hidden for want of room.
enum class ModelsPlace { UnderSpeed, Lower, Hidden };

// Places the readout cards beside her (DESIGN 2a.6): each has its own anchor, kept clear of her outline, never behind
// her; anchors trail her on soft springs and every card floats on its own slow figure of eight. The column's last
// cards continue under the speed on her right (under the models card while it shows) so that both sides weigh about the
// same (`HudBalance`), and more of them when her left side has no room for the rest; a card changing sides fades out
// and back in at its new place instead of crossing her. The models card
// narrows its names, then drops them, then moves down past the spilled cards, and only then hides. The 額度 card takes
// the widest of its forms that fits its room, so only its narrowest form decides whether the column spills.
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
    // The models card: names 9 t wide, narrowed as far as 4 t; back from hidden, or its names wider again, after it has
    // fit with 0.04 D to spare for 1.2 s; below the spilled cards at most 0.3 D.
    static constexpr double kModelsNames = 9;
    static constexpr double kModelsNamesMin = 4;
    static constexpr double kModelsBack = 1.2;
    static constexpr double kModelsSpare = 0.04;
    static constexpr double kModelsReach = 0.3;

    HudWings();

    // Advances the layout one frame. `sizes` are the cards' current sizes in points and `shown` which cards should
    // show (switched on and not quiet), both indexed by `HudCard`; the models card's size comes from `models`.
    void update(double dt, double time, const HudFrame& f, const std::array<CardSize, kHudCardCount>& sizes,
                const std::array<bool, kHudCardCount>& shown);

    // A card's float at time `t`: offset in shares of `D`, tilt in radians and breathing scale.
    static Float floatOf(HudCard card, double t);

    const std::array<Place, kHudCardCount>& places() const { return places_; }
    const std::array<double, kHudCardCount>& presence() const { return presence_; }
    const std::array<CardSize, kHudCardCount>& sizes() const { return sizes_; }
    int split() const { return split_; }
    // No split fits with one edge: each card hugs her outline.
    bool hugging() const { return hugging_; }
    bool isAtHerRight(HudCard card) const {
        return card == HudCard::Speed || card == HudCard::Models || right_[static_cast<std::size_t>(index(card))];
    }
    // A card is fading in or out or changing sides (frames are needed even when nothing else moves).
    bool isFading() const;
    ModelsPlace modelsPlace() const { return modelsPlace_; }
    const ModelsForm& modelsForm() const { return form_; }
    // The room from the 額度 card's column edge to the orb's edge at its place this frame (points): its right edge on
    // the column's line at her left, its left edge when it spilled to her right.
    double quotaRoom() const { return quotaRoom_; }

    // No float, tilt or breathing (Reduce Motion, effects Off): the cards still keep clear of her.
    bool still = false;
    // The column is split between her sides by weight (DESIGN 2a.6, *Balance*); off below S = 96, where its cards stay
    // at her left unless they spill over.
    bool balances = true;
    // The models card's widths and height this frame.
    ModelsShape models;
    // The 額度 card's narrowest form (no resets, short bars, no headers; points), 0 for none: the renderer sets it every
    // frame, and only when even that does not fit does the card spill over.
    double quotaNarrowest = 0;

private:
    struct Fit {
        double shift, over;
    };
    struct Best {
        int split;
        bool hugging;
    };
    // The models card at a top: its left edge and the form that fits the room to the orb's edge (nullopt: none).
    struct ModelsFit {
        double left;
        std::optional<ModelsForm> form, spare;
    };

    bool intrudes(HudCard card, double x, double y, const HudFrame& f) const;
    std::pair<double, double> speedAnchor(const HudFrame& f, CardSize size) const;
    std::pair<double, double> modelAnchor(const HudFrame& f, CardSize size) const;
    void columnAnchors(double dt, const HudFrame& f, const std::array<CardSize, kHudCardCount>& wanted,
                       const std::array<bool, kHudCardCount>& shown);
    void choose(const HudFrame& f, const HudHeights& heights);
    void rebalance(double dt, const HudFrame& f, const HudHeights& heights, double gap, bool moving);
    std::optional<int> firstFitting(const HudFrame& f, std::size_t count, double room);
    std::optional<int> better(const HudFrame& f);
    double changeWait() const;
    Best best(const HudFrame& f);
    Fit fit(const HudFrame& f, int split, double room, double reach, bool aligned);
    double layout(const HudFrame& f, int split, double shift, bool aligned, double room, bool write);
    std::optional<ModelsForm> formFor(double room) const;
    double speedBottom() const;
    double spillBottom(const HudFrame& f) const;
    double modelsEdge(const HudFrame& f, double top, double h) const;
    ModelsFit modelsFit(const HudFrame& f, double top) const;
    std::optional<double> lowerTop(const HudFrame& f, bool spare) const;
    void chooseModels(double dt, const HudFrame& f, bool shown);
    void modelsAnchor(const HudFrame& f);
    double fitting(std::size_t card) const;

    std::array<Place, kHudCardCount> places_{};
    std::array<double, kHudCardCount> presence_{};
    std::array<CardSize, kHudCardCount> sizes_{};
    std::array<std::pair<SecondOrder, SecondOrder>, kHudCardCount> anchors_;
    std::array<std::pair<SecondOrder, SecondOrder>, kHudCardCount> growth_;
    std::array<std::pair<double, double>, kHudCardCount> targets_{};
    std::array<bool, kHudCardCount> right_{};
    std::array<double, kHudCardCount> hop_{};
    ModelsPlace modelsPlace_ = ModelsPlace::UnderSpeed;
    ModelsPlace modelsWas_ = ModelsPlace::UnderSpeed;  // its last place before it hid
    std::optional<ModelsPlace> modelsHop_;
    ModelsForm form_;
    double modelsStrain_ = 0, modelsEase_ = 0, modelsWiden_ = 0;
    double lowerTop_ = 0;
    bool snapModels_ = false;
    bool modelsFresh_ = true;  // it shows after it was gone: its best place at once
    int split_ = static_cast<int>(kHudColumn.size());
    double quotaRoom_ = std::numeric_limits<double>::infinity();
    double strain_ = 0;
    double ease_ = 0;
    double sinceChange_ = std::numeric_limits<double>::infinity();
    HudBalance balance_;
    HudHeights weighed_{};
    bool hugging_ = false;
    bool placed_ = false;
};

}  // namespace agentspeed::character
