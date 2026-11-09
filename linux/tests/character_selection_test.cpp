// CHARACTERS 7 (which character an orb shows) and the footprint of DESIGN 2a.6, ported from macOS CharacterOrbTests.
#include <gtest/gtest.h>

#include "agentspeed/character/selection.hpp"

using namespace agentspeed::character;

namespace {
const std::vector<CharacterSelection::Pack> kPacks = {{"claude", {"anthropic"}}, {"gpt", {"openai"}}};
using Rates = std::map<std::string, double>;
}  // namespace

TEST(CharacterSelection, NothingInstalledShowsNothing) {
    CharacterSelection s;
    EXPECT_EQ(s.update("claude", Rates{{"anthropic", 50}}, {}, 0), std::nullopt);
}

TEST(CharacterSelection, LockedShowsItsCharacterRightAway) {
    CharacterSelection s("claude");
    EXPECT_EQ(s.update("gpt", Rates{{"anthropic", 90}}, kPacks, 0), "gpt");
}

TEST(CharacterSelection, ALockOnARemovedCharacterFallsBackToAuto) {
    CharacterSelection s;
    EXPECT_EQ(s.update("gemini", Rates{{"openai", 30}}, kPacks, 0), "gpt");
}

TEST(CharacterSelection, AutoFollowsTheFastestVendorWithACharacter) {
    CharacterSelection s;
    EXPECT_EQ(s.update(std::nullopt, Rates{{"google", 200}, {"openai", 40}, {"anthropic", 30}}, kPacks, 0), "gpt");
}

TEST(CharacterSelection, AChangeIsHeldForThreeSeconds) {
    CharacterSelection s;
    EXPECT_EQ(s.update(std::nullopt, Rates{{"anthropic", 60}}, kPacks, 0), "claude");
    EXPECT_EQ(s.update(std::nullopt, Rates{{"openai", 90}, {"anthropic", 60}}, kPacks, 1), "claude");
    EXPECT_EQ(s.update(std::nullopt, Rates{{"openai", 90}, {"anthropic", 60}}, kPacks, 3.9), "claude");
    EXPECT_EQ(s.update(std::nullopt, Rates{{"openai", 90}, {"anthropic", 60}}, kPacks, 4.0), "gpt");
}

TEST(CharacterSelection, ABriefLeadDoesNotSwitch) {
    CharacterSelection s;
    s.update(std::nullopt, Rates{{"anthropic", 60}}, kPacks, 0);
    s.update(std::nullopt, Rates{{"openai", 90}}, kPacks, 1);
    s.update(std::nullopt, Rates{{"anthropic", 60}}, kPacks, 2);
    EXPECT_EQ(s.update(std::nullopt, Rates{{"openai", 90}}, kPacks, 4.5), "claude");
}

TEST(CharacterSelection, QuietOrUnknownVendorsKeepTheLastCharacter) {
    CharacterSelection s;
    EXPECT_EQ(s.update(std::nullopt, Rates{}, kPacks, 0), "claude");
    s.update(std::nullopt, Rates{{"openai", 20}}, kPacks, 1);
    EXPECT_EQ(s.update(std::nullopt, Rates{{"openai", 20}}, kPacks, 5), "gpt");
    EXPECT_EQ(s.update(std::nullopt, Rates{{"xai", 300}}, kPacks, 9), "gpt");
}

TEST(CharacterFootprint, TheCharacterStandsInADoubleDisc) {
    EXPECT_EQ(footprint::scale("character"), 2);
    for (const char* style : {"led", "needle", "liquid", "reactor", "minimal"}) EXPECT_EQ(footprint::scale(style), 1);
}

TEST(CharacterFootprint, ClicksLandOnTheBodyOnly) {
    const double d = 300;
    // Screen coordinates (y down): macOS's dy 120 (up) is -120 here.
    EXPECT_TRUE(footprint::characterContains(0, 0, d));
    EXPECT_TRUE(footprint::characterContains(0, -120, d));
    EXPECT_TRUE(footprint::characterContains(0, 140, d));
    EXPECT_FALSE(footprint::characterContains(100, 0, d));
    EXPECT_FALSE(footprint::characterContains(0, -140, d));
    const auto box = footprint::characterBounds(d);
    EXPECT_NEAR(box.width, 126, 0.001);
    EXPECT_NEAR(box.height, 270, 0.001);
    EXPECT_NEAR(box.y + box.height / 2, 0.04 * d, 1e-9);
}

TEST(CharacterReadouts, DefaultOnExceptTheModel) {
    const CharacterReadouts r;
    EXPECT_TRUE(r.isDefault());
    for (auto readout : kCharacterReadouts) EXPECT_EQ(r.get(readout), readout != CharacterReadout::Model);
    CharacterReadouts changed;
    changed.set(CharacterReadout::Model, true);
    changed.set(CharacterReadout::Cost, false);
    EXPECT_TRUE(changed.model && !changed.cost && !changed.isDefault());
}

TEST(CharacterReadouts, CharacterIDsAreCleaned) {
    EXPECT_EQ(cleanCharacterID(" claude "), "claude");
    EXPECT_EQ(cleanCharacterID("../x"), std::nullopt);
    EXPECT_EQ(cleanCharacterID("Not Valid"), std::nullopt);
    EXPECT_EQ(cleanCharacterID("-a"), std::nullopt);
    EXPECT_EQ(cleanCharacterID(std::string(65, 'a')), std::nullopt);
    EXPECT_TRUE(isValidCharacterID("gpt-5"));
}
