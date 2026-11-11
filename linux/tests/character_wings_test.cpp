// HudWings and HudTrace (DESIGN 2a.6, Readouts), ported from macOS HudWingsTests.
#include <gtest/gtest.h>

#include <algorithm>
#include <cstdlib>
#include <string>

#include "agentspeed/character/hud_quota.hpp"
#include "agentspeed/character/hud_wings.hpp"
#include "agentspeed/character/performer.hpp"
#include "character_support.hpp"

using namespace agentspeed::character;

namespace {

// A frame like a 148 pt orb's (D = 296, window 444): her outline a slim body from y 80 to 400 around x 222, with an
// optional arm held out to one side at chest height.
HudFrame frame(double armOut = 0, double armSide = 1) {
    HudSilhouette s(0, 444, 4);
    for (double y = 80; y <= 400; y += 2) {
        s.add(190, y);
        s.add(254, y);
    }
    if (armOut > 0) {
        for (double y = 150; y <= 190; y += 2) s.add(222 + armSide * armOut, y);
    }
    return HudFrame({0, 0, 444, 444}, s, 296, 222, 80, 170, 140);
}

// Her outline with wide hair at her left from her head to her knees: no room for the column there.
HudFrame wideHairFrame() {
    HudSilhouette s(0, 444, 4);
    for (double y = 80; y <= 400; y += 2) {
        s.add(190, y);
        s.add(254, y);
    }
    for (double y = 80; y <= 300; y += 2) s.add(60, y);
    return HudFrame({0, 0, 444, 444}, s, 296, 222, 80, 170, 140);
}

// The models card's size comes from its shape; these tests leave it and the 額度 card off unless they are about them.
const std::array<CardSize, kHudCardCount> kSizes = {{{80, 40}, {90, 18}, {78, 34}, {70, 30}, {74, 16}, {86, 16}, {0, 0}, {0, 0}}};
const std::array<bool, kHudCardCount> kAll = {true, true, true, true, true, true, false, false};
// The whole column at her left.
constexpr int kWhole = static_cast<int>(kHudColumn.size());

bool overlaps(const HudWings::Place& p, CardSize size, const HudFrame& f) {
    const auto e = f.silhouette.extent(p.y - size.h / 2, p.y + size.h / 2);
    if (!e) return false;
    return p.x + size.w / 2 > e->left && p.x - size.w / 2 < e->right;
}

std::size_t at(HudCard c) { return static_cast<std::size_t>(index(c)); }

}  // namespace

class HudWingsBalances : public ::testing::TestWithParam<bool> {};

TEST_P(HudWingsBalances, CardsSitBesideHerNeverOverHer) {
    const bool balances = GetParam();
    const auto f = frame();
    HudWings wings;
    wings.still = true;
    wings.balances = balances;
    wings.update(1.0 / 60, 0, f, kSizes, kAll);
    std::vector<HudCard> cards = {HudCard::Speed};
    cards.insert(cards.end(), kHudColumn.begin(), kHudColumn.end());
    for (HudCard card : cards) {
        const auto& p = wings.places()[at(card)];
        EXPECT_FALSE(overlaps(p, kSizes[at(card)], f)) << index(card);
        EXPECT_TRUE(p.x - kSizes[at(card)].w / 2 >= 0 && p.x + kSizes[at(card)].w / 2 <= 444);
    }
    EXPECT_GT(wings.places()[0].x, 254);
    if (balances) EXPECT_LT(wings.split(), kWhole);
    else EXPECT_EQ(wings.split(), kWhole);
    for (HudCard card : kHudColumn) {
        if (!kAll[at(card)]) continue;
        if (wings.isAtHerRight(card)) EXPECT_GT(wings.places()[at(card)].x, 254) << index(card);
        else EXPECT_LT(wings.places()[at(card)].x, 190) << index(card);
    }
    const auto& model = wings.places()[1];
    EXPECT_LT(std::fabs(model.x - 222), 1e-9);
    EXPECT_LT(model.y + kSizes[1].h / 2, 80);
    std::vector<double> ys;
    for (HudCard card : kHudColumn) {
        if (kAll[at(card)]) ys.push_back(wings.places()[at(card)].y);
    }
    EXPECT_TRUE(std::is_sorted(ys.begin(), ys.end()));
}

