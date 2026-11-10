#include <gtest/gtest.h>

#include <QFile>
#include <QJsonDocument>
#include <QJsonObject>
#include <QSettings>
#include <QVariantMap>

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

// DESIGN 2b `characterReadouts`: `latency` and `models` default true, also for settings written before they existed;
// written only when they differ from the defaults.
TEST(OrbCharacter, LatencyAndModelsDefaultOnAndRoundTrip) {
    const auto older = decode(R"({"id": "a", "style": "character", "characterReadouts": {"model": true, "network": false}})");
    EXPECT_TRUE(older.characterReadouts.latency);
    EXPECT_TRUE(older.characterReadouts.models);
    EXPECT_FALSE(older.characterReadouts.network);
    auto off = older;
    off.characterReadouts.latency = false;
    off.characterReadouts.models = false;
    const auto json = off.toJson().value(QStringLiteral("characterReadouts")).toObject();
    EXPECT_EQ(json.value(QStringLiteral("latency")), QJsonValue(false));
    EXPECT_EQ(json.value(QStringLiteral("models")), QJsonValue(false));
    EXPECT_EQ(OrbConfig::fromJson(off.toJson()).characterReadouts, off.characterReadouts);
    const auto odd = decode(R"({"id": "a", "characterReadouts": {"latency": 0, "models": "no"}})");
    EXPECT_TRUE(odd.characterReadouts.latency && odd.characterReadouts.models);
    EXPECT_TRUE(odd.characterReadouts.isDefault());
}

// DESIGN 2b `characterReadouts`: `quota` defaults true, also for settings written before it existed; written only when
// it differs from the default, and a value that is not a boolean takes the default.
TEST(OrbCharacter, QuotaDefaultsOnAndRoundTrips) {
    const auto older = decode(R"({"id": "a", "style": "character", "characterReadouts": {"models": false}})");
    EXPECT_TRUE(older.characterReadouts.quota);
    auto off = older;
    off.characterReadouts.quota = false;
    const auto json = off.toJson().value(QStringLiteral("characterReadouts")).toObject();
    EXPECT_EQ(json.value(QStringLiteral("quota")), QJsonValue(false));
    EXPECT_EQ(OrbConfig::fromJson(off.toJson()).characterReadouts, off.characterReadouts);
    EXPECT_TRUE(decode(R"({"id": "a", "characterReadouts": {"quota": "off"}})").characterReadouts.quota);
    auto plain = decode(R"({"id": "a", "style": "character"})");
    EXPECT_FALSE(plain.toJson().contains(QStringLiteral("characterReadouts")));
    plain.characterReadouts.quota = false;
    EXPECT_TRUE(plain.toJson().contains(QStringLiteral("characterReadouts")));
}

// The Settings orb editor lists the checks in the menus' order (the context menu and the tray build theirs from the
// same list).
TEST(OrbCharacter, TheEditorListsTheReadoutChecksInTheMenusOrder) {
    QStringList keys, titles;
    for (const auto& item : OrbConfig::readoutItems()) {
        keys << item.toMap().value(QStringLiteral("key")).toString();
        titles << item.toMap().value(QStringLiteral("title")).toString();
    }
    EXPECT_EQ(keys, (QStringList{"intelligence", "thinking", "latency", "network", "cost", "agents", "models", "quota", "model"}));
    EXPECT_EQ(titles, (QStringList{"character.readouts.intel", "character.readouts.thinking", "character.readouts.latency",
                                   "character.readouts.network", "character.readouts.cost", "character.readouts.agents",
                                   "character.readouts.models", "character.readouts.quota", "character.readouts.model"}));
    QFile qml(QStringLiteral(AGENTSPEED_APP_DIR "/qml/characters/CharacterReadoutsGroup.qml"));
    ASSERT_TRUE(qml.open(QIODevice::ReadOnly));
    EXPECT_TRUE(QString::fromUtf8(qml.readAll()).contains(QStringLiteral("model: orbs.characterReadoutItems()")));
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
