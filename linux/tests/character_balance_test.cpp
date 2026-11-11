// HudBalance and the balanced HudWings (DESIGN 2a.6, Balance), ported from macOS HudBalanceTests.
#include <gtest/gtest.h>

#include <algorithm>
#include <cmath>
#include <set>
#include <vector>

#include "agentspeed/character/hud_wings.hpp"

using namespace agentspeed::character;

namespace {

constexpr double kD = 296;
const double kGapPts = 0.022 * kD;
constexpr int kWhole = static_cast<int>(kHudColumn.size());

std::size_t at(HudCard c) { return static_cast<std::size_t>(index(c)); }

HudSilhouette slim() {
    HudSilhouette s(0, 444, 4);
    for (double y = 80; y <= 400; y += 2) {
        s.add(190, y);
        s.add(254, y);
    }
    return s;
}

// A 148 pt orb's frame (D = 296, window 444): her outline a slim body from y 80 to 400 around x 222.
HudFrame body(double bottom = 444) { return HudFrame({0, 0, 444, bottom}, slim(), kD, 222, 80, 170, 140); }

// The body with her arm reaching out over the column at her left (y 130–260): 費用 has no room there.
HudFrame arm() {
    auto s = slim();
    for (double y = 130; y <= 260; y += 2) s.add(222 - 100, y);
    return HudFrame({0, 0, 444, 444}, s, kD, 222, 80, 170, 140);
}

// Her wings spread at her right under the speed, and optionally wide hair at her left.
HudFrame wings(bool hair = false) {
    auto s = slim();
    for (double y = 205; y <= 444; y += 2) s.add(430, y);
    if (hair) {
        for (double y = 80; y <= 300; y += 2) s.add(60, y);
    }
    return HudFrame({0, 0, 444, 444}, s, kD, 222, 80, 170, 140);
}

// The owner's cards at S = 148 (t = 8): 性能 with 思考 and 首字, network, 費用, today, 額度 with two sources (Codex 7d,
// Claude Code 5h and 7d) or three (and Claude Desktop 7d and Fable 7d), the models card with `rows` rows.
std::array<CardSize, kHudCardCount> owner(int sources = 3, int rows = 1) {
    const double quota = sources == 3 ? 85.0 : sources == 2 ? 55.0 : 0;
    return {{{84, 62}, {90, 18}, {90, 38.5}, {78, 26.4}, {110, 18}, {86, 26.4}, {130, 6.56 + rows * 9.76}, {150, quota}}};
}

// The models card 130 pt wide in every form, as tall as `sizes` says.
ModelsShape shapeOf(const std::array<CardSize, kHudCardCount>& sizes) {
    const auto m = sizes[at(HudCard::Models)];
    return ModelsShape{8, m.w, 0, 0, m.w, m.h};
}

std::array<bool, kHudCardCount> everyButModel() {
    std::array<bool, kHudCardCount> shown{};
    shown.fill(true);
    shown[at(HudCard::Model)] = false;
    return shown;
}
const std::array<bool, kHudCardCount> kShown = everyButModel();

HudHeights heightsOf(const std::array<CardSize, kHudCardCount>& sizes, bool models = true) {
    return HudBalance::heights(sizes, kShown, models);
}

int splitMoving(std::initializer_list<HudCard> cards) {
    int split = kWhole;
    for (HudCard c : cards) {
        split = std::min(split, static_cast<int>(std::find(kHudColumn.begin(), kHudColumn.end(), c) - kHudColumn.begin()));
    }
    return split;
}

int first(const HudBalance::Order& order) { return order.empty() ? -1 : order.splits[0]; }

void place(HudWings& w, const HudFrame& f, const std::array<CardSize, kHudCardCount>& sizes) {
    w.models = shapeOf(sizes);
    w.update(1.0 / 60, 0, f, sizes, kShown);
}

struct Timeline {
    HudWings wings;
    double t = 0;
    std::vector<double> changes;