TEST(HudWings, TheSpeedMovesOffAnArmHeldOutToItsSide) {
    const auto f = frame(150);
    HudWings wings;
    wings.still = true;
    for (int n = 0; n < 240; ++n) wings.update(1.0 / 60, n / 60.0, f, kSizes, kAll);
    EXPECT_FALSE(overlaps(wings.places()[0], kSizes[0], f));
}

TEST(HudWings, AHiddenCardFadesAndTheColumnClosesUp) {
    const auto f = frame();
    HudWings wings;
    wings.still = true;
    // The column on her left alone: with balance the remaining cards may change sides too.
    wings.balances = false;
    auto shown = kAll;
    wings.update(1.0 / 60, 0, f, kSizes, shown);
    const double before = wings.places()[at(HudCard::Cost)].y;
    shown[at(HudCard::Network)] = false;
    for (int n = 1; n <= 180; ++n) wings.update(1.0 / 60, n / 60.0, f, kSizes, shown);
    EXPECT_EQ(wings.places()[at(HudCard::Network)].alpha, 0);
    EXPECT_LT(wings.places()[at(HudCard::Cost)].y, before - 25);
}

TEST(HudWings, CardsFloatUnlessStill) {
    const auto f = frame();
    HudWings moving, still;
    still.still = true;
    double spread = 0, tilt = 0;
    std::optional<double> first;
    for (int n = 0; n < 600; ++n) {
        const double t = n / 60.0;
        moving.update(1.0 / 60, t, f, kSizes, kAll);
        still.update(1.0 / 60, t, f, kSizes, kAll);
        const auto& p = moving.places()[at(HudCard::Network)];
        if (!first) first = p.y;
        spread = std::max(spread, std::fabs(p.y - *first));
        tilt = std::max(tilt, std::fabs(p.spin));
        EXPECT_EQ(still.places()[at(HudCard::Network)].spin, 0);
    }
    EXPECT_TRUE(spread > 4 && spread < 15) << spread;
    EXPECT_TRUE(tilt > 0.02 && tilt < 0.06) << tilt;
}

TEST(HudWings, TheColumnContinuesAtHerRightWhenHerLeftIsFull) {
    const auto f = wideHairFrame();
    HudWings wings;
    for (int n = 0; n < 240; ++n) wings.update(1.0 / 60, n / 60.0, f, kSizes, kAll);
    EXPECT_LT(wings.split(), 4);
    const auto& speed = wings.places()[0];
    int moved = 0;
    for (HudCard card : kHudColumn) {
        if (!kAll[at(card)]) continue;
        const auto& p = wings.places()[at(card)];
        EXPECT_GT(p.alpha, 0.9) << index(card);
        const double w = kSizes[at(card)].w, h = kSizes[at(card)].h;
        if (const auto e = f.silhouette.extent(p.y - h / 2 + 3, p.y + h / 2 - 3)) {
            EXPECT_TRUE(p.x + w / 2 < e->left + 6 || p.x - w / 2 > e->right - 6) << index(card) << " overlaps her";
            EXPECT_TRUE(p.x - w / 2 >= 0 && p.x + w / 2 <= 444);
        }
        if (p.x > 222) {
            ++moved;
            EXPECT_GT(p.y, speed.y);
        }
    }
    EXPECT_GT(moved, 0);
}

TEST(HudWings, CardsComeBackOnlyAfterHerLeftStaysClear) {
    HudWings wings;
    wings.balances = false;
    double t = 0;
    for (int n = 0; n < 120; ++n, t += 1.0 / 60) wings.update(1.0 / 60, t, wideHairFrame(), kSizes, kAll);
    const int spilled = wings.split();
    EXPECT_LT(spilled, 4);
    for (int n = 0; n < 30; ++n, t += 1.0 / 60) wings.update(1.0 / 60, t, frame(), kSizes, kAll);
    EXPECT_EQ(wings.split(), spilled);
    for (int n = 0; n < 600; ++n, t += 1.0 / 60) wings.update(1.0 / 60, t, frame(), kSizes, kAll);
    EXPECT_EQ(wings.split(), kWhole);
    for (HudCard card : kHudColumn) EXPECT_LT(wings.places()[at(card)].x, 190);
}

TEST_P(HudWingsBalances, APassingGestureMovesNoCard) {
    HudWings wings;
    wings.balances = GetParam();
    double t = 0;
    int first = -1;
    for (int n = 0; n < 300; ++n, t += 1.0 / 60) {
        const bool hair = n % 120 >= 60 && n % 120 < 72;
        wings.update(1.0 / 60, t, hair ? wideHairFrame() : frame(), kSizes, kAll);
        if (first < 0) first = wings.split();
        EXPECT_EQ(wings.split(), first);
    }
    EXPECT_TRUE(GetParam() || first == kWhole);
}

