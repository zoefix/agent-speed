// The character orb's models card (DESIGN 2a.6, Models): display names, cutting at the end, its lines' motion and the
// vendor's dot.
#include <gtest/gtest.h>

#include "agentspeed/character/hud_models.hpp"
#include "agentspeed/character/store.hpp"

using namespace agentspeed::character;

namespace {

// One unit per code point, so widths read as characters.
double perCodePoint(std::string_view s) {
    double n = 0;
    for (unsigned char c : s) {
        if ((c & 0xC0) != 0x80) n += 1;
    }
    return n;
}

ModelRow row(const char* model, std::optional<std::int64_t> speed = std::nullopt) { return {model, speed, std::nullopt}; }

std::vector<ModelRow> rows(int n) {
    static const char* kIds[] = {"m0", "m1", "m2", "m3", "m4", "m5", "m6", "m7"};
    std::vector<ModelRow> out;
    for (int i = 0; i < n; ++i) out.push_back(row(kIds[i], 100 - i));
    return out;
}

const ModelsMotion::Line* lineOf(const ModelsMotion& motion, const std::string& model) {
    for (const auto& l : motion.lines()) {
        if (l.row && l.row->model == model) return &l;
    }
    return nullptr;
}

const ModelsMotion::Line* moreLine(const ModelsMotion& motion) {
    for (const auto& l : motion.lines()) {
        if (!l.row && !l.leaving()) return &l;
    }
    return nullptr;
}

}  // namespace

TEST(ModelNames, ATrailingDateIsDropped) {
    EXPECT_EQ(modelDisplayName("claude-sonnet-4-5-20250929"), "claude-sonnet-4-5");
    EXPECT_EQ(modelDisplayName("gpt-4o-2024-08-06"), "gpt-4o");
    EXPECT_EQ(modelDisplayName("grok-5-fast-reasoning-2026-09-01"), "grok-5-fast-reasoning");
    for (const char* kept : {"claude-opus-5-5", "gemini-2.5-pro-preview-05-06", "model-12345678", "o3-20251301", "x-20250229-preview",
                             "20250929", "-20250929", "gpt-4-0613", "qwen3-coder-2025-13-01"}) {
        EXPECT_EQ(modelDisplayName(kept), kept);
    }
}

TEST(ModelNames, ALongNameIsCutAtItsEnd) {
    EXPECT_EQ(cutAtEnd("claude-sonnet-4-5", 17, perCodePoint), "claude-sonnet-4-5");
    EXPECT_EQ(cutAtEnd("claude-sonnet-4-5", 10, perCodePoint), "claude-so…");
    // A cut that ends on a separator drops it: `claude…`, not `claude-…`.
    EXPECT_EQ(cutAtEnd("claude-sonnet-4-5", 8, perCodePoint), "claude…");
    EXPECT_EQ(cutAtEnd("模型名稱很長的模型", 4, perCodePoint), "模型名…");
    EXPECT_EQ(cutAtEnd("gpt-6.1-sol", 0.5, perCodePoint), "…");
    EXPECT_EQ(cutAtEnd("", 0, perCodePoint), "");
    for (double w = 1; w <= 17; w += 0.5) EXPECT_LE(perCodePoint(cutAtEnd("claude-sonnet-4-5", w, perCodePoint)), w) << w;
}

TEST(ModelsMotion, AtMostFourRowsThenTheMoreLineCountingTheRest) {
    ModelsMotion motion;
    motion.sync(rows(7), kModelRows, 0, false);
    EXPECT_EQ(motion.shown(), 4);
    EXPECT_EQ(motion.more(), 3);
    EXPECT_EQ(motion.count(), 5);
    ASSERT_NE(moreLine(motion), nullptr);
    EXPECT_EQ(moreLine(motion)->more, 3);
    EXPECT_EQ(moreLine(motion)->to, 4);
    EXPECT_EQ(lineOf(motion, "m4"), nullptr);
    for (int i = 0; i < 4; ++i) EXPECT_EQ(lineOf(motion, "m" + std::to_string(i))->to, i);
    // Below S = 96 the card keeps its first two rows; the "+N" line counts every model not drawn.
    motion.sync(rows(7), kSmallModelRows, 1, false);
    EXPECT_EQ(motion.count(), 3);
    EXPECT_EQ(moreLine(motion)->more, 5);
    EXPECT_EQ(moreLine(motion)->to, 2);
    // Four models fit without the line.
    ModelsMotion four;
    four.sync(rows(4), kModelRows, 0, false);
    EXPECT_EQ(four.count(), 4);
    EXPECT_EQ(moreLine(four), nullptr);
}

