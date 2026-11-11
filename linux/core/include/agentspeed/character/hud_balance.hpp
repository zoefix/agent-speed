#pragma once

#include <array>
#include <cstddef>
#include <limits>
#include <optional>

#include "agentspeed/character/hud_card.hpp"

namespace agentspeed::character {

// Each card's height as the balance weighs it (0: not weighed), indexed by `HudCard`.
using HudHeights = std::array<double, kHudCardCount>;

// How the column is split between her two sides (DESIGN 2a.6, *Balance*): the left keeps the column's first shown
// cards, the last ones continue under the models card on her right, so that both sides weigh about the same. A side's
// weight is its cards' heights plus the gaps between them; the speed and its title chip do not count. The split is
// chosen again only when a card appears or leaves or a side's weight changes by more than `kThreshold` × D, and a new
// split must hold for a while before cards change sides (`HudWings` keeps the timing).
struct HudBalance {
    // A side's weight change (× D) that chooses the split again.
    static constexpr double kThreshold = 0.06;
    static constexpr std::size_t kSplits = kHudColumn.size() + 1;

    struct Weights {
        double left, right;
    };

    // Splits in rank order, held in place so a warm frame allocates nothing.
    struct Order {
        std::array<int, kSplits> splits{};
        std::size_t count = 0;
        const int* begin() const { return splits.data(); }
        const int* end() const { return splits.data() + count; }
        bool empty() const { return count == 0; }
        // The split's place in the order; `count` when it is not there.
        std::size_t rank(int split) const;
    };

    // The column's shown cards, and the models card while it shows at her right; 0 for the rest.
    static HudHeights heights(const std::array<CardSize, kHudCardCount>& sizes, const std::array<bool, kHudCardCount>& shown,
                              bool models);
    // Each side's weight with the column's cards before `split` at her left and the rest under the models card.
    static Weights weights(const HudHeights& heights, int split, double gap);
    // The distinct splits from the most even to the least: `k` shown cards moved (the split at the first of them),
    // ordered by `|left − right|`, a tie (within half a point) to the smaller `k`.
    static Order ranking(const HudHeights& heights, double gap);
    // Without balance: the most cards at her left first.
    static Order byCount();

    // Whether a card appeared or left, or a side's weight with `split` changed by more than `threshold` points, since
    // the split was chosen.
    bool isStale(const HudHeights& heights, int split, double gap, double threshold) const;
    // Records this frame's heights; true while any changed since the last frame.
    bool moving(const HudHeights& heights);
    // The split was chosen with these heights: nothing waits.
    void settle(const HudHeights& heights);

    // The heights the split was last chosen with; nullopt chooses again.
    std::optional<HudHeights> reference;
    Order order;
    // A new split waiting to have held for `HudWings::kSpillAfter`, and for how long it has.
    std::optional<int> pending;
    double held = 0;
    // Seconds between the last two changes of the split.
    double lastGap = std::numeric_limits<double>::infinity();
    // Whether `order` was made by weight (false below S = 96).
    bool balanced = true;

private:
    HudHeights previous_{};
    bool seen_ = false;
};

}  // namespace agentspeed::character