INSTANTIATE_TEST_SUITE_P(Balance, HudWingsBalances, ::testing::Bool());

TEST(HudWings, FloatsDifferPerCard) {
    const auto a = HudWings::floatOf(HudCard::Performance, 3);
    const auto b = HudWings::floatOf(HudCard::Network, 3);
    EXPECT_NE(a.dx, b.dx);
    EXPECT_NE(a.dy, b.dy);
}

TEST(HudTrace, TraceMarksEveryBandOfHerDrawnParts) {
    // The fixture rig: a 10 px square (y 20–30) over a 20 px face (y 40–60), feet at y 90, drawn at 2 pt per px.
    Performer performer(Pack::load(tiny_pack::make({{"idle", tiny_pack::rigJSON()}})));
    performer.step(1.0 / 60, 0);
    HudSilhouette silhouette(0, 800, 4);
    std::vector<double> scratch;
    const auto anchors = performer.trace(silhouette, 300, 600, 2, scratch);
    auto marked = [&](double y0, double y1) {
        for (double y = y0; y < y1; y += 4) {
            if (!silhouette.extent(y, y + 1)) return false;
        }
        return true;
    };
    EXPECT_TRUE(marked(464, 476) && marked(504, 536));
    EXPECT_FALSE(silhouette.extent(486, 494).has_value());
    const Rig& active = performer.activeRig();
    const double k = 2 * performer.activePlacement().scale;
    std::vector<double> skinned;
    for (std::size_t part = 0; part < active.parts.size(); ++part) {
        active.skin(static_cast<int>(part), performer.world(), skinned);
        for (std::size_t v = 0; v < skinned.size(); v += 2) {
            const double x = 300 + (skinned[v] - performer.activePlacement().feetX) * k;
            const double y = 600 + (skinned[v + 1] - performer.activePlacement().feetY) * k;
            const auto e = silhouette.extent(y, y);
            ASSERT_TRUE(e.has_value());
            EXPECT_TRUE(e->left <= x + 1e-9 && x <= e->right + 1e-9);
        }
    }
    EXPECT_LT(std::fabs(anchors.headTop - 500), 6);
    EXPECT_TRUE(anchors.shoulders < anchors.chest && anchors.chest < 600);
    const auto warm = scratch.size();
    (void)performer.trace(silhouette, 300, 600, 2, scratch);
    EXPECT_EQ(scratch.size(), warm);
}

namespace {

// The models card's shape in these tests: t = 8, 60 pt besides the names, names of 100 pt (more than 9 t), 40 pt with
// the dots and speeds only, 40 pt tall.
const ModelsShape kModels{8, 60, 100, 0, 40, 40};

// The slim body of `frame()` with a skirt reaching right to `right` between y0 and y1 (under the speed at y 150–190).
HudFrame skirtFrame(double right, double y0 = 200, double y1 = 260) {
    HudSilhouette s(0, 444, 4);
    for (double y = 80; y <= 400; y += 2) {
        s.add(190, y);
        s.add(254, y);
    }
    for (double y = y0; y <= y1; y += 2) s.add(right, y);
    return HudFrame({0, 0, 444, 444}, s, 296, 222, 80, 170, 140);
}

std::array<bool, kHudCardCount> withModels() {
    auto shown = kAll;
    shown[at(HudCard::Models)] = true;
    return shown;
}

void run(HudWings& wings, const HudFrame& f, double seconds, double& t, const std::array<bool, kHudCardCount>& shown = withModels()) {
    for (int n = 0; n < static_cast<int>(std::lround(seconds * 60)); ++n, t += 1.0 / 60) wings.update(1.0 / 60, t, f, kSizes, shown);
}

double leftOf(const HudWings& wings, HudCard card) { return wings.places()[at(card)].x - wings.sizes()[at(card)].w / 2; }
double topOf(const HudWings& wings, HudCard card) { return wings.places()[at(card)].y - wings.sizes()[at(card)].h / 2; }

}  // namespace