TEST(ModelsMotion, LinesArrivingWithTheCardShowAtOnceAndLaterOnesFadeIn) {
    ModelsMotion motion;
    motion.sync({row("a", 50)}, kModelRows, 10, false);
    EXPECT_EQ(lineOf(motion, "a")->alpha(10), 1);
    motion.sync({row("a", 50), row("b")}, kModelRows, 11, false);
    EXPECT_EQ(lineOf(motion, "b")->alpha(11), 0);
    EXPECT_NEAR(lineOf(motion, "b")->alpha(11 + 0.125), 0.5, 1e-9);
    EXPECT_EQ(lineOf(motion, "b")->alpha(11.25), 1);
    EXPECT_TRUE(motion.moving(11.1));
    EXPECT_FALSE(motion.moving(11.3));
}

TEST(ModelsMotion, RowsGlideToTheirNewPlaces) {
    ModelsMotion motion;
    motion.sync({row("a", 90), row("b", 50)}, kModelRows, 0, false);
    motion.sync({row("b", 120), row("a", 90)}, kModelRows, 1, false);
    const auto* b = lineOf(motion, "b");
    EXPECT_EQ(b->position(1), 1);
    EXPECT_NEAR(b->position(1.15), 0.5, 1e-9);
    EXPECT_EQ(b->position(1.3), 0);
    EXPECT_EQ(lineOf(motion, "a")->position(1.3), 1);
    EXPECT_EQ(b->row->speed, 120);
    // Still (Reduce Motion, effects Off): at once.
    ModelsMotion still;
    still.sync({row("a", 90), row("b", 50)}, kModelRows, 0, true);
    still.sync({row("b", 120), row("a", 90)}, kModelRows, 1, true);
    EXPECT_EQ(lineOf(still, "b")->position(1), 0);
}

TEST(ModelsMotion, AModelThatStopsFadesOutOfItsRowAndCanComeBack) {
    ModelsMotion motion;
    motion.sync({row("a", 90), row("b", 50), row("c", 20)}, kModelRows, 0, false);
    motion.sync({row("a", 90), row("c", 20)}, kModelRows, 2, false);
    const auto* b = lineOf(motion, "b");
    ASSERT_NE(b, nullptr);
    EXPECT_TRUE(b->leaving());
    EXPECT_EQ(b->to, 1);  // it fades where it was
    EXPECT_NEAR(b->alpha(2.1), 0.6, 1e-9);
    EXPECT_EQ(motion.count(), 2);
    EXPECT_EQ(lineOf(motion, "c")->to, 1);
    // Back before it has gone: it fades in again from where it was.
    motion.sync({row("a", 90), row("b", 50), row("c", 20)}, kModelRows, 2.1, false);
    b = lineOf(motion, "b");
    EXPECT_FALSE(b->leaving());
    EXPECT_NEAR(b->alpha(2.1), 0.6, 1e-9);
    // Gone after its fade.
    motion.sync({row("a", 90)}, kModelRows, 5, false);
    motion.sync({row("a", 90)}, kModelRows, 5.3, false);
    EXPECT_EQ(lineOf(motion, "b"), nullptr);
    EXPECT_EQ(motion.lines().size(), 1u);
}

TEST(VendorAccents, AnInstalledCharacterFirstThenTheCatalog) {
    InstalledCharacter installed;
    installed.manifest.id = "claude";
    installed.manifest.vendors = {"anthropic"};
    installed.manifest.accent = "#D97757";
    InstalledCharacter broken;
    broken.manifest.vendors = {"openai"};
    broken.manifest.accent = "orange";
    CharacterCatalog catalog;
    CharacterCatalog::Entry gpt;
    gpt.vendors = {"openai"};
    gpt.accent = "#10A37F";
    CharacterCatalog::Entry claude;
    claude.vendors = {"anthropic"};
    claude.accent = "#000000";
    catalog.characters = {gpt, claude};
    EXPECT_EQ(vendors::accent("anthropic", {installed}, &catalog), "#D97757");
    EXPECT_EQ(vendors::accent("openai", {installed, broken}, &catalog), "#10A37F");
    EXPECT_EQ(vendors::accent("openai", {installed}, nullptr), std::nullopt);
    EXPECT_EQ(vendors::accent("mistral", {installed}, &catalog), std::nullopt);
    EXPECT_EQ(vendors::accent(std::nullopt, {installed}, &catalog), std::nullopt);
}
