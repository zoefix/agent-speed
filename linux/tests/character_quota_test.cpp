// The character orb's 額度 card (DESIGN 2a.6, 額度): its tones, forms and widths, the fit's patience, its lines' motion
// and its heights.
#include <gtest/gtest.h>

#include <cmath>

#include "agentspeed/character/hud_quota.hpp"

using namespace agentspeed::character;

namespace {

constexpr double kInf = std::numeric_limits<double>::infinity();

QuotaLine line(const char* label, int percent, const char* reset = "", bool passed = false) {
    return {label, percent, percent / 100.0, reset, passed};
}

QuotaSource source(const char* key, std::vector<QuotaLine> lines, const char* host = "") {
    return {key, key, host, 0x34D399, std::move(lines)};
}

std::vector<QuotaSource> card() {
    return {source("codex", {line("7d", 25, "週一")}),
            source("claudeCode", {line("5h", 0, "", true), line("7d", 86)}),
            source("claudeDesktop", {line("7d", 94, "10h"), line("Fable 7d", 27, "42m")})};
}

const QuotaMotion::Source* find(const QuotaMotion& motion, const std::string& key) {
    for (const auto& s : motion.sources()) {
        if (s.key == key) return &s;
    }
    return nullptr;
}

// Runs the motion for `seconds` at 60 fps with the same sources.
void run(QuotaMotion& motion, const std::vector<QuotaSource>& sources, double& t, double seconds, bool still = false) {
    for (int n = 0; n < static_cast<int>(seconds * 60 + 0.5); ++n) {
        t += 1.0 / 60;
        motion.sync(sources, false, t, still);
    }
}

// The columns of a card whose label column is 30 pt, its resets 14 pt and its widest header 60 pt (t = 8).
QuotaColumns columns(double header = 60, double reset = 14) { return {30, 20, reset, header}; }

}  // namespace

TEST(QuotaCard, TheFillTurnsAmberFromEightyAndRedFromNinetyFive) {
    EXPECT_EQ(quotaLevel(0), QuotaLevel::Normal);
    EXPECT_EQ(quotaLevel(79), QuotaLevel::Normal);
    EXPECT_EQ(quotaLevel(80), QuotaLevel::Warning);
    EXPECT_EQ(quotaLevel(94), QuotaLevel::Warning);
    EXPECT_EQ(quotaLevel(95), QuotaLevel::Critical);
    EXPECT_EQ(quotaLevel(100), QuotaLevel::Critical);
}

// A window line: the label column, 0.5 t, the bar (5 t, 3 t once shortened), 0.5 t, the `100%` cell, 0.4 t, the reset.
TEST(QuotaCard, ALineSetsItsCellsInColumns) {
    const QuotaMetrics m{8};
    const auto full = quotaLineLayout(QuotaForm::Full, columns(), m);
    EXPECT_DOUBLE_EQ(full.label, 0);
    EXPECT_DOUBLE_EQ(full.bar, 30 + 4);
    EXPECT_DOUBLE_EQ(full.barLength, 40);
    EXPECT_DOUBLE_EQ(full.percentRight, 34 + 40 + 4 + 20);
    EXPECT_DOUBLE_EQ(full.reset, 98 + 3.2);
    EXPECT_DOUBLE_EQ(quotaLineLayout(QuotaForm::ShortBars, columns(), m).barLength, 24);
    // Without headers the dot moves before the source's first label, 0.35 t before it.
    const auto compact = quotaLineLayout(QuotaForm::Compact, columns(), m);
    EXPECT_DOUBLE_EQ(compact.label, 0.55 * 8 + 0.35 * 8);
    EXPECT_DOUBLE_EQ(compact.barLength, 24);
    EXPECT_DOUBLE_EQ(m.barHeight(), 0.26 * 8);
    EXPECT_DOUBLE_EQ(m.small(), 0.9 * 8);
}