// DESIGN 2a.6 Models, Place and width: under the speed title chip, 0.022 D below it, its left edge on the speed's line,
// its names 9 t wide.
TEST(HudWingsModels, TheCardSitsUnderTheSpeedOnItsLine) {
    HudWings wings;
    wings.still = true;
    wings.models = kModels;
    double t = 0;
    run(wings, frame(), 1.0 / 60, t);
    EXPECT_EQ(wings.modelsPlace(), ModelsPlace::UnderSpeed);
    EXPECT_EQ(wings.modelsForm(), (ModelsForm{true, 72}));
    EXPECT_NEAR(leftOf(wings, HudCard::Models), leftOf(wings, HudCard::Speed), 1e-6);
    EXPECT_NEAR(topOf(wings, HudCard::Models), topOf(wings, HudCard::Speed) + wings.sizes()[0].h + 0.022 * 296, 1e-6);
    EXPECT_NEAR(wings.sizes()[at(HudCard::Models)].w, 60 + 72, 1e-9);
    EXPECT_EQ(wings.places()[at(HudCard::Models)].alpha, 1);
    EXPECT_TRUE(wings.isAtHerRight(HudCard::Models));
    // Switched off or quiet: it fades and takes no room.
    auto off = withModels();
    off[at(HudCard::Models)] = false;
    run(wings, frame(), 1, t, off);
    EXPECT_EQ(wings.places()[at(HudCard::Models)].alpha, 0);
}

TEST(HudWingsModels, SpilledCardsContinueUnderIt) {
    HudWings wings;
    wings.models = kModels;
    double t = 0;
    run(wings, wideHairFrame(), 4, t);
    ASSERT_LT(wings.split(), 4);
    ASSERT_EQ(wings.modelsPlace(), ModelsPlace::UnderSpeed);
    const double bottom = topOf(wings, HudCard::Models) + wings.sizes()[at(HudCard::Models)].h;
    int spilled = 0;
    for (HudCard card : kHudColumn) {
        if (!wings.isAtHerRight(card)) continue;
        ++spilled;
        EXPECT_GT(topOf(wings, card), bottom - 0.035 * 296) << index(card);
        EXPECT_GT(wings.places()[at(card)].y, wings.places()[at(HudCard::Models)].y) << index(card);
    }
    EXPECT_GT(spilled, 0);
}

// Shorten before hiding: the names narrow as far as 4 t to fit the room to the orb's edge, then the rows keep only the
// dot and the speed; the card's left edge steps right of a wider outline under the speed.
TEST(HudWingsModels, TheNamesNarrowThenGoBeforeTheCardMoves) {
    HudWings wings;
    wings.still = true;
    wings.models = kModels;
    double t = 0;
    run(wings, skirtFrame(300), 1.0 / 60, t);
    EXPECT_EQ(wings.modelsPlace(), ModelsPlace::UnderSpeed);
    EXPECT_TRUE(wings.modelsForm().names);
    EXPECT_NEAR(wings.modelsForm().nameWidth, 436.6 - (300 + 0.035 * 296) - 60, 1e-6);
    EXPECT_GE(leftOf(wings, HudCard::Models), 300 + 0.035 * 296 - 1e-6);
    EXPECT_LE(leftOf(wings, HudCard::Models) + wings.sizes()[at(HudCard::Models)].w, 436.6 + 1e-6);
    run(wings, skirtFrame(345), 1.0 / 60, t);
    EXPECT_EQ(wings.modelsPlace(), ModelsPlace::UnderSpeed);
    EXPECT_FALSE(wings.modelsForm().names);
    EXPECT_NEAR(wings.sizes()[at(HudCard::Models)].w, 40, 1e-9);
    // 4 t is the narrowest name: just below it the names go.
    const double room4t = 60 + 32;
    run(wings, skirtFrame(436.6 - room4t - 0.035 * 296 + 0.5), 1.0 / 60, t);
    EXPECT_FALSE(wings.modelsForm().names);
    run(wings, skirtFrame(436.6 - room4t - 0.035 * 296 - 0.5), 1.0 / 60, t);
    EXPECT_TRUE(wings.modelsForm().names);
    EXPECT_NEAR(wings.modelsForm().nameWidth, 32.5, 1e-6);
}

