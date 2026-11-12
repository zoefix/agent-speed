// The inputs of a living character in the app (CHARACTERS 5, Behavior, Inputs): the orb's agents into her activity,
// the single click, the pointer onto her canvas and into her heights, the hit capsule and the events of each frame.
// Ported from the Windows CharacterActivityTests and CharacterLiveInputsTests.
#include <gtest/gtest.h>

#include <QDateTime>
#include <QVariantMap>

#include "agentspeed/character/hud_wings.hpp"
#include "orb/character/character_activity.hpp"
#include "orb/character/character_live.hpp"

using agentspeed::Phase;
using agentspeed::character::AgentActivity;
using orb::AgentFacts;
using orb::CharacterActivity;
using orb::CharacterClickRule;
using orb::CharacterLive;
using orb::CharacterLiveReader;
using orb::LiveGeometry;

namespace {

constexpr qint64 kStart = 1'794'560'400'000;  // 2026-11-12 09:00 UTC

AgentFacts agent(const char* id, Phase phase, std::optional<qint64> inFlight = std::nullopt, std::optional<qint64> lastEnd = std::nullopt) {
    AgentFacts a;
    a.id = id;
    a.phase = phase;
    a.inFlightSinceMs = inFlight;
    a.lastResponseEndMs = lastEnd;
    return a;
}

LiveGeometry geometry() {
    LiveGeometry g;
    g.center = QPointF(222, 222);
    g.diameter = 296;
    g.canvasSide = 444;
    g.face = {222, 120};
    g.height = 200;
    return g;
}

}  // namespace

TEST(CharacterActivity, ThePhaseIsGeneratingThenToolsThenIdle) {
    CharacterActivity activity(kStart);
    activity.update({agent("a", Phase::Tools), agent("b", Phase::Generating, kStart)});
    EXPECT_EQ(activity.phase(), Phase::Generating);
    activity.update({agent("a", Phase::Tools), agent("b", Phase::Idle)});
    EXPECT_EQ(activity.phase(), Phase::Tools);
    activity.update({agent("a", Phase::Idle)});
    EXPECT_EQ(activity.phase(), Phase::Idle);
    activity.update({});
    EXPECT_EQ(activity.phase(), Phase::Idle);
}

TEST(CharacterActivity, TheWaitRunsFromTheEarliestRequestUntilAnyOutput) {
    CharacterActivity activity(kStart);
    activity.update({agent("a", Phase::Generating, kStart + 5000), agent("b", Phase::Generating, kStart + 2000), agent("c", Phase::Tools, kStart)});
    EXPECT_EQ(activity.waitingSinceMs(), kStart + 2000);
    auto flowing = agent("a", Phase::Generating, kStart + 5000);
    flowing.outputSinceInFlight = true;
    activity.update({flowing, agent("b", Phase::Generating, kStart + 2000)});
    EXPECT_FALSE(activity.waitingSinceMs()) << "output flows: nothing waits";
    activity.update({agent("a", Phase::Tools, kStart)});
    EXPECT_FALSE(activity.waitingSinceMs());
}