// 額度 Room: the reset cells go first, then the bars shorten, then the headers go; the plate adds its padding.
TEST(QuotaCard, FormsNarrowInTheirOrder) {
    const QuotaMetrics m{8};
    const auto w = quotaWidths(columns(90), m, 6.4, false);
    EXPECT_DOUBLE_EQ(w[0], 30 + 4 + 40 + 4 + 20 + 3.2 + 14 + 12.8);
    EXPECT_DOUBLE_EQ(w[1], 98 + 12.8);
    // A header wider than the short line keeps the plate at its width; the compact form drops it.
    EXPECT_DOUBLE_EQ(w[2], 90 + 12.8);
    EXPECT_DOUBLE_EQ(w[3], 7.2 + 30 + 4 + 24 + 4 + 20 + 12.8);
    EXPECT_TRUE(w[0] > w[1] && w[1] > w[2] && w[2] > w[3]);
    // With a short header the compact form is no narrower than the short bars: the fit then keeps the headers.
    const auto narrow = quotaWidths(columns(60), m, 6.4, false);
    EXPECT_LT(narrow[2], narrow[3]);
    EXPECT_EQ(FormFit::narrowest(narrow), 2);
    EXPECT_DOUBLE_EQ(quotaContentWidth(QuotaForm::NoResets, columns(90), m), 98);
}

// LOD: below S = 96 the card has no reset cells; nor does it while no window has a reset time.
TEST(QuotaCard, ResetCellsAreUnusedBelowNinetySixOrWithoutResetTimes) {
    const QuotaMetrics m{8};
    EXPECT_EQ(quotaWidths(columns(), m, 6.4, true)[0], kInf);
    EXPECT_LT(quotaWidths(columns(), m, 6.4, true)[1], kInf);
    EXPECT_EQ(quotaWidths(columns(60, 0), m, 6.4, false)[0], kInf);
    EXPECT_EQ(FormFit::best(quotaWidths(columns(), m, 6.4, true), 1000), 1);
}

// DESIGN 13: the host follows the name while the card's width holds it, else it takes its own line; always in the
// compact form, which has no header to follow.
TEST(QuotaCard, AHostTakesALineOfItsOwnOnlyWhenItDoesNotFit) {
    EXPECT_FALSE(quotaHostOwnLine(QuotaForm::Full, 90, 100));
    EXPECT_FALSE(quotaHostOwnLine(QuotaForm::Full, 100.4, 100));
    EXPECT_TRUE(quotaHostOwnLine(QuotaForm::NoResets, 101, 100));
    EXPECT_TRUE(quotaHostOwnLine(QuotaForm::Compact, 10, 100));
}

TEST(QuotaCard, FormsNarrowAtOnceAndWidenAfterFittingWithRoomToSpare) {
    const QuotaWidths widths = {140, 120, 100, 104};
    FormFit fit;
    fit.update(1.0 / 60, widths, 200, 12, false);
    EXPECT_EQ(fit.form(), QuotaForm::Full);
    fit.update(1.0 / 60, widths, 125, 12, false);
    EXPECT_EQ(fit.form(), QuotaForm::NoResets) << "narrows at once";
    // Room for the full form without the spare changes nothing; with it, after 1.2 s.
    for (int n = 0; n < 120; ++n) fit.update(1.0 / 60, widths, 145, 12, false);
    EXPECT_EQ(fit.form(), QuotaForm::NoResets);
    for (int n = 0; n < 60; ++n) fit.update(1.0 / 60, widths, 160, 12, false);
    EXPECT_EQ(fit.form(), QuotaForm::NoResets);
    for (int n = 0; n < 20; ++n) fit.update(1.0 / 60, widths, 160, 12, false);
    EXPECT_EQ(fit.form(), QuotaForm::Full);
    // Nothing fits: the narrowest, not the last.
    fit.update(1.0 / 60, widths, 50, 12, false);
    EXPECT_EQ(fit.form(), QuotaForm::ShortBars);
    EXPECT_EQ(FormFit::best({kInf, 120, 100, 100}, 110), 2);
    EXPECT_EQ(FormFit::narrowest({kInf, 120, 100, 100}), 3);
    fit.update(1.0 / 60, widths, 300, 12, true);
    EXPECT_EQ(fit.form(), QuotaForm::Full) << "a card off screen takes its best form at once";
}