TEST(HudWingsModels, NamesNarrowAtOnceAndWidenAfterRoomToSpareFor1Point2Seconds) {
    HudWings wings;
    wings.models = kModels;
    double t = 0;
    run(wings, frame(), 2, t);
    ASSERT_EQ(wings.modelsForm().nameWidth, 72);
    run(wings, skirtFrame(300), 1.0 / 60, t);
    EXPECT_LT(wings.modelsForm().nameWidth, 70);
    run(wings, frame(), 1.1, t);
    EXPECT_LT(wings.modelsForm().nameWidth, 70);
    run(wings, frame(), 0.2, t);
    EXPECT_EQ(wings.modelsForm().nameWidth, 72);
}

// It moves down past the spill-over cards' room (up to 0.3 D) once its place has not fit for 0.6 s, fading out and back
// in; a passing gesture moves nothing; it moves back up once under the speed has fit with room to spare for 4 s.
TEST(HudWingsModels, ItMovesDownAfterNotFittingFor0Point6Seconds) {
    HudWings wings;
    wings.models = kModels;
    double t = 0;
    run(wings, frame(), 2, t);
    run(wings, skirtFrame(420), 0.3, t);
    EXPECT_EQ(wings.modelsPlace(), ModelsPlace::UnderSpeed);
    run(wings, frame(), 1, t);
    run(wings, skirtFrame(420), 0.55, t);
    EXPECT_EQ(wings.modelsPlace(), ModelsPlace::UnderSpeed);
    run(wings, skirtFrame(420), 0.1, t);
    double lowest = 1;
    for (int n = 0; n < 60; ++n, t += 1.0 / 60) {
        wings.update(1.0 / 60, t, skirtFrame(420), kSizes, withModels());
        lowest = std::min(lowest, wings.places()[at(HudCard::Models)].alpha);
    }
    EXPECT_EQ(wings.modelsPlace(), ModelsPlace::Lower);
    EXPECT_LT(lowest, 0.05);  // it faded out where it was and back in at the lower place
    run(wings, skirtFrame(420), 3, t);
    EXPECT_GT(wings.places()[at(HudCard::Models)].alpha, 0.95);
    EXPECT_GE(topOf(wings, HudCard::Models), 260 - 0.02 * 296);
    EXPECT_LE(topOf(wings, HudCard::Models), 196.512 + 0.3 * 296 + 0.02 * 296);
    EXPECT_TRUE(wings.modelsForm().names);
    run(wings, frame(), 3.8, t);
    EXPECT_EQ(wings.modelsPlace(), ModelsPlace::Lower);
    run(wings, frame(), 0.5, t);
    EXPECT_EQ(wings.modelsPlace(), ModelsPlace::UnderSpeed);
}

TEST(HudWingsModels, ItHidesOnlyWhenNoPlaceFitsAndComesBackAfter1Point2Seconds) {
    HudWings wings;
    wings.models = kModels;
    double t = 0;
    run(wings, frame(), 2, t);
    const auto crowded = skirtFrame(420, 200, 340);
    run(wings, crowded, 0.7, t);
    EXPECT_EQ(wings.modelsPlace(), ModelsPlace::Hidden);
    run(wings, crowded, 0.6, t);
    EXPECT_EQ(wings.presence()[at(HudCard::Models)], 0);
    EXPECT_EQ(wings.places()[at(HudCard::Models)].alpha, 0);
    run(wings, frame(), 1.1, t);
    EXPECT_EQ(wings.modelsPlace(), ModelsPlace::Hidden);
    run(wings, frame(), 0.2, t);
    EXPECT_EQ(wings.modelsPlace(), ModelsPlace::UnderSpeed);
    run(wings, frame(), 1, t);
    EXPECT_GT(wings.places()[at(HudCard::Models)].alpha, 0.9);
    EXPECT_NEAR(leftOf(wings, HudCard::Models), leftOf(wings, HudCard::Speed), 0.04 * 296);
}

// A card that shows again after it was switched off or quiet starts at its best place, without the patience.
TEST(HudWingsModels, ACardThatShowsAgainStartsAtItsBestPlace) {
    HudWings wings;
    wings.models = kModels;
    double t = 0;
    run(wings, frame(), 2, t);
    auto off = withModels();
    off[at(HudCard::Models)] = false;
    run(wings, skirtFrame(420), 1, t, off);
    EXPECT_EQ(wings.places()[at(HudCard::Models)].alpha, 0);
    run(wings, skirtFrame(420), 1.0 / 60, t);
    EXPECT_EQ(wings.modelsPlace(), ModelsPlace::Lower);
    run(wings, skirtFrame(420), 0.5, t);
    EXPECT_GT(wings.places()[at(HudCard::Models)].alpha, 0.9);
    EXPECT_GE(topOf(wings, HudCard::Models), 260 - 0.02 * 296);
}

