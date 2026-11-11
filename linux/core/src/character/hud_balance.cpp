#include "agentspeed/character/hud_balance.hpp"

#include <algorithm>
#include <cmath>

namespace agentspeed::character {

std::size_t HudBalance::Order::rank(int split) const {
    for (std::size_t i = 0; i < count; ++i) {
        if (splits[i] == split) return i;
    }
    return count;
}

HudHeights HudBalance::heights(const std::array<CardSize, kHudCardCount>& sizes, const std::array<bool, kHudCardCount>& shown,
                               bool models) {
    HudHeights out{};
    for (HudCard card : kHudCards) {
        const auto i = static_cast<std::size_t>(index(card));
        const bool weighed = card == HudCard::Models ? models : std::find(kHudColumn.begin(), kHudColumn.end(), card) != kHudColumn.end();
        out[i] = weighed && shown[i] && sizes[i].w > 0 ? sizes[i].h : 0;
    }
    return out;
}

HudBalance::Weights HudBalance::weights(const HudHeights& heights, int split, double gap) {
    double left = 0, right = 0;
    int lefts = 0, rights = 0;
    for (std::size_t k = 0; k < kHudColumn.size(); ++k) {
        const double h = heights[static_cast<std::size_t>(index(kHudColumn[k]))];
        if (!(h > 0)) continue;
        if (static_cast<int>(k) < split) {
            left += h;
            ++lefts;
        } else {
            right += h;
            ++rights;
        }
    }
    const double models = heights[static_cast<std::size_t>(index(HudCard::Models))];
    if (models > 0) {
        right += models;
        ++rights;
    }
    return {left + std::max(0, lefts - 1) * gap, right + std::max(0, rights - 1) * gap};
}

HudBalance::Order HudBalance::ranking(const HudHeights& heights, double gap) {
    struct Scored {
        int split, moved;
        double spread;
    };
    std::array<Scored, kSplits> scored{};
    std::size_t n = 0;
    const auto column = static_cast<int>(kHudColumn.size());
    auto add = [&](int split) {
        if (n >= kSplits) return;
        const Weights w = weights(heights, split, gap);
        scored[n] = {split, static_cast<int>(n), std::round(std::fabs(w.left - w.right) / 0.5)};
        ++n;
    };
    add(column);
    for (int k = column - 1; k >= 0; --k) {
        if (heights[static_cast<std::size_t>(index(kHudColumn[static_cast<std::size_t>(k)]))] > 0) add(k);
    }
    // Insertion by (spread, moved): at most six entries, kept in place.
    for (std::size_t i = 1; i < n && i < kSplits; ++i) {
        const Scored item = scored[i];
        std::size_t j = i;
        while (j > 0 && (scored[j - 1].spread > item.spread || (scored[j - 1].spread == item.spread && scored[j - 1].moved > item.moved))) {
            scored[j] = scored[j - 1];
            --j;
        }
        scored[j] = item;
    }
    Order order;
    for (std::size_t i = 0; i < n && i < kSplits; ++i) order.splits[i] = scored[i].split;
    order.count = n;
    return order;
}

HudBalance::Order HudBalance::byCount() {
    Order order;
    for (std::size_t i = 0; i < kSplits; ++i) order.splits[i] = static_cast<int>(kHudColumn.size() - i);
    order.count = kSplits;
    return order;
}

bool HudBalance::isStale(const HudHeights& heights, int split, double gap, double threshold) const {
    if (!reference) return true;
    for (std::size_t i = 0; i < heights.size(); ++i) {
        if (((*reference)[i] > 0) != (heights[i] > 0)) return true;
    }
    const Weights now = weights(heights, split, gap);
    const Weights then = weights(*reference, split, gap);
    return std::fabs(now.left - then.left) > threshold || std::fabs(now.right - then.right) > threshold;
}

bool HudBalance::moving(const HudHeights& heights) {
    if (!seen_) {
        previous_ = heights;
        seen_ = true;
        return false;
    }
    bool changed = false;
    for (std::size_t i = 0; i < heights.size(); ++i) {
        if (std::fabs(previous_[i] - heights[i]) > 0.01) changed = true;
        previous_[i] = heights[i];
    }
    return changed;
}

void HudBalance::settle(const HudHeights& heights) {
    reference = heights;
    pending.reset();
    held = 0;
}

}  // namespace agentspeed::character