// 額度 Motion: the sources of a card that appears come with it, their bars growing from empty; a bar eases to a new
// length over 0.4 s and a changed percent crossfades over 0.2 s.
TEST(QuotaMotion, BarsGrowFromEmptyAndEaseToANewLength) {
    QuotaMotion motion;
    double t = 0;
    motion.sync(card(), false, t, false);
    ASSERT_EQ(motion.sources().size(), 3u);
    for (const auto& s : motion.sources()) EXPECT_EQ(s.presence.value(t), 1) << s.key;
    const auto& first = motion.sources()[0].lines[0];
    EXPECT_LT(first.bar(t + 1.0 / 60), 0.05);
    EXPECT_TRUE(motion.moving(t));
    run(motion, card(), t, 0.5);
    EXPECT_NEAR(motion.sources()[0].lines[0].bar(t), 0.25, 1e-9);
    EXPECT_FALSE(motion.moving(t));
    auto next = card();
    next[0].lines[0] = line("7d", 31, "週一");
    motion.sync(next, false, t, false);
    const auto& easing = motion.sources()[0].lines[0];
    EXPECT_GT(easing.bar(t + 0.1), 0.25);
    EXPECT_LT(easing.bar(t + 0.1), 0.31);
    EXPECT_EQ(easing.data.percent, 31);
    EXPECT_EQ(easing.oldPercent, 25);
    EXPECT_NEAR(easing.percentMix(t + 0.1), 0.5, 1e-9);
    run(motion, next, t, 0.5);
    EXPECT_NEAR(motion.sources()[0].lines[0].bar(t), 0.31, 1e-9);
    EXPECT_EQ(motion.sources()[0].lines[0].percentMix(t), 1);
}

// A reset empties its bar over 0.6 s while its reset cell fades out with it.
TEST(QuotaMotion, AResetEmptiesTheBarOverSixTenthsWhileItsCellFades) {
    QuotaMotion motion;
    double t = 0;
    motion.sync(card(), false, t, true);
    EXPECT_EQ(motion.sources()[2].lines[0].reset, "10h");
    auto passed = card();
    passed[2].lines[0] = line("7d", 0, "", true);
    passed[2].lines[0].bar = 0;
    motion.sync(passed, false, t, false);
    const auto& l = motion.sources()[2].lines[0];
    EXPECT_EQ(l.barDuration, QuotaMotion::kResetEmpty);
    EXPECT_GT(l.bar(t + 0.3), 0.05);
    EXPECT_LT(l.bar(t + 0.3), 0.94);
    EXPECT_EQ(l.reset, "10h") << "the cell keeps its text while it fades";
    EXPECT_NEAR(l.resetPresence.value(t + 0.3), 0.5, 1e-9);
    run(motion, passed, t, 0.7);
    EXPECT_EQ(motion.sources()[2].lines[0].bar(t), 0);
    EXPECT_EQ(motion.sources()[2].lines[0].resetPresence.value(t), 0);
    // A new reset time comes in like a crossfade, from nothing.
    passed[2].lines[0] = line("7d", 3, "5h");
    motion.sync(passed, false, t, false);
    EXPECT_TRUE(motion.sources()[2].lines[0].oldReset.empty());
    EXPECT_NEAR(motion.sources()[2].lines[0].resetPresence.value(t + 0.1), 0.5, 1e-9);
}

// A changed reset text crossfades in its cell.
TEST(QuotaMotion, AChangedResetCrossfades) {
    QuotaMotion motion;
    double t = 0;
    motion.sync(card(), false, t, true);
    auto next = card();
    next[2].lines[0].reset = "9h";
    motion.sync(next, false, t, false);
    const auto& l = motion.sources()[2].lines[0];
    EXPECT_EQ(l.reset, "9h");
    EXPECT_EQ(l.oldReset, "10h");
    EXPECT_NEAR(l.resetMix(t + 0.1), 0.5, 1e-9);
    EXPECT_EQ(l.resetPresence.value(t + 0.1), 1);
}