namespace {

std::array<CardSize, kHudCardCount> withQuota(CardSize quota) {
    auto sizes = kSizes;
    sizes[at(HudCard::Quota)] = quota;
    return sizes;
}

std::array<bool, kHudCardCount> allShown() {
    std::array<bool, kHudCardCount> shown{};
    shown.fill(true);
    shown[at(HudCard::Models)] = false;
    return shown;
}

}  // namespace

// DESIGN 2a.6 額度: the column's last card, its right edge on the column's line, its room from there to the orb's edge.
TEST(HudWingsQuota, ItIsTheColumnsLastCard) {
    EXPECT_EQ(kHudColumn.back(), HudCard::Quota);
    EXPECT_EQ(kHudCards.back(), HudCard::Quota) << "new cards go last, so the others keep their float phases";
    const auto f = frame();
    HudWings wings;
    wings.still = true;
    wings.balances = false;
    const auto sizes = withQuota({120, 60});
    wings.update(1.0 / 60, 0, f, sizes, allShown());
    const auto& quota = wings.places()[at(HudCard::Quota)];
    const auto& agents = wings.places()[at(HudCard::Agents)];
    EXPECT_GT(quota.y, agents.y);
    EXPECT_FALSE(wings.isAtHerRight(HudCard::Quota));
    const double right = quota.x + 60;
    EXPECT_NEAR(right, agents.x + sizes[at(HudCard::Agents)].w / 2, 1e-6);
    EXPECT_NEAR(wings.quotaRoom(), right - 0.025 * 296, 1e-6);
    EXPECT_FALSE(overlaps(quota, sizes[at(HudCard::Quota)], f));
}

TEST(HudWingsQuota, AnEmptyCardTakesNoRoom) {
    const auto f = frame();
    HudWings with, without;
    with.still = without.still = true;
    with.update(1.0 / 60, 0, f, kSizes, allShown());
    without.update(1.0 / 60, 0, f, kSizes, kAll);
    for (std::size_t k = 0; k + 1 < kHudColumn.size(); ++k) {
        EXPECT_EQ(with.places()[at(kHudColumn[k])].y, without.places()[at(kHudColumn[k])].y) << k;
    }
}

// 額度 Room: it narrows before it spills, so only its narrowest form decides whether the column spills.
TEST(HudWingsQuota, ItsNarrowestFormDecidesWhetherTheColumnSpills) {
    // Her left has room for 147 pt; the card's full form is 170 pt, its narrowest 120.
    auto f = frame();
    for (double y = 80; y <= 400; y += 2) f.silhouette.add(165, y);
    const auto sizes = withQuota({170, 40});
    HudWings wide;
    wide.still = true;
    wide.balances = false;
    wide.update(1.0 / 60, 0, f, sizes, allShown());
    EXPECT_LT(wide.split(), kWhole) << "without a narrower form it spills";
    HudWings narrow;
    narrow.still = true;
    narrow.balances = false;
    narrow.quotaNarrowest = 120;
    narrow.update(1.0 / 60, 0, f, sizes, allShown());
    EXPECT_EQ(narrow.split(), kWhole);
    EXPECT_LT(narrow.quotaRoom(), 170);
    EXPECT_GE(narrow.quotaRoom(), 120);
}

TEST(HudWingsQuota, ASpilledCardMeasuresItsRoomToTheRightEdge) {
    const auto f = wideHairFrame();
    HudWings wings;
    wings.still = true;
    wings.update(1.0 / 60, 0, f, withQuota({110, 44}), allShown());
    EXPECT_TRUE(wings.isAtHerRight(HudCard::Quota));
    const double left = wings.places()[at(HudCard::Quota)].x - 55;
    EXPECT_NEAR(wings.quotaRoom(), 444 - 0.025 * 296 - left, 1e-6);
}