    void run(double seconds, const HudFrame& f, const std::array<CardSize, kHudCardCount>& sizes,
             const std::array<bool, kHudCardCount>& shown = kShown) {
        wings.models = shapeOf(sizes);
        for (int n = 0; n < static_cast<int>(std::lround(seconds * 60)); ++n) {
            const int before = wings.split();
            wings.update(1.0 / 60, t, f, sizes, shown);
            // The first frame's choice is not a change.
            if (t > 0 && wings.split() != before) changes.push_back(t);
            t += 1.0 / 60;
        }
    }
};

}  // namespace

TEST(HudBalance, TheOwnersCardsMoveTheQuotaCardUnderTheModelsCard) {
    // Three sources: 額度 alone weighs about as much as the four cards above it.
    for (int rows = 1; rows <= 4; ++rows) {
        EXPECT_EQ(first(HudBalance::ranking(heightsOf(owner(3, rows)), kGapPts)), splitMoving({HudCard::Quota})) << rows;
    }
    // Two sources: with one or two model rows today follows it; four rows weigh enough without it.
    EXPECT_EQ(first(HudBalance::ranking(heightsOf(owner(2, 1)), kGapPts)), splitMoving({HudCard::Agents, HudCard::Quota}));
    EXPECT_EQ(first(HudBalance::ranking(heightsOf(owner(2, 2)), kGapPts)), splitMoving({HudCard::Agents, HudCard::Quota}));
    EXPECT_EQ(first(HudBalance::ranking(heightsOf(owner(2, 4)), kGapPts)), splitMoving({HudCard::Quota}));
}

TEST(HudBalance, WeightsAreHeightsPlusTheGapsBetweenCards) {
    const auto h = heightsOf(owner(3, 1));
    const auto w = HudBalance::weights(h, splitMoving({HudCard::Quota}), kGapPts);
    EXPECT_NEAR(w.left, 38.5 + 26.4 + 18 + 26.4 + 3 * kGapPts, 1e-9);
    EXPECT_NEAR(w.right, 16.32 + 85 + kGapPts, 1e-9) << "the models card and 額度; the speed does not count";
    EXPECT_NEAR(HudBalance::weights(h, kWhole, kGapPts).right, 16.32, 1e-9);
}

TEST(HudBalance, TheRankingGoesFromTheMostEvenSplitToTheLeast) {
    const auto h = heightsOf(owner(3, 1));
    const auto order = HudBalance::ranking(h, kGapPts);
    std::vector<double> spreads;
    for (int s : order) {
        const auto w = HudBalance::weights(h, s, kGapPts);
        spreads.push_back(std::fabs(w.left - w.right));
    }
    EXPECT_TRUE(std::is_sorted(spreads.begin(), spreads.end()));
    EXPECT_EQ(std::set<int>(order.begin(), order.end()).size(), order.count);
    EXPECT_EQ(order.count, kHudColumn.size() + 1);
}

TEST(HudBalance, NoCardMovesWhileTheColumnIsNotHeavier) {
    auto sizes = owner(0, 4);
    for (HudCard c : {HudCard::Network, HudCard::Cost, HudCard::Agents}) sizes[at(c)] = {0, 0};
    // 性能 alone (38.5) against four model rows (45.6).
    EXPECT_EQ(first(HudBalance::ranking(heightsOf(sizes), kGapPts)), kWhole);
}

TEST(HudBalance, ATieGoesToTheSmallerK) {
    // Two 20 pt cards against a 20 pt models card: k = 0 leaves 40 + gap against 20, k = 1 20 against 40 + gap.
    HudHeights h{};
    h[at(HudCard::Performance)] = 20;
    h[at(HudCard::Network)] = 20;
    h[at(HudCard::Models)] = 20;
    const auto spread = [&](int s) {
        const auto w = HudBalance::weights(h, s, kGapPts);
        return std::fabs(w.left - w.right);
    };
    EXPECT_LT(std::fabs(spread(kWhole) - spread(1)), 0.5);
    EXPECT_EQ(first(HudBalance::ranking(h, kGapPts)), kWhole) << "the tie keeps the cards at her left";
}

TEST(HudBalance, HiddenCardsDoNotCountAndTheSplitSitsAtTheFirstMovedCard) {
    auto sizes = owner(3, 1);
    sizes[at(HudCard::Agents)] = {0, 0};
    const auto h = heightsOf(sizes);
    EXPECT_EQ(h[at(HudCard::Agents)], 0);
    const auto order = HudBalance::ranking(h, kGapPts);
    EXPECT_LT(order.rank(splitMoving({HudCard::Quota})), order.count);
    EXPECT_EQ(order.rank(splitMoving({HudCard::Agents})), order.count) << "a hidden card adds no split of its own";
}

