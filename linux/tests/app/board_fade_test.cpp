#include <gtest/gtest.h>

#include <QCoreApplication>
#include <QElapsedTimer>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QSettings>

#include "board/board_store.hpp"
#include "orb/idle_fade.hpp"

// DESIGN 2 "fade when idle", shared by the orbs and the boards (2c): the boards document's `fadeWhenIdle` (off for a new
// board and for one saved by an earlier version), the rule (60 s idle; activity, hover and a drag restore at once; 1.5 s
// after the pointer leaves) and its motion (0.6 s ease-in-out to 45 %, a cut back and with Reduce Motion).
namespace {

class BoardFade : public ::testing::Test {
protected:
    void SetUp() override { QSettings().remove(QStringLiteral("boards")); }
    void TearDown() override { QSettings().remove(QStringLiteral("boards")); }

    static void store(const char* json) { QSettings().setValue(QStringLiteral("boards"), QByteArray(json)); }
};

TEST_F(BoardFade, ABoardFromAnEarlierVersionDoesNotFade) {
    store(R"({"version":1,"boards":[{"id":"b","style":"neon","size":"M","sources":"all","opacity":0.8,"alwaysOnTop":true,)"
          R"("clickThrough":false,"allSpaces":true,"visible":true}]})");
    BoardStore boards;
    const auto board = boards.board(QStringLiteral("b"));
    ASSERT_TRUE(board.contains(QStringLiteral("fadeWhenIdle")));
    EXPECT_EQ(board.value(QStringLiteral("fadeWhenIdle")).typeId(), QMetaType::Bool);
    EXPECT_FALSE(board.value(QStringLiteral("fadeWhenIdle")).toBool());
    EXPECT_DOUBLE_EQ(board.value(QStringLiteral("opacity")).toDouble(), 0.8);
}

TEST_F(BoardFade, AValueThatIsNotABooleanReadsAsOff) {
    store(R"({"version":1,"boards":[{"id":"a","fadeWhenIdle":"yes"},{"id":"b","fadeWhenIdle":1},{"id":"c","fadeWhenIdle":null},)"
          R"({"id":"d","fadeWhenIdle":true}]})");
    BoardStore boards;
    for (const char* id : {"a", "b", "c"}) EXPECT_FALSE(boards.board(QLatin1String(id)).value(QStringLiteral("fadeWhenIdle")).toBool()) << id;
    EXPECT_TRUE(boards.board(QStringLiteral("d")).value(QStringLiteral("fadeWhenIdle")).toBool());
}

TEST_F(BoardFade, ANewBoardDoesNotFadeAndTheSwitchPersists) {
    BoardStore boards;
    const auto id = boards.add();
    ASSERT_FALSE(id.isEmpty());
    EXPECT_FALSE(boards.board(id).value(QStringLiteral("fadeWhenIdle")).toBool());

    boards.set(id, QStringLiteral("fadeWhenIdle"), true);
    const auto saved = QJsonDocument::fromJson(QSettings().value(QStringLiteral("boards")).toByteArray()).object();
    EXPECT_TRUE(saved.value(QStringLiteral("boards")).toArray().at(0).toObject().value(QStringLiteral("fadeWhenIdle")).toBool());
    BoardStore reloaded;
    EXPECT_TRUE(reloaded.board(id).value(QStringLiteral("fadeWhenIdle")).toBool());
    reloaded.set(id, QStringLiteral("fadeWhenIdle"), false);
    EXPECT_FALSE(BoardStore().board(id).value(QStringLiteral("fadeWhenIdle")).toBool());
}

TEST(IdleFadeRule, FadesAfterSixtySecondsOfIdle) {
    IdleFade fade;
    EXPECT_FALSE(fade.update(true, true, false, 100));
    EXPECT_FALSE(fade.update(true, true, false, 159.9));
    EXPECT_TRUE(fade.update(true, true, false, 160));
    EXPECT_TRUE(fade.faded());
    EXPECT_FALSE(fade.update(true, true, false, 161)) << "no change";
}

TEST(IdleFadeRule, ActivityRestoresAtOnceAndStartsTheSixtySecondsAgain) {
    IdleFade fade;
    fade.update(true, true, false, 0);
    fade.update(true, true, false, 60);
    ASSERT_TRUE(fade.faded());
    EXPECT_TRUE(fade.update(true, false, false, 70));
    EXPECT_FALSE(fade.faded());
    EXPECT_FALSE(fade.update(true, true, false, 129));
    EXPECT_TRUE(fade.update(true, true, false, 131));
}

TEST(IdleFadeRule, HoverOrADragRestoresAndItFadesAgainAfterThePointerLeaves) {
    IdleFade fade;
    fade.update(true, true, false, 0);
    fade.update(true, true, false, 61);
    ASSERT_TRUE(fade.faded());
    EXPECT_TRUE(fade.update(true, true, true, 62));
    EXPECT_FALSE(fade.faded());
    EXPECT_FALSE(fade.update(true, true, true, 90)) << "kept while the pointer is on it";
    EXPECT_FALSE(fade.update(true, true, false, 91)) << "not at once after it leaves";
    EXPECT_FALSE(fade.update(true, true, false, 92.4));
    EXPECT_TRUE(fade.update(true, true, false, 92.5));
}

TEST(IdleFadeRule, SwitchingItOffRestores) {
    IdleFade fade;
    fade.update(true, true, false, 0);
    fade.update(true, true, false, 60);
    ASSERT_TRUE(fade.faded());
    EXPECT_TRUE(fade.update(false, true, false, 61));
    EXPECT_FALSE(fade.faded());
    EXPECT_FALSE(fade.update(false, true, false, 500));
}

TEST(IdleFadeMotion, FadesOverSixTenthsOfASecondAndComesBackAtOnce) {
    int applied = 0;
    IdleFadeMotion motion([&] { ++applied; });
    EXPECT_DOUBLE_EQ(motion.factor(), 1.0);
    QElapsedTimer clock;
    clock.start();
    motion.setFaded(true);
    EXPECT_TRUE(motion.running());
    double previous = 1;
    bool between = false;
    while (motion.running() && clock.elapsed() < 3000) {
        QCoreApplication::processEvents(QEventLoop::AllEvents, 5);
        EXPECT_LE(motion.factor(), previous + 1e-9);
        previous = motion.factor();
        between = between || (motion.factor() < 0.95 && motion.factor() > 0.5);
    }
    EXPECT_GE(clock.elapsed(), 550);
    EXPECT_TRUE(between) << "eased through the values between";
    EXPECT_DOUBLE_EQ(motion.factor(), IdleFade::kFactor);
    EXPECT_GT(applied, 3);

    motion.setFaded(false);
    EXPECT_FALSE(motion.running());
    EXPECT_DOUBLE_EQ(motion.factor(), 1.0);
}

TEST(IdleFadeMotion, ReduceMotionCutsTheFade) {
    int applied = 0;
    IdleFadeMotion motion([&] { ++applied; });
    motion.setFaded(true, true);
    EXPECT_FALSE(motion.running());
    EXPECT_DOUBLE_EQ(motion.factor(), IdleFade::kFactor);
    EXPECT_EQ(applied, 1);
}

}  // namespace