// AGENT_SPEED_CHARACTER_PACKS (a folder of unpacked packs): every pack dances through the bands for 60 s at a 148 pt
// orb while the cards follow her outline, with the owner's cards (性能 with 首字, network, 費用, today, 額度 with three
// sources, three models rows); prints how often cards changed sides and how many card-frames cover her.
// `AGENT_SPEED_HUD_BALANCE=0`: the spill-over alone, for comparison.
TEST(HudWingsPack, CardsStayBesideEveryCharacter) {
    const char* rootEnv = std::getenv("AGENT_SPEED_CHARACTER_PACKS");
    if (!rootEnv) GTEST_SKIP() << "AGENT_SPEED_CHARACTER_PACKS not set";
    namespace fs = std::filesystem;
    std::vector<std::string> ids;
    for (const auto& e : fs::directory_iterator(rootEnv)) {
        if (fs::exists(e.path() / "manifest.json")) ids.push_back(e.path().filename().string());
    }
    std::sort(ids.begin(), ids.end());
    const double d = 296, side = 444;
    auto sizes = std::array<CardSize, kHudCardCount>{{{84, 62}, {90, 18}, {90, 38.5}, {78, 26.4}, {110, 18}, {86, 26.4}, {0, 0}, {150, 85}}};
    // 額度's forms as the renderer fits them to its room: full, no resets, short bars, no headers.
    const QuotaWidths quotaWidths = {150, 130, 115, 104};
    const std::array<double, 4> quotaHeights = {85, 85, 85, 57};
    const char* balanceEnv = std::getenv("AGENT_SPEED_HUD_BALANCE");
    const bool balances = !balanceEnv || std::string(balanceEnv) != "0";
    // Three running models at t = 8: names of 9 t, a 26 pt speed column and 18 pt of unit.
    const ModelsShape models{8, 2 * 6.4 + 8.32 + 6.4 + 26 + 18, 96, 0, 2 * 6.4 + 8.32 + 26 + 18, 2 * 4.4 + 3 * 7.36 + 2 * 2.4};
    auto shown = kAll;
    shown[at(HudCard::Models)] = true;
    shown[at(HudCard::Quota)] = true;
    const std::vector<int> levels = {2, 5, 8, 11, 13, 9, 4, 1};
    for (const auto& id : ids) {
        Performer performer(Pack::load(fs::path(rootEnv) / id), 11);
        HudWings wings;
        wings.models = models;
        wings.balances = balances;
        wings.quotaNarrowest = 104;
        FormFit form;
        HudSilhouette silhouette(0, side, 2);
        std::vector<double> scratch;
        const double ppp = 0.86 * d / performer.idleHeight();
        int changes = 0, last = -1, covered = 0;
        std::array<int, 3> places{};
        int narrowed = 0, dots = 0;
        for (int n = 0; n < 3600; ++n) {
            performer.step(1.0 / 60, levels[std::min<std::size_t>(levels.size() - 1, static_cast<std::size_t>(n / 450))]);
            const auto anchors = performer.trace(silhouette, 222, 222 + 0.45 * d, ppp, scratch);
            const HudFrame f({0, 0, side, side}, silhouette, d, anchors);
            form.update(1.0 / 60, quotaWidths, wings.quotaRoom(), 0.04 * d, n == 0);
            const auto q = static_cast<std::size_t>(form.form());
            sizes[at(HudCard::Quota)] = {quotaWidths[q], quotaHeights[q]};
            wings.update(1.0 / 60, n / 60.0, f, sizes, shown);
            ++places[static_cast<std::size_t>(wings.modelsPlace())];
            if (!wings.modelsForm().names) ++dots;
            else if (wings.modelsForm().nameWidth < HudWings::kModelsNames * models.t - 1e-9) ++narrowed;
            if (wings.split() != last) {
                changes += last < 0 ? 0 : 1;
                last = wings.split();
            }
            std::vector<HudCard> cards(kHudColumn.begin(), kHudColumn.end());
            cards.push_back(HudCard::Speed);
            cards.push_back(HudCard::Models);
            for (HudCard card : cards) {
                const auto& p = wings.places()[at(card)];
                const double w = wings.sizes()[at(card)].w, h = wings.sizes()[at(card)].h;
                if (!(p.alpha > 0.5)) continue;
                const auto e = silhouette.extent(p.y - h / 2 + 4, p.y + h / 2 - 4);
                if (!e) continue;
                const double enters = p.x < anchors.centerX ? p.x + w / 2 - e->left : e->right - (p.x - w / 2);
                if (enters > 6) ++covered;
            }
        }
        std::printf("hud %s: %d side changes in 60 s, %d covered card-frames, split %d; models card under the speed %d, lower %d, "
                    "hidden %d frames, names narrowed %d, dots only %d\n",
                    id.c_str(), changes, covered, wings.split(), places[0], places[1], places[2], narrowed, dots);
    }
}
