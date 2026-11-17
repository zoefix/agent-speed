#include <gtest/gtest.h>

#include <QCoreApplication>
#include <QElapsedTimer>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QSettings>
#include <functional>

#include "orb/idle_hide.hpp"
#include "orb/orb_idle.hpp"
#include "orbs/orbs_store.hpp"

// DESIGN 2 "hide when idle" and 2b `hideWhenIdle`: off for a new orb and for one saved by an earlier version, a boolean
// or off, saved with its orb and never written to `visible`, copied to new orbs; the rule (60 s idle, deferred by hover
// and a drag, replacing the fade, back on activity, the switch or being shown again, the pointer only reaching an orb
// still on the screen) and its motion (0.6 s ease-in-out to 0, then off the screen; back over 0.12 s; Reduce Motion cuts).
namespace {

class OrbHideWhenIdle : public ::testing::Test {
protected:
    void SetUp() override { QSettings().remove(QStringLiteral("orbs")); }
    void TearDown() override { QSettings().remove(QStringLiteral("orbs")); }

    static void store(const char* json) { QSettings().setValue(QStringLiteral("orbs"), QString::fromUtf8(json)); }
    static QJsonArray saved() {
        const auto doc = QJsonDocument::fromJson(QSettings().value(QStringLiteral("orbs")).toString().toUtf8());
        return doc.object().value(QStringLiteral("orbs")).toArray();
    }
};

TEST_F(OrbHideWhenIdle, ANewOrbAndOneFromAnEarlierVersionDoNotHide) {
    EXPECT_FALSE(OrbConfig().hideWhenIdle);
    EXPECT_FALSE(OrbsStore().orbs().front().hideWhenIdle);

    store(R"({"version":1,"orbs":[{"id":"a","style":"needle","fadeWhenIdle":true,"visible":true}]})");
    OrbsStore orbs;
    EXPECT_FALSE(orbs.find(QStringLiteral("a"))->hideWhenIdle);
    EXPECT_TRUE(orbs.find(QStringLiteral("a"))->fadeWhenIdle);
}

TEST_F(OrbHideWhenIdle, AValueThatIsNotABooleanReadsAsOff) {
    store(R"({"version":1,"orbs":[{"id":"a","hideWhenIdle":"yes"},{"id":"b","hideWhenIdle":1},{"id":"c","hideWhenIdle":null},)"
          R"({"id":"d","hideWhenIdle":[]},{"id":"e","hideWhenIdle":{}},{"id":"f","hideWhenIdle":true}]})");
    OrbsStore orbs;
    for (const char* id : {"a", "b", "c", "d", "e"}) EXPECT_FALSE(orbs.find(QLatin1String(id))->hideWhenIdle) << id;
    EXPECT_TRUE(orbs.find(QStringLiteral("f"))->hideWhenIdle);
}

TEST_F(OrbHideWhenIdle, TheSwitchIsSavedWithItsOrbAndNeverTouchesVisible) {
    store(R"({"version":1,"orbs":[{"id":"a"},{"id":"b"}]})");
    {
        OrbsStore orbs;
        OrbConfig b = *orbs.find(QStringLiteral("b"));
        b.hideWhenIdle = true;
        orbs.update(b);
        EXPECT_TRUE(orbs.find(QStringLiteral("b"))->visible);
    }
    const auto list = saved();
    ASSERT_EQ(list.size(), 2);
    EXPECT_EQ(list.at(0).toObject().value(QStringLiteral("hideWhenIdle")), QJsonValue(false));
    EXPECT_EQ(list.at(1).toObject().value(QStringLiteral("hideWhenIdle")), QJsonValue(true));
    EXPECT_EQ(list.at(1).toObject().value(QStringLiteral("visible")), QJsonValue(true));

    OrbsStore reloaded;
    EXPECT_FALSE(reloaded.find(QStringLiteral("a"))->hideWhenIdle);
    EXPECT_TRUE(reloaded.find(QStringLiteral("b"))->hideWhenIdle);
    OrbConfig b = *reloaded.find(QStringLiteral("b"));
    b.hideWhenIdle = false;
    reloaded.update(b);
    EXPECT_FALSE(OrbsStore().find(QStringLiteral("b"))->hideWhenIdle);
}

TEST_F(OrbHideWhenIdle, AddDuplicateAndSplitCopyItAndMergeKeepsTheTargets) {
    store(R"({"version":1,"orbs":[{"id":"h","hideWhenIdle":true},{"id":"p"}]})");
    OrbsStore orbs;
    const OrbConfig hiding = *orbs.find(QStringLiteral("h"));
    const OrbConfig plain = *orbs.find(QStringLiteral("p"));
    // Add and Split make new orbs with childOf; Duplicate inserts a copy.
    EXPECT_TRUE(OrbConfig::childOf(hiding, QStringLiteral("c1")).hideWhenIdle);
    EXPECT_FALSE(OrbConfig::childOf(plain, QStringLiteral("c2")).hideWhenIdle);
    OrbConfig copy = hiding;
    copy.id = QStringLiteral("copy");
    ASSERT_TRUE(orbs.insert(copy, hiding.id));
    EXPECT_TRUE(orbs.find(QStringLiteral("copy"))->hideWhenIdle);

    // Merge keeps the target with its own switch; undo brings back every orb's.
    orbs.keepUndo();
    orbs.replaceAll({plain});
    EXPECT_FALSE(orbs.orbs().front().hideWhenIdle);
    ASSERT_TRUE(orbs.undo());
    EXPECT_TRUE(orbs.find(QStringLiteral("h"))->hideWhenIdle);
    EXPECT_TRUE(orbs.find(QStringLiteral("copy"))->hideWhenIdle);
    EXPECT_FALSE(orbs.find(QStringLiteral("p"))->hideWhenIdle);
    orbs.keepUndo();
    orbs.replaceAll({hiding});
    EXPECT_TRUE(orbs.orbs().front().hideWhenIdle);
}

// An orb with its switches, idle since `at` (its first tick).
OrbIdle idleSince(bool fade, bool hide, double at = 0) {
    OrbIdle idle;
    idle.configure(fade, hide, at);
    idle.update(true, false, true, at);
    return idle;
}

TEST(OrbIdle, FadeAloneFades) {
    auto idle = idleSince(true, false);
    EXPECT_FALSE(idle.update(true, false, true, 59.9));
    EXPECT_TRUE(idle.update(true, false, true, 60));
    EXPECT_TRUE(idle.faded());
    EXPECT_FALSE(idle.hidden());
    EXPECT_FALSE(idle.update(true, false, true, 1000));
}

TEST(OrbIdle, HidesAfterSixtySecondsOfIdle) {
    auto idle = idleSince(false, true, 100);
    EXPECT_FALSE(idle.update(true, false, true, 130));
    EXPECT_FALSE(idle.update(true, false, true, 159.9));
    EXPECT_FALSE(idle.hidden());
    EXPECT_TRUE(idle.update(true, false, true, 160));
    EXPECT_TRUE(idle.hidden());
    EXPECT_FALSE(idle.update(true, false, false, 161)) << "only once";
}

TEST(OrbIdle, HidingReplacesTheFade) {
    auto idle = idleSince(true, true);
    EXPECT_TRUE(idle.update(true, false, true, 60));
    EXPECT_TRUE(idle.hidden());
    EXPECT_FALSE(idle.faded()) << "no 45 % step";
    EXPECT_FALSE(idle.update(true, false, false, 500));
    EXPECT_FALSE(idle.faded());
}

TEST(OrbIdle, HoverOrADragDefersTheHide) {
    auto idle = idleSince(false, true);
    EXPECT_FALSE(idle.update(true, true, true, 90));
    EXPECT_FALSE(idle.hidden());
    EXPECT_FALSE(idle.update(true, false, true, 91)) << "not at once after the pointer leaves";
    EXPECT_FALSE(idle.update(true, false, true, 92.4));
    EXPECT_TRUE(idle.update(true, false, true, 92.5));
    EXPECT_TRUE(idle.hidden());
}

TEST(OrbIdle, ActivityBringsItBackAndStartsTheSixtySecondsAgain) {
    auto idle = idleSince(false, true);
    idle.update(true, false, true, 60);
    ASSERT_TRUE(idle.hidden());
    EXPECT_TRUE(idle.update(false, false, false, 100)) << "off the screen, activity brings it back";
    EXPECT_FALSE(idle.hidden());
    EXPECT_FALSE(idle.update(false, false, true, 101)) << "nothing to bring back";
    EXPECT_FALSE(idle.update(true, false, true, 160.9));
    EXPECT_TRUE(idle.update(true, false, true, 161));
}

TEST(OrbIdle, ThePointerBringsBackAnOrbStillFadingOutButNeverOneOffTheScreen) {
    auto leaving = idleSince(false, true);
    leaving.update(true, false, true, 60);
    EXPECT_TRUE(leaving.update(true, true, true, 60.3));
    EXPECT_FALSE(leaving.hidden());
    EXPECT_FALSE(leaving.update(true, false, true, 61)) << "the pointer gone, it waits 1.5 s";
    EXPECT_TRUE(leaving.update(true, false, true, 62.5));

    auto away = idleSince(false, true);
    away.update(true, false, true, 60);
    EXPECT_FALSE(away.update(true, true, false, 61));
    EXPECT_TRUE(away.hidden());
    EXPECT_FALSE(away.update(true, false, false, 70));
    EXPECT_TRUE(away.hidden()) << "the pointer passing over its place never brings it back";

    auto faded = idleSince(true, false);
    faded.update(true, false, true, 60);
    EXPECT_TRUE(faded.update(true, true, true, 61));
    EXPECT_FALSE(faded.faded());
}

TEST(OrbIdle, TurningHideOffBringsTheOrbBackAndStartsTheSixtySecondsAgain) {
    auto idle = idleSince(true, true);
    idle.update(true, false, true, 60);
    ASSERT_TRUE(idle.hidden());
    idle.configure(true, false, 100);
    EXPECT_TRUE(idle.update(true, false, false, 100));
    EXPECT_FALSE(idle.hidden());
    EXPECT_FALSE(idle.faded()) << "the fade does not take over at once";
    EXPECT_FALSE(idle.update(true, false, true, 159.9));
    EXPECT_TRUE(idle.update(true, false, true, 160));
    EXPECT_TRUE(idle.faded());
}

TEST(OrbIdle, TurningHideOnEndsTheFadeAndWaitsSixtySeconds) {
    auto idle = idleSince(true, false);
    idle.update(true, false, true, 60);
    ASSERT_TRUE(idle.faded());
    idle.configure(true, true, 100);
    EXPECT_TRUE(idle.update(true, false, true, 100));
    EXPECT_FALSE(idle.faded()) << "back to its full opacity";
    EXPECT_FALSE(idle.hidden());
    EXPECT_FALSE(idle.update(true, false, true, 159.9));
    EXPECT_TRUE(idle.update(true, false, true, 160));
    EXPECT_TRUE(idle.hidden());
}

TEST(OrbIdle, UnchangedOrFadeSwitchesKeepAHiddenOrbHidden) {
    auto idle = idleSince(false, true);
    idle.update(true, false, true, 60);
    idle.configure(false, true, 100);
    EXPECT_FALSE(idle.update(true, false, false, 100));
    EXPECT_TRUE(idle.hidden());
    idle.configure(true, true, 101);
    EXPECT_FALSE(idle.update(true, false, false, 101));
    EXPECT_TRUE(idle.hidden());
    EXPECT_FALSE(idle.faded());
}

TEST(OrbIdle, ShownAgainItComesBackAndTheSixtySecondsStartAgain) {
    auto idle = idleSince(false, true);
    idle.update(true, false, true, 60);
    ASSERT_TRUE(idle.hidden());
    idle.rearmHide(100);
    EXPECT_TRUE(idle.update(true, false, true, 100));
    EXPECT_FALSE(idle.hidden());
    EXPECT_FALSE(idle.update(true, false, true, 159.9));
    EXPECT_TRUE(idle.update(true, false, true, 160));
}

TEST(OrbIdle, SwitchedOffOrNeverIdleItNeverHides) {
    auto off = idleSince(false, false);
    EXPECT_FALSE(off.update(true, false, true, 1000));
    EXPECT_FALSE(off.hidden());

    OrbIdle busy;
    busy.configure(true, true, 0);
    for (double t = 0; t <= 1000; t += 0.5) busy.update(false, false, true, t);
    EXPECT_FALSE(busy.hidden());
    EXPECT_FALSE(busy.faded());
}

// Runs the event loop until `done` or `ms` passed; returns the time it took.
qint64 runUntil(const std::function<bool()>& done, qint64 ms, const std::function<void()>& each = {}) {
    QElapsedTimer clock;
    clock.start();
    while (!done() && clock.elapsed() < ms) {
        QCoreApplication::processEvents(QEventLoop::AllEvents, 5);
        if (each) each();
    }
    return clock.elapsed();
}

TEST(IdleHideMotion, FadesToZeroInOutOverSixTenthsOfASecondThenLeavesTheScreen) {
    int applied = 0, gone = 0;
    IdleHideMotion motion([&] { ++applied; }, [&] { ++gone; });
    EXPECT_DOUBLE_EQ(motion.factor(), 1.0);
    motion.setHidden(true);
    EXPECT_TRUE(motion.running());
    EXPECT_FALSE(motion.gone()) << "still on the screen while it fades";
    double previous = 1;
    bool between = false, early = false;
    const auto took = runUntil([&] { return !motion.running(); }, 3000, [&] {
        EXPECT_LE(motion.factor(), previous + 1e-9);
        previous = motion.factor();
        between = between || (motion.factor() < 0.9 && motion.factor() > 0.1);
        early = early || (motion.running() && gone > 0);
    });
    EXPECT_GE(took, 550);
    EXPECT_TRUE(between) << "eased through the values between";
    EXPECT_FALSE(early) << "it leaves only once faded out";
    EXPECT_DOUBLE_EQ(motion.factor(), 0.0);
    EXPECT_EQ(gone, 1);
    EXPECT_TRUE(motion.gone());
    EXPECT_GT(applied, 3);
}

TEST(IdleHideMotion, ComesBackOverATwelfthOfASecond) {
    int gone = 0;
    IdleHideMotion motion([] {}, [&] { ++gone; });
    motion.setHidden(true, true);
    ASSERT_TRUE(motion.gone());
    motion.setHidden(false);
    EXPECT_TRUE(motion.running());
    EXPECT_FALSE(motion.gone());
    double previous = 0;
    const auto took = runUntil([&] { return !motion.running(); }, 2000, [&] {
        EXPECT_GE(motion.factor(), previous - 1e-9);
        previous = motion.factor();
    });
    EXPECT_GE(took, 100);
    EXPECT_LT(took, 550) << "far quicker than the hide";
    EXPECT_DOUBLE_EQ(motion.factor(), 1.0);
    EXPECT_EQ(gone, 1);
}

TEST(IdleHideMotion, ActivityWhileItFadesOutReturnsFromWhereItIsAndItNeverLeaves) {
    int gone = 0;
    IdleHideMotion motion([] {}, [&] { ++gone; });
    motion.setHidden(true);
    runUntil([&] { return motion.factor() < 0.8; }, 2000);
    const double at = motion.factor();
    ASSERT_GT(at, 0.0);
    ASSERT_LT(at, 1.0);
    motion.setHidden(false);
    EXPECT_GE(motion.factor(), at - 1e-9) << "no jump to 0";
    runUntil([&] { return !motion.running(); }, 2000);
    EXPECT_DOUBLE_EQ(motion.factor(), 1.0);
    runUntil([] { return false; }, 700);
    EXPECT_EQ(gone, 0);
    EXPECT_FALSE(motion.gone());
}

TEST(IdleHideMotion, ReduceMotionCutsBothWays) {
    int applied = 0, gone = 0;
    IdleHideMotion motion([&] { ++applied; }, [&] { ++gone; });
    motion.setHidden(true, true);
    EXPECT_FALSE(motion.running());
    EXPECT_DOUBLE_EQ(motion.factor(), 0.0);
    EXPECT_EQ(applied, 1);
    EXPECT_EQ(gone, 1);
    EXPECT_TRUE(motion.gone());

    motion.setHidden(false, true);
    EXPECT_FALSE(motion.running());
    EXPECT_DOUBLE_EQ(motion.factor(), 1.0);
    EXPECT_EQ(applied, 2);
    EXPECT_EQ(gone, 1);
    motion.setHidden(false, true);
    EXPECT_EQ(applied, 2) << "no change";
}

}  // namespace