TEST(HudBalance, StaleOnlyWhenACardComesOrGoesOrASideChangesEnough) {
    HudBalance balance;
    const auto h = heightsOf(owner(3, 1));
    const int s = splitMoving({HudCard::Quota});
    const double threshold = 0.06 * kD;
    EXPECT_TRUE(balance.isStale(h, s, kGapPts, threshold)) << "nothing chosen yet";
    balance.settle(h);
    EXPECT_FALSE(balance.isStale(h, s, kGapPts, threshold));
    auto grown = h;
    grown[at(HudCard::Performance)] += 10;
    EXPECT_FALSE(balance.isStale(grown, s, kGapPts, threshold)) << "10 pt is under 0.06 D";
    grown[at(HudCard::Quota)] += 30;
    EXPECT_TRUE(balance.isStale(grown, s, kGapPts, threshold)) << "a source joined 額度";
    auto gone = h;
    gone[at(HudCard::Network)] = 0;
    EXPECT_TRUE(balance.isStale(gone, s, kGapPts, threshold));
    auto noModels = h;
    noModels[at(HudCard::Models)] = 0;
    EXPECT_TRUE(balance.isStale(noModels, s, kGapPts, threshold));
}

TEST(HudWingsBalance, TheQuotaCardContinuesUnderTheModelsCardWithItsLeftEdgeAndAccentTowardHer) {
    const auto f = body();
    HudWings w;
    w.still = true;
    const auto sizes = owner(3, 1);
    place(w, f, sizes);
    EXPECT_EQ(w.split(), splitMoving({HudCard::Quota}));
    EXPECT_TRUE(w.isAtHerRight(HudCard::Quota));
    EXPECT_FALSE(w.isAtHerRight(HudCard::Agents));
    const auto& models = w.places()[at(HudCard::Models)];
    const auto& quota = w.places()[at(HudCard::Quota)];
    const auto m = sizes[at(HudCard::Models)], q = sizes[at(HudCard::Quota)];
    EXPECT_NEAR(quota.y - q.h / 2, models.y + m.h / 2 + kGapPts, 1e-6) << "directly under the models card";
    EXPECT_NEAR(quota.x - q.w / 2, models.x - m.w / 2, 1e-6) << "one left edge on her right";
    std::vector<HudCard> cards(kHudColumn.begin(), kHudColumn.end());
    cards.push_back(HudCard::Speed);
    cards.push_back(HudCard::Models);
    for (HudCard card : cards) {
        const auto& p = w.places()[at(card)];
        const auto s = sizes[at(card)];
        EXPECT_TRUE(p.x - s.w / 2 >= 0 && p.x + s.w / 2 <= 444 && p.y + s.h / 2 <= 444) << index(card);
        const auto e = f.silhouette.extent(p.y - s.h / 2, p.y + s.h / 2);
        if (!e) continue;
        EXPECT_TRUE(p.x + s.w / 2 <= e->left || p.x - s.w / 2 >= e->right) << index(card) << " covers her";
    }
}

TEST(HudWingsBalance, MovedCardsAreNeverLeftOfTheModelsCard) {
    // Her arm held out to her right beside the speed (y 150–190) pushes the speed and the models card right; her
    // outline under them is slim again, yet 額度 keeps the models card's line.
    auto s = slim();
    for (double y = 150; y <= 190; y += 2) s.add(282, y);
    const HudFrame f({0, 0, 444, 444}, s, kD, 222, 80, 170, 140);
    HudWings w;
    w.still = true;
    auto sizes = owner(3, 1);
    sizes[at(HudCard::Quota)].w = 130;
    place(w, f, sizes);
    ASSERT_TRUE(w.isAtHerRight(HudCard::Quota));
    const double modelsLeft = w.places()[at(HudCard::Models)].x - sizes[at(HudCard::Models)].w / 2;
    EXPECT_GT(modelsLeft, 282);
    EXPECT_NEAR(w.places()[at(HudCard::Quota)].x - sizes[at(HudCard::Quota)].w / 2, modelsLeft, 1e-6);
}