// Reduce Motion (here effects Off): lengths change at once; a source joining or leaving still fades (0.25 s), and one
// that leaves keeps its lines while it fades.
TEST(QuotaMotion, StillChangesLengthsAtOnceButKeepsTheFades) {
    QuotaMotion motion;
    double t = 0;
    const auto all = card();
    const std::vector<QuotaSource> one(all.begin(), all.begin() + 1);
    motion.sync(one, false, t, true);
    EXPECT_EQ(motion.sources()[0].lines[0].bar(t), 0.25);
    motion.sync(card(), false, t, true);
    const auto* joining = find(motion, "claudeCode");
    ASSERT_NE(joining, nullptr);
    EXPECT_LT(joining->presence.value(t + 0.02), 0.1);
    EXPECT_EQ(joining->lines[1].bar(t), 0.86) << "its bars come at their length";
    EXPECT_TRUE(motion.moving(t));
    run(motion, card(), t, 0.5, true);
    EXPECT_EQ(find(motion, "claudeCode")->presence.value(t), 1);
    motion.sync(one, false, t, true);
    const auto* leaving = find(motion, "claudeCode");
    ASSERT_NE(leaving, nullptr);
    EXPECT_TRUE(leaving->leaving());
    EXPECT_EQ(leaving->lines.size(), 2u);
    EXPECT_NEAR(leaving->presence.value(t + 0.125), 0.5, 1e-9);
    // Leaving sources keep their place in the card while they fade.
    EXPECT_EQ(motion.sources()[1].key, "claudeCode");
    run(motion, one, t, 0.5, true);
    ASSERT_EQ(motion.sources().size(), 1u);
    EXPECT_EQ(motion.sources()[0].key, "codex");
}

// A window joining a source fades in where QUOTAS 2 puts it; one leaving fades out in its place.
TEST(QuotaMotion, AWindowJoiningOrLeavingFadesInItsPlace) {
    QuotaMotion motion;
    double t = 0;
    motion.sync(card(), false, t, true);
    auto next = card();
    next[0].lines.insert(next[0].lines.begin(), line("5h", 40, "2h"));
    motion.sync(next, false, t, false);
    const auto& lines = motion.sources()[0].lines;
    ASSERT_EQ(lines.size(), 2u);
    EXPECT_EQ(lines[0].data.label, "5h");
    EXPECT_NEAR(lines[0].presence.value(t + 0.125), 0.5, 1e-9);
    EXPECT_EQ(lines[0].bar(t), 0) << "its bar grows from empty";
    run(motion, next, t, 0.5);
    motion.sync(card(), false, t, false);
    EXPECT_EQ(motion.sources()[0].lines[0].data.label, "5h");
    EXPECT_FALSE(motion.sources()[0].lines[0].presence.wanted);
    run(motion, card(), t, 0.3);
    ASSERT_EQ(motion.sources()[0].lines.size(), 1u);
    EXPECT_EQ(motion.sources()[0].lines[0].data.label, "7d");
}

// A source coming back while it fades out turns around from where it was.
TEST(QuotaMotion, ASourceComingBackTurnsAroundMidFade) {
    QuotaMotion motion;
    double t = 0;
    motion.sync(card(), false, t, true);
    const auto all = card();
    const std::vector<QuotaSource> two(all.begin(), all.begin() + 2);
    motion.sync(two, false, t, true);
    t += 0.1;
    motion.sync(card(), false, t, true);
    const auto* back = find(motion, "claudeDesktop");
    ASSERT_NE(back, nullptr);
    EXPECT_NEAR(back->presence.value(t), 0.6, 1e-9);
    EXPECT_NEAR(back->presence.value(t + 0.05), 0.8, 1e-9);
}

