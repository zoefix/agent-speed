#include <gtest/gtest.h>

#include <QSettings>
#include <cmath>
#include <limits>

#include "orb/readout.hpp"
#include "orbs/orbs_store.hpp"
#include "orbs/size_slider_action.hpp"

// DESIGN 2, 2a and 2b: one size range for every style (64–240 pt, changing the style keeps the size), no unit line
// below 96 pt with the title kept in place, and the context menu's slider in 4 pt steps.
namespace {

class OrbSize : public ::testing::Test {
protected:
    void SetUp() override { QSettings().remove(QStringLiteral("orbs")); }
    void TearDown() override { QSettings().remove(QStringLiteral("orbs")); }
};

TEST_F(OrbSize, EveryStyleSharesOneRange) {
    QSettings().setValue(QStringLiteral("orbs"),
                         QStringLiteral(R"({"version":1,"orbs":[{"id":"a","style":"led","size":10},{"id":"b","style":"needle","size":72},)"
                                        R"({"id":"c","style":"minimal","size":500},{"id":"d","style":"reactor","size":64}]})"));
    OrbsStore store;
    ASSERT_EQ(store.orbs().size(), 4u);
    EXPECT_DOUBLE_EQ(store.orbs()[0].size, 64);
    EXPECT_DOUBLE_EQ(store.orbs()[1].size, 72);
    EXPECT_DOUBLE_EQ(store.orbs()[2].size, 240);
    EXPECT_DOUBLE_EQ(store.orbs()[3].size, 64);
}

TEST_F(OrbSize, ChangingTheStyleKeepsTheSize) {
    QSettings().setValue(QStringLiteral("orbs"), QStringLiteral(R"({"version":1,"orbs":[{"id":"m","style":"minimal","size":68}]})"));
    OrbsStore store;
    for (const char* style : {"led", "needle", "liquid", "reactor", "minimal"}) {
        OrbConfig c = *store.find(QStringLiteral("m"));
        c.style = QString::fromLatin1(style);
        store.update(c);
        EXPECT_DOUBLE_EQ(store.find(QStringLiteral("m"))->size, 68) << style;
    }
}

TEST(OrbReadoutSmall, BelowNinetySixPointsTheUnitLineGoesAndTheTitleStays) {
    const orb::ReadoutLayout led;
    EXPECT_GT(led.at(96).unitSize, 0);
    const auto small = led.at(92);
    EXPECT_EQ(small.unitSize, 0);
    EXPECT_EQ(small.title, orb::ReadoutLayout::Title::BelowValue);
    EXPECT_DOUBLE_EQ(small.titlePlace, led.unitGap + led.titlePlace);
    EXPECT_DOUBLE_EQ(small.valueBaseline, led.valueBaseline);

    orb::ReadoutLayout needle;
    needle.title = orb::ReadoutLayout::Title::Fixed;
    needle.titlePlace = 0.338;
    EXPECT_EQ(needle.at(64).unitSize, 0);
    EXPECT_DOUBLE_EQ(needle.at(64).titlePlace, 0.338);

    orb::ReadoutLayout minimal;
    minimal.unitSize = 0;
    minimal.title = orb::ReadoutLayout::Title::BelowValue;
    minimal.titlePlace = 0.098;
    EXPECT_DOUBLE_EQ(minimal.at(64).titlePlace, 0.098);
}

TEST(OrbSizeSlider, SnapsToFourPointStepsInsideTheRange) {
    EXPECT_EQ(SizeSliderAction::snap(10), 64);
    EXPECT_EQ(SizeSliderAction::snap(101), 100);
    EXPECT_EQ(SizeSliderAction::snap(102), 104);
    EXPECT_EQ(SizeSliderAction::snap(148), 148);
    EXPECT_EQ(SizeSliderAction::snap(239), 240);
    EXPECT_EQ(SizeSliderAction::snap(400), 240);
    EXPECT_EQ(SizeSliderAction::snap(std::numeric_limits<double>::quiet_NaN()), 64);
}

}  // namespace
