// HudWings and HudTrace (DESIGN 2a.6, Readouts), ported from macOS HudWingsTests.
#include <gtest/gtest.h>

#include <algorithm>
#include <cstdlib>

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

const std::array<CardSize, 6> kSizes = {{{80, 40}, {90, 18}, {78, 34}, {70, 30}, {74, 16}, {86, 16}}};
const std::array<bool, 6> kAll = {true, true, true, true, true, true};

bool overlaps(const HudWings::Place& p, CardSize size, const HudFrame& f) {
    const auto e = f.silhouette.extent(p.y - size.h / 2, p.y + size.h / 2);
    if (!e) return false;
    return p.x + size.w / 2 > e->left && p.x - size.w / 2 < e->right;
}

std::size_t at(HudCard c) { return static_cast<std::size_t>(index(c)); }

}  // namespace

TEST(HudWings, CardsSitBesideHerNeverOverHer) {
    const auto f = frame();
    HudWings wings;
    wings.still = true;
    wings.update(1.0 / 60, 0, f, kSizes, kAll);
    std::vector<HudCard> cards = {HudCard::Speed};
    cards.insert(cards.end(), kHudColumn.begin(), kHudColumn.end());
    for (HudCard card : cards) {
        const auto& p = wings.places()[at(card)];
        EXPECT_FALSE(overlaps(p, kSizes[at(card)], f)) << index(card);
        EXPECT_TRUE(p.x - kSizes[at(card)].w / 2 >= 0 && p.x + kSizes[at(card)].w / 2 <= 444);
    }
    EXPECT_GT(wings.places()[0].x, 254);
    for (HudCard card : kHudColumn) EXPECT_LT(wings.places()[at(card)].x, 190);
    const auto& model = wings.places()[1];
    EXPECT_LT(std::fabs(model.x - 222), 1e-9);
    EXPECT_LT(model.y + kSizes[1].h / 2, 80);
    std::vector<double> ys;
    for (HudCard card : kHudColumn) ys.push_back(wings.places()[at(card)].y);
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
    double t = 0;
    for (int n = 0; n < 120; ++n, t += 1.0 / 60) wings.update(1.0 / 60, t, wideHairFrame(), kSizes, kAll);
    const int spilled = wings.split();
    EXPECT_LT(spilled, 4);
    for (int n = 0; n < 30; ++n, t += 1.0 / 60) wings.update(1.0 / 60, t, frame(), kSizes, kAll);
    EXPECT_EQ(wings.split(), spilled);
    for (int n = 0; n < 600; ++n, t += 1.0 / 60) wings.update(1.0 / 60, t, frame(), kSizes, kAll);
    EXPECT_EQ(wings.split(), 4);
    for (HudCard card : kHudColumn) EXPECT_LT(wings.places()[at(card)].x, 190);
}

TEST(HudWings, APassingGestureMovesNoCard) {
    HudWings wings;
    double t = 0;
    for (int n = 0; n < 300; ++n, t += 1.0 / 60) {
        const bool hair = n % 120 >= 60 && n % 120 < 72;
        wings.update(1.0 / 60, t, hair ? wideHairFrame() : frame(), kSizes, kAll);
        EXPECT_EQ(wings.split(), 4);
    }
}

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

// AGENT_SPEED_CHARACTER_PACKS (a folder of unpacked packs): every pack dances through the bands for 60 s at a 148 pt
// orb while the cards follow her outline; prints how often cards changed sides and how many card-frames cover her.
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
    const std::array<CardSize, 6> sizes = {{{84, 62}, {90, 18}, {80, 28}, {64, 27}, {96, 18}, {62, 27}}};
    const std::vector<int> levels = {2, 5, 8, 11, 13, 9, 4, 1};
    for (const auto& id : ids) {
        Performer performer(Pack::load(fs::path(rootEnv) / id), 11);
        HudWings wings;
        HudSilhouette silhouette(0, side, 2);
        std::vector<double> scratch;
        const double ppp = 0.86 * d / performer.idleHeight();
        int changes = 0, last = -1, covered = 0;
        for (int n = 0; n < 3600; ++n) {
            performer.step(1.0 / 60, levels[std::min<std::size_t>(levels.size() - 1, static_cast<std::size_t>(n / 450))]);
            const auto anchors = performer.trace(silhouette, 222, 222 + 0.45 * d, ppp, scratch);
            const HudFrame f({0, 0, side, side}, silhouette, d, anchors);
            wings.update(1.0 / 60, n / 60.0, f, sizes, kAll);
            if (wings.split() != last) {
                changes += last < 0 ? 0 : 1;
                last = wings.split();
            }
            std::vector<HudCard> cards(kHudColumn.begin(), kHudColumn.end());
            cards.push_back(HudCard::Speed);
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
        std::printf("hud %s: %d side changes in 60 s, %d covered card-frames, split %d\n", id.c_str(), changes, covered,
                    wings.split());
    }
}
