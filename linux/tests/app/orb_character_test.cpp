#include <gtest/gtest.h>

#include <QJsonDocument>
#include <QJsonObject>
#include <QSettings>

#include "orbs/orbs_store.hpp"

// DESIGN 2b data model rows `character` and `characterReadouts` (tolerant decode, written only when not default,
// copied by Add, Duplicate and Split), ported from macOS CharacterOrbTests.
namespace {

OrbConfig decode(const char* json) { return OrbConfig::fromJson(QJsonDocument::fromJson(QByteArray(json)).object()); }

using agentspeed::character::CharacterReadout;
using agentspeed::character::CharacterReadouts;

}  // namespace

TEST(OrbCharacter, CharacterIDsAreCleanedOnDecode) {
    const auto orb = decode(R"({"id": "a", "style": "character", "character": "claude"})");
    EXPECT_EQ(orb.style, QStringLiteral("character"));
    EXPECT_EQ(orb.character, QStringLiteral("claude"));
    EXPECT_FALSE(decode(R"({"id": "a", "character": "../x"})").character.has_value());
    EXPECT_FALSE(decode(R"({"id": "a", "character": 3})").character.has_value());
}

TEST(OrbCharacter, AutoIsOmittedWhenEncoding) {
    OrbConfig orb;
    orb.id = QStringLiteral("a");
    EXPECT_FALSE(orb.toJson().contains(QStringLiteral("character")));
    orb.style = QStringLiteral("character");
    orb.character = QStringLiteral("gpt");
    EXPECT_EQ(OrbConfig::fromJson(orb.toJson()).character, QStringLiteral("gpt"));
    // Clearing a lock read from disk drops the key instead of keeping the old value.
    auto again = OrbConfig::fromJson(orb.toJson());
    again.character.reset();
    EXPECT_FALSE(again.toJson().contains(QStringLiteral("character")));
}

TEST(OrbCharacter, ReadoutsDefaultOnExceptTheModel) {
    OrbConfig orb;
    orb.id = QStringLiteral("a");
    orb.style = QStringLiteral("character");
    EXPECT_TRUE(orb.characterReadouts.isDefault());
    for (auto r : agentspeed::character::kCharacterReadouts) EXPECT_EQ(orb.characterReadouts.get(r), r != CharacterReadout::Model);
    EXPECT_TRUE(decode(R"({"id": "a", "style": "character"})").characterReadouts.isDefault());
    EXPECT_FALSE(orb.toJson().contains(QStringLiteral("characterReadouts")));
}

TEST(OrbCharacter, ReadoutsDecodeTolerantlyAndRoundTrip) {
    const auto orb = decode(R"({"id": "a", "characterReadouts": {"model": true, "network": false, "cost": "yes"}})");
    CharacterReadouts expected;
    expected.model = true;
    expected.network = false;
    EXPECT_EQ(orb.characterReadouts, expected);
    EXPECT_EQ(OrbConfig::fromJson(orb.toJson()).characterReadouts, orb.characterReadouts);
    EXPECT_TRUE(decode(R"({"id": "a", "characterReadouts": 3})").characterReadouts.isDefault());
}

TEST(OrbCharacter, AddDuplicateAndSplitCopyTheCharacterAndReadouts) {
    OrbConfig a;
    a.id = QStringLiteral("a");
    a.style = QStringLiteral("character");
    a.character = QStringLiteral("claude");
    a.characterReadouts.set(CharacterReadout::Model, true);
    a.characterReadouts.set(CharacterReadout::Cost, false);
    const auto child = OrbConfig::childOf(a, QStringLiteral("b"));
    EXPECT_EQ(child.id, QStringLiteral("b"));
    EXPECT_EQ(child.style, QStringLiteral("character"));
    EXPECT_EQ(child.character, a.character);
    EXPECT_EQ(child.characterReadouts, a.characterReadouts);
}

TEST(OrbCharacter, TheStoreKeepsTheCharacterStyle) {
    QSettings().remove(QStringLiteral("orbs"));
    QSettings().setValue(QStringLiteral("orbs"),
                         QStringLiteral(R"({"version":1,"orbs":[{"id":"c","style":"character","size":148,"character":"kimi"}]})"));
    OrbsStore store;
    ASSERT_EQ(store.orbs().size(), 1u);
    EXPECT_EQ(store.orbs()[0].style, QStringLiteral("character"));
    EXPECT_EQ(store.orbs()[0].character, QStringLiteral("kimi"));
    EXPECT_DOUBLE_EQ(store.orbs()[0].footprintDiameter(), 296);
    OrbConfig led = store.orbs()[0];
    led.style = QStringLiteral("led");
    EXPECT_DOUBLE_EQ(led.footprintDiameter(), 148);
    QSettings().remove(QStringLiteral("orbs"));
}