TEST(CharacterActivity, IdleCountsFromTheNewestEndOrTheFirstReading) {
    CharacterActivity activity(kStart);
    activity.update({agent("a", Phase::Idle)});
    EXPECT_EQ(activity.idleSinceMs(), kStart);
    activity.update({agent("a", Phase::Idle, std::nullopt, kStart - 60'000)});
    EXPECT_EQ(activity.idleSinceMs(), kStart) << "an end before the first reading keeps the origin";
    activity.update({agent("a", Phase::Idle, std::nullopt, kStart + 9000), agent("b", Phase::Idle, std::nullopt, kStart + 4000)});
    EXPECT_EQ(activity.idleSinceMs(), kStart + 9000);
    activity.update({agent("b", Phase::Idle, std::nullopt, kStart + 4000)});
    EXPECT_EQ(activity.idleSinceMs(), kStart + 9000) << "an agent leaving the list never moves it back";
    activity.noteResponseEnd(kStart + 12'000);
    EXPECT_EQ(activity.idleSinceMs(), kStart + 12'000);
}

TEST(CharacterActivity, TroubleFiresWhenAnAgentStartsLoopingNotWhenTheOrbFirstSeesIt) {
    CharacterActivity activity(kStart);
    auto looping = agent("a", Phase::Generating);
    looping.looping = true;
    EXPECT_FALSE(activity.update({looping})) << "already looping at the first reading";
    EXPECT_FALSE(activity.update({looping}));
    auto other = agent("b", Phase::Tools);
    EXPECT_FALSE(activity.update({looping, other}));
    other.looping = true;
    EXPECT_TRUE(activity.update({looping, other}));
    EXPECT_EQ(activity.troubles(), 1);
    other.looping = false;
    activity.update({looping, other});
    other.looping = true;
    EXPECT_TRUE(activity.update({looping, other})) << "looping again";
    EXPECT_EQ(activity.troubles(), 2);
}

TEST(CharacterActivity, TroubleFiresOnceForEachFailureRightAfterAFailure) {
    CharacterActivity activity(kStart);
    auto a = agent("a", Phase::Generating);
    a.repeatFailureMs = kStart - 1000;
    EXPECT_FALSE(activity.update({a})) << "a failure from before the first reading";
    EXPECT_FALSE(activity.update({a}));
    a.repeatFailureMs = kStart + 3000;
    EXPECT_TRUE(activity.update({a}));
    EXPECT_FALSE(activity.update({a})) << "the same failure fires once";
    EXPECT_FALSE(activity.update({})) << "the agent left the list";
    EXPECT_FALSE(activity.update({a})) << "and came back with the failure it had";
    EXPECT_EQ(activity.troubles(), 1);
}

TEST(CharacterActivity, TheAgentMapsCarryTheirFacts) {
    QVariantMap row;
    row.insert(QStringLiteral("id"), QStringLiteral("x"));
    row.insert(QStringLiteral("phase"), QStringLiteral("generating"));
    row.insert(QStringLiteral("inFlightSinceMs"), kStart);
    row.insert(QStringLiteral("outputSinceInFlight"), true);
    row.insert(QStringLiteral("repeatFailureMs"), kStart + 5);
    row.insert(QStringLiteral("looping"), true);
    QVariantMap idle;
    idle.insert(QStringLiteral("id"), QStringLiteral("y"));
    idle.insert(QStringLiteral("phase"), QStringLiteral("idle"));
    idle.insert(QStringLiteral("lastResponseEndMs"), kStart - 5);
    const auto facts = AgentFacts::fromAgents({row, idle});
    ASSERT_EQ(facts.size(), 2u);
    EXPECT_EQ(facts[0].id, "x");
    EXPECT_EQ(facts[0].phase, Phase::Generating);
    EXPECT_EQ(facts[0].inFlightSinceMs, kStart);
    EXPECT_TRUE(facts[0].outputSinceInFlight);
    EXPECT_EQ(facts[0].repeatFailureMs, kStart + 5);
    EXPECT_FALSE(facts[0].lastResponseEndMs);
    EXPECT_TRUE(facts[0].looping);
    EXPECT_EQ(facts[1].phase, Phase::Idle);
    EXPECT_EQ(facts[1].lastResponseEndMs, kStart - 5);
    EXPECT_FALSE(facts[1].inFlightSinceMs);
}

TEST(CharacterClick, AQuickClickFiresOnceTheDoubleClickTimePassed) {
    CharacterClickRule rule(0.4);
    rule.down(10.0, 50, 60, 1);
    const auto delay = rule.up(10.1, 51, 61, false);
    ASSERT_TRUE(delay);
    EXPECT_NEAR(*delay, 0.3, 1e-9);
    EXPECT_FALSE(rule.take(10.2)) << "still waiting for a second click";
    const auto click = rule.take(10.4);
    ASSERT_TRUE(click);
    EXPECT_EQ(*click, (std::pair{50.0, 60.0})) << "where it was pressed";
    EXPECT_FALSE(rule.take(10.5)) << "taken once";
}

TEST(CharacterClick, ADoubleClickNeverFires) {
    CharacterClickRule rule(0.4);
    rule.down(10.0, 50, 60, 1);
    ASSERT_TRUE(rule.up(10.08, 50, 60, false));
    rule.down(10.16, 50, 60, 1);
    rule.down(10.16, 50, 60, 2);
    EXPECT_FALSE(rule.up(10.22, 50, 60, false));
    EXPECT_FALSE(rule.take(11.0));
}

TEST(CharacterClick, ADragALongPressAndAWanderingPressNeverFire) {
    CharacterClickRule rule(0.4);
    rule.down(1, 0, 0, 1);
    EXPECT_FALSE(rule.up(1.1, 1, 1, true)) << "a drag";
    rule.down(2, 0, 0, 1);
    EXPECT_FALSE(rule.up(2.6, 0, 0, false)) << "held 0.6 s";
    rule.down(3, 0, 0, 1);
    EXPECT_FALSE(rule.up(3.1, 3, 3, false)) << "4.2 pt away";
    rule.down(4, 0, 0, 1, false);
    EXPECT_FALSE(rule.up(4.1, 0, 0, false)) << "not armed: no living character";
    EXPECT_FALSE(rule.take(10));
}

TEST(CharacterClick, AReleaseAfterTheDoubleClickTimeFiresAtOnceAndCancelDropsIt) {
    CharacterClickRule rule(0.3);
    rule.down(1, 0, 0, 1);
    EXPECT_EQ(rule.up(1.45, 0, 0, false), 0.0);
    EXPECT_TRUE(rule.take(1.45));
    rule.down(2, 0, 0, 1);
    ASSERT_TRUE(rule.up(2.05, 0, 0, false));
    rule.cancel();
    EXPECT_FALSE(rule.take(3));
}

TEST(CharacterLive, TheActivityLandsOnTheWindowsClock) {
    CharacterActivity activity(kStart);
    activity.update({agent("a", Phase::Generating, kStart, kStart - 30'000), agent("b", Phase::Idle, std::nullopt, kStart - 30'000)});
    CharacterLive live;
    live.update(activity, 1010, kStart + 10'000);
    EXPECT_EQ(live.phase, AgentActivity::Generating);
    ASSERT_TRUE(live.waitingSince);
    EXPECT_NEAR(*live.waitingSince, 1000, 1e-9);
    EXPECT_NEAR(live.idleSince, 1000, 1e-9) << "from the orb's first reading";
    EXPECT_EQ(live.hour, QDateTime::fromMSecsSinceEpoch(kStart + 10'000).time().hour());
    activity.update({agent("a", Phase::Idle, std::nullopt, kStart + 20'000)});
    live.update(activity, 1030, kStart + 30'000);
    EXPECT_EQ(live.phase, AgentActivity::Idle);
    EXPECT_FALSE(live.waitingSince);
    EXPECT_NEAR(live.idleSince, 1020, 1e-9);
}

TEST(CharacterLive, AWindowPointLandsOnHerCanvasWithTheHoverScaleUndone) {
    EXPECT_EQ(CharacterLive::canvasPoint(QPointF(0, 0), 300, 1), QPointF(0, 0));
    EXPECT_EQ(CharacterLive::canvasPoint(QPointF(150, 150), 300, 1.03), QPointF(150, 150));
    const auto right = CharacterLive::canvasPoint(QPointF(253, 150), 300, 1.03);
    EXPECT_NEAR(right.x(), 250, 1e-9);
    EXPECT_NEAR(right.y(), 150, 1e-9);
}

TEST(CharacterLive, TheCursorIsInHerHeightsFromHerFaceWithinTwoHeights) {
    const auto c = CharacterLive::cursor(QPointF(160, 130), {100, 100}, 200);
    ASSERT_TRUE(c);
    EXPECT_NEAR(c->first, 0.3, 1e-12);
    EXPECT_NEAR(c->second, 0.15, 1e-12);
    EXPECT_FALSE(CharacterLive::cursor(QPointF(400, 400), {100, 100}, 200));
    EXPECT_TRUE(CharacterLive::cursor(QPointF(499, 100), {100, 100}, 200));
    EXPECT_FALSE(CharacterLive::cursor(QPointF(501, 100), {100, 100}, 200));
    EXPECT_FALSE(CharacterLive::cursor(QPointF(160, 130), {100, 100}, 0));
}

TEST(CharacterLive, OverIsHerHitCapsuleMovedWithHerSideSteps) {
    const double d = 296, c = 222;
    EXPECT_TRUE(CharacterLive::overCapsule(QPointF(c, c), QPointF(c, c), d, 0));
    EXPECT_FALSE(CharacterLive::overCapsule(QPointF(c + 0.3 * d, c), QPointF(c, c), d, 0));
    EXPECT_TRUE(CharacterLive::overCapsule(QPointF(c + 0.3 * d, c), QPointF(c, c), d, 0.3 * d));
    EXPECT_FALSE(CharacterLive::overCapsule(QPointF(c, c - 0.5 * d), QPointF(c, c), d, 0));
    EXPECT_TRUE(CharacterLive::overCapsule(QPointF(c, c + 0.45 * d), QPointF(c, c), d, 0)) << "the capsule sits 0.04 D below the center";
}

TEST(CharacterLive, EachFrameTakesTheLiveInputsAndFiresEachEventOnce) {
    CharacterLive live;
    live.phase = AgentActivity::Generating;
    live.waitingSince = 95;
    live.idleSince = 40;
    live.hour = 3;
    live.notePeak();
    CharacterLiveReader reader;
    const auto g = geometry();
    const auto first = reader.read(live, 100, g, nullptr);
    EXPECT_EQ(first.phase, AgentActivity::Generating);
    EXPECT_EQ(first.wait, 5.0);
    EXPECT_EQ(first.idle, 0);
    EXPECT_EQ(first.hour, 3);
    EXPECT_FALSE(first.newPeak) << "an event before her first frame never fires";
    EXPECT_FALSE(first.cursor);
    EXPECT_FALSE(first.over) << "no window: no pointer";

    live.notePeak();
    live.noteTrouble();
    live.phase = AgentActivity::Idle;
    const auto second = reader.read(live, 101, g, nullptr);
    EXPECT_TRUE(second.newPeak);
    EXPECT_TRUE(second.trouble);
    EXPECT_FALSE(second.wait);
    EXPECT_EQ(second.idle, 61);
    const auto third = reader.read(live, 102, g, nullptr);
    EXPECT_FALSE(third.newPeak);
    EXPECT_FALSE(third.trouble);

    live.celebratesPeaks = false;
    live.notePeak();
    EXPECT_FALSE(reader.read(live, 103, g, nullptr).newPeak) << "peaks cheer her only while they are celebrated";

    live.scripted = true;
    live.scriptedCursor = std::pair{0.2, 0.1};
    const auto scripted = reader.read(live, 104, g, nullptr);
    ASSERT_TRUE(scripted.cursor);
    EXPECT_NEAR(scripted.cursor->first, 0.2, 1e-9);
    EXPECT_NEAR(scripted.cursor->second, 0.1, 1e-9);
    live.clicks += 1;
    EXPECT_FALSE(reader.read(live, 105, g, nullptr).click) << "without her traced outline a click lands on nothing";
}

TEST(CharacterLive, TheWindowsPointerIsReadUnlessADragMovesTheOrb) {
    CharacterLive live;
    std::optional<QPointF> pointer = QPointF(222, 222);
    int asked = 0;
    live.pointer = [&] {
        ++asked;
        return pointer;
    };
    CharacterLiveReader reader;
    const auto g = geometry();
    auto in = reader.read(live, 1, g, nullptr);
    ASSERT_TRUE(in.cursor);
    EXPECT_NEAR(in.cursor->second, (222.0 - 120) / 200, 1e-12);
    EXPECT_TRUE(in.over) << "the canvas center is inside her capsule";
    EXPECT_EQ(asked, 1);

    live.dragging = true;
    EXPECT_FALSE(reader.read(live, 2, g, nullptr).over);
    live.dragging = false;
    pointer.reset();
    EXPECT_FALSE(reader.read(live, 3, g, nullptr).cursor);
    pointer = QPointF(440, 4);
    EXPECT_TRUE(reader.read(live, 4, g, nullptr).cursor);
    pointer = QPointF(1400, 222);
    in = reader.read(live, 5, g, nullptr);
    EXPECT_FALSE(in.cursor) << "beyond two heights";
    EXPECT_FALSE(in.over);
}

TEST(CharacterLive, AClickOnHerDrawnOutlinePokesHerAndBesideItNothing) {
    agentspeed::character::HudSilhouette outline(0, 444, 4);
    for (double y = 100; y <= 400; y += 2) {
        outline.add(200, y);
        outline.add(244, y);
    }
    CharacterLive live;
    CharacterLiveReader reader;
    const auto g = geometry();
    (void)reader.read(live, 1, g, &outline);
    live.click(QPointF(222, 300));
    EXPECT_TRUE(reader.read(live, 2, g, &outline).click);
    EXPECT_FALSE(reader.read(live, 3, g, &outline).click) << "fired once";
    live.click(QPointF(300, 300));
    EXPECT_FALSE(reader.read(live, 4, g, &outline).click) << "beside her figure";
    // The hover scale grows the drawing around the center: a press at 246.5 is beyond her edge (244 + 2 slack) unscaled,
    // and on it while she is drawn 3 % larger.
    live.click(QPointF(246.5, 300));
    EXPECT_FALSE(reader.read(live, 5, g, &outline).click);
    live.click(QPointF(246.5, 300));
    LiveGeometry hovered = g;
    hovered.contentScale = 1.03;
    EXPECT_TRUE(reader.read(live, 6, hovered, &outline).click);
}