TEST(HudWingsBalance, BelowS96TheColumnKeepsItsCardsAtHerLeft) {
    HudWings w;
    w.still = true;
    w.balances = false;
    place(w, body(), owner(3, 1));
    EXPECT_EQ(w.split(), kWhole);
}

TEST(HudWingsBalance, ASourceJoiningMovesCardsOnceTheNewSplitHasHeld) {
    Timeline run;
    const auto f = body();
    run.run(1, f, owner(2, 1));
    EXPECT_EQ(run.wings.split(), splitMoving({HudCard::Agents, HudCard::Quota}));
    EXPECT_TRUE(run.changes.empty()) << "the first choice is not a change";
    // A third source joins 額度: today goes back to her left, 0.6 s later.
    run.run(0.5, f, owner(3, 1));
    EXPECT_EQ(run.wings.split(), splitMoving({HudCard::Agents, HudCard::Quota}));
    run.run(0.2, f, owner(3, 1));
    EXPECT_EQ(run.wings.split(), splitMoving({HudCard::Quota}));
    // today fades out at her right and back in at her left.
    bool dimmed = false;
    for (int n = 0; n < 40; ++n) {
        run.run(1.0 / 60, f, owner(3, 1));
        if (run.wings.places()[at(HudCard::Agents)].alpha < 0.2) dimmed = true;
    }
    EXPECT_TRUE(dimmed);
    EXPECT_FALSE(run.wings.isAtHerRight(HudCard::Agents));
    run.run(1, f, owner(3, 1));
    EXPECT_GT(run.wings.places()[at(HudCard::Agents)].alpha, 0.95);
}

TEST(HudWingsBalance, SmallChangesMoveNothing) {
    Timeline run;
    const auto f = body();
    auto sizes = owner(3, 1);
    run.run(1, f, sizes);
    const int chosen = run.wings.split();
    // 思考 hiding (about 10 pt) and a wider today line: under 0.06 D.
    sizes[at(HudCard::Performance)].h -= 10.24;
    sizes[at(HudCard::Agents)].w += 30;
    run.run(10, f, sizes);
    EXPECT_EQ(run.wings.split(), chosen);
    EXPECT_TRUE(run.changes.empty());
}

TEST(HudWingsBalance, ACardAppearingChoosesAgain) {
    Timeline run;
    const auto f = body();
    auto quiet = kShown;
    quiet[at(HudCard::Network)] = false;
    quiet[at(HudCard::Performance)] = false;
    run.run(1, f, owner(2, 1), quiet);
    const int before = run.wings.split();
    // 性能 and network wake up: the left grows by two cards, so more of the column moves.
    run.run(0.3, f, owner(2, 1));
    EXPECT_EQ(run.wings.split(), before);
    run.run(1, f, owner(2, 1));
    EXPECT_EQ(run.wings.split(), splitMoving({HudCard::Agents, HudCard::Quota}));
    EXPECT_LT(run.wings.split(), before);
}

TEST(HudWingsBalance, ChangesComeNoSoonerThanFourSecondsApartAndTwelveAfterABurst) {
    Timeline run;
    const auto f = body();
    const auto two = owner(2, 1), three = owner(3, 1);
    run.run(1, f, two);
    run.run(1, f, three);
    ASSERT_EQ(run.changes.size(), 1u);
    // The source leaves at once: the split waits until 4 s after the last change.
    run.run(2.5, f, two);
    EXPECT_EQ(run.changes.size(), 1u);
    run.run(1.5, f, two);
    ASSERT_EQ(run.changes.size(), 2u);
    EXPECT_GE(run.changes[1] - run.changes[0], HudWings::kReturnAfter - 1e-9);
    // Two changes within 30 s: the next waits 12 s.
    run.run(10, f, three);
    EXPECT_EQ(run.changes.size(), 2u);
    run.run(3, f, three);
    ASSERT_EQ(run.changes.size(), 3u);
    EXPECT_GE(run.changes[2] - run.changes[1], HudWings::kSettledReturn - 1e-9);
}