// LOD: below S = 96 the card lists its first source alone; clearing starts afresh.
TEST(QuotaMotion, SmallOrbsListTheFirstSourceAlone) {
    QuotaMotion motion;
    double t = 0;
    motion.sync(card(), true, t, true);
    ASSERT_EQ(motion.sources().size(), 1u);
    EXPECT_EQ(motion.sources()[0].key, "codex");
    motion.sync(card(), false, t, true);
    EXPECT_EQ(motion.sources().size(), 3u);
    EXPECT_LT(find(motion, "claudeCode")->presence.value(t), 0.01);
    motion.clear();
    EXPECT_TRUE(motion.empty());
    motion.sync(card(), false, t, true);
    EXPECT_EQ(find(motion, "claudeCode")->presence.value(t), 1) << "after a clear the sources come with the card";
}

// Heights: per source a header row (and one more for a host on its own line) and its lines, 0.25 t apart; sources
// 0.45 t apart; each share weighted by its presence.
TEST(QuotaCard, HeightsFollowTheFormAndThePresence) {
    const QuotaMetrics m{8};
    QuotaMotion motion;
    motion.sync(card(), false, 0, true);
    const auto& codex = motion.sources()[0];
    const double row = 0.92 * 8, header = 0.92 * 7.2, gap = 2;
    EXPECT_NEAR(quotaBlockHeight(codex, QuotaForm::Full, false, m, 0), header + gap + row, 1e-9);
    EXPECT_NEAR(quotaBlockHeight(codex, QuotaForm::Full, true, m, 0), 2 * (header + gap) + row, 1e-9);
    EXPECT_NEAR(quotaBlockHeight(codex, QuotaForm::Compact, false, m, 0), row, 1e-9);
    const auto& desktop = motion.sources()[2];
    EXPECT_NEAR(quotaBlockHeight(desktop, QuotaForm::NoResets, false, m, 0), header + gap + 2 * row + gap, 1e-9);
    const auto none = [](const QuotaMotion::Source&) { return false; };
    const double whole = quotaContentHeight(motion, QuotaForm::Full, m, 0, none);
    EXPECT_NEAR(whole, 3 * (header + gap) + 5 * row + 2 * gap + 2 * 0.45 * 8, 1e-9);
    // A source fading out gives back its room as it fades.
    const auto all = card();
    const std::vector<QuotaSource> two(all.begin(), all.begin() + 2);
    motion.sync(two, false, 0, true);
    const double half = quotaContentHeight(motion, QuotaForm::Full, m, 0.125, none);
    const double block = quotaBlockHeight(*find(motion, "claudeDesktop"), QuotaForm::Full, false, m, 0.125);
    EXPECT_NEAR(half, whole - 0.5 * (block + 0.45 * 8), 1e-9);
    EXPECT_EQ(quotaContentHeight(QuotaMotion{}, QuotaForm::Full, m, 0, none), 0);
}

// A form change eases the bars and the reset cells with the plate instead of jumping; off screen or still, at once.
TEST(QuotaCard, TheBarsAndResetCellsEaseToANewForm) {
    QuotaFormEase ease;
    ease.update(1.0 / 60, QuotaForm::Full, false);
    EXPECT_EQ(ease.bar(), 5);
    EXPECT_EQ(ease.resets(), 1);
    ease.update(1.0 / 60, QuotaForm::ShortBars, false);
    EXPECT_GT(ease.bar(), 4.9);
    EXPECT_GT(ease.resets(), 0.9);
    for (int n = 0; n < 6; ++n) ease.update(1.0 / 60, QuotaForm::ShortBars, false);
    EXPECT_LT(ease.bar(), 4.9);
    EXPECT_GT(ease.bar(), 3.1);
    for (int n = 0; n < 60; ++n) ease.update(1.0 / 60, QuotaForm::ShortBars, false);
    EXPECT_NEAR(ease.bar(), 3, 0.01);
    EXPECT_NEAR(ease.resets(), 0, 0.01);
    ease.update(1.0 / 60, QuotaForm::NoResets, true);
    EXPECT_EQ(ease.bar(), 5);
    EXPECT_EQ(ease.resets(), 0);
}