TEST(HudWingsBalance, SpillOverWaitsForTheChangeLimitToo) {
    // Her arm reaches over the column right after the split changed: 費用 has no room, but the cards hug her where
    // they are until the split may change again.
    const auto f = body(), reach = arm();
    const auto two = owner(2, 2), three = owner(3, 2);
    Timeline run;
    run.run(1, f, two);
    run.run(1, f, three);
    ASSERT_EQ(run.changes.size(), 1u);
    run.run(2, reach, three);
    EXPECT_EQ(run.changes.size(), 1u);
    run.run(2, reach, three);
    ASSERT_EQ(run.changes.size(), 2u);
    EXPECT_GE(run.changes[1] - run.changes[0], HudWings::kReturnAfter - 1e-9);
    EXPECT_LE(run.wings.split(), splitMoving({HudCard::Cost, HudCard::Agents, HudCard::Quota}));
    // After two changes within 30 s, the spill-over waits 12 s.
    Timeline burst;
    burst.run(1, f, two);
    burst.run(1, f, three);
    burst.run(4.5, f, two);
    ASSERT_EQ(burst.changes.size(), 2u);
    burst.run(10, reach, two);
    EXPECT_EQ(burst.changes.size(), 2u);
    burst.run(2, reach, two);
    ASSERT_EQ(burst.changes.size(), 3u);
    EXPECT_GE(burst.changes[2] - burst.changes[1], HudWings::kSettledReturn - 1e-9);
}

TEST(HudWingsBalance, ASplitThatDoesNotFitGivesWayToTheNextBest) {
    // Her wings spread at her right under the speed: no card fits there, so the column stays at her left.
    HudWings w;
    w.still = true;
    place(w, wings(), owner(3, 1));
    EXPECT_EQ(w.split(), kWhole);
    // A short window: today and 額度 (the most even) run past its bottom, 額度 alone (the next best) does not.
    const auto sizes = owner(2, 1);
    const auto order = HudBalance::ranking(heightsOf(sizes), kGapPts);
    ASSERT_GE(order.count, 2u);
    EXPECT_EQ(order.splits[0], splitMoving({HudCard::Agents, HudCard::Quota}));
    EXPECT_EQ(order.splits[1], splitMoving({HudCard::Quota}));
    HudWings next;
    next.still = true;
    place(next, body(307.4), sizes);
    EXPECT_EQ(next.split(), splitMoving({HudCard::Quota}));
    const auto& quota = next.places()[at(HudCard::Quota)];
    EXPECT_LE(quota.y + sizes[at(HudCard::Quota)].h / 2, 307.4 - 0.025 * kD + 1e-6);
}

TEST(HudWingsBalance, WhenNoSplitFitsTheSpillOverDecides) {
    // Wide hair at her left and wings at her right: nothing fits near her shoulders, so the column moves or hugs her
    // as it does without balance.
    const auto f = wings(true);
    HudWings balanced, alone;
    balanced.still = true;
    alone.still = true;
    alone.balances = false;
    place(balanced, f, owner(3, 1));
    place(alone, f, owner(3, 1));
    EXPECT_EQ(balanced.split(), alone.split());
    EXPECT_EQ(balanced.hugging(), alone.hugging());
    for (HudCard card : kHudCards) EXPECT_EQ(balanced.places()[at(card)].x, alone.places()[at(card)].x) << index(card);
}

TEST(HudWingsBalance, ADanceDoesNotSendCardsBackAndForth) {
    // Her arm swings out to her left over the column for 1.2 s of most 2.5 s for a minute: 費用 has no room.
    const auto f = body(), reach = arm();
    Timeline run;
    for (int n = 0; n < 24; ++n) {
        run.run(1.3, f, owner(3, 2));
        run.run(1.2, n % 3 == 2 ? f : reach, owner(3, 2));
    }
    EXPECT_FALSE(run.changes.empty());
    EXPECT_LE(run.changes.size(), 2u);
    EXPECT_LE(run.wings.split(), splitMoving({HudCard::Cost, HudCard::Agents, HudCard::Quota})) << "費用 waits at her right";
    // Calm again: back to the most even split.
    run.run(15, f, owner(3, 2));
    EXPECT_EQ(run.wings.split(), splitMoving({HudCard::Quota}));
}
