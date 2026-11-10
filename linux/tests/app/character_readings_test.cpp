#include <gtest/gtest.h>

#include <QVariantList>

#include "format.hpp"
#include "orb/character/character_readings.hpp"
#include "strings.hpp"

// A `character` orb's 首字 row and models card (DESIGN 2a.6, Readouts): the row's units in every language, the card's
// rows and its quiet rule, and what accessibility reads.
namespace {

using orb::CharacterReadings;

QVariantMap modelRow(const char* model, qint64 speed, const char* vendor = "") {
    return {{QStringLiteral("model"), QString::fromUtf8(model)}, {QStringLiteral("speed"), speed}, {QStringLiteral("vendor"), QString::fromUtf8(vendor)}};
}

QVariantMap setWith(QVariant latency, QVariantList models) {
    QVariantMap set;
    if (latency.isValid()) set.insert(QStringLiteral("firstLatencyMs"), latency);
    set.insert(QStringLiteral("models"), models);
    return set;
}

void stepFor(CharacterReadings& r, double& t, double seconds) {
    for (int n = 0; n < static_cast<int>(seconds * 60 + 0.5); ++n) {
        t += 1.0 / 60;
        r.step(t, 1.0 / 60);
    }
}

}  // namespace

TEST(CharacterLatency, RoundsOnceAndTheUnitFollowsTheRoundedValue) {
    const auto at = [](double ms) {
        const auto [number, unit] = format::firstTokenLatency(ms);
        return number + QLatin1Char(' ') + unit;
    };
    EXPECT_EQ(at(0), QStringLiteral("0 perf.unit.ms"));
    EXPECT_EQ(at(850), QStringLiteral("850 perf.unit.ms"));
    EXPECT_EQ(at(845), QStringLiteral("850 perf.unit.ms"));
    EXPECT_EQ(at(844.9), QStringLiteral("840 perf.unit.ms"));
    EXPECT_EQ(at(994.9), QStringLiteral("990 perf.unit.ms"));
    EXPECT_EQ(at(995), QStringLiteral("1.0 perf.unit.s"));
    EXPECT_EQ(at(1050), QStringLiteral("1.1 perf.unit.s"));
    EXPECT_EQ(at(1234), QStringLiteral("1.2 perf.unit.s"));
    EXPECT_EQ(at(9949), QStringLiteral("9.9 perf.unit.s"));
    EXPECT_EQ(at(9950), QStringLiteral("10 perf.unit.s"));
    EXPECT_EQ(at(14'000), QStringLiteral("14 perf.unit.s"));
    EXPECT_EQ(at(14'500), QStringLiteral("15 perf.unit.s"));
}

TEST(CharacterLatency, TheRowAndItsWordsInEveryLanguage) {
    Strings strings;
    struct Want {
        const char* language;
        const char* glyph;
        const char* ms;
        const char* s;
        const char* spokenMs;
        const char* spokenS;
    };
    const Want wants[] = {
        {"zh-Hant", "首字", "毫秒", "秒", "首字延遲 850 毫秒", "首字延遲 1.2 秒"},
        {"zh-Hans", "首字", "毫秒", "秒", "首字延迟 850 毫秒", "首字延迟 1.2 秒"},
        {"en", "TTFT", "ms", "s", "first-token latency 850 ms", "first-token latency 1.2 s"},
        {"ja", "初回", "ミリ秒", "秒", "初回出力まで 850 ミリ秒", "初回出力まで 1.2 秒"},
    };
    for (const auto& w : wants) {
        SCOPED_TRACE(w.language);
        strings.setChoice(QString::fromLatin1(w.language));
        const auto ms = CharacterReadings::latencyOf(850.0, strings);
        ASSERT_TRUE(ms.has_value());
        EXPECT_EQ(ms->glyph, QString::fromUtf8(w.glyph));
        EXPECT_EQ(ms->number, QStringLiteral("850"));
        EXPECT_EQ(ms->unit, QString::fromUtf8(w.ms));
        EXPECT_EQ(ms->spoken, QString::fromUtf8(w.spokenMs));
        const auto s = CharacterReadings::latencyOf(1234.0, strings);
        EXPECT_EQ(s->number, QStringLiteral("1.2"));
        EXPECT_EQ(s->unit, QString::fromUtf8(w.s));
        EXPECT_EQ(s->spoken, QString::fromUtf8(w.spokenS));
        EXPECT_EQ(CharacterReadings::latencyOf(14'200.0, strings)->number, QStringLiteral("14"));
    }
    EXPECT_FALSE(CharacterReadings::latencyOf(QVariant(), strings).has_value());
    EXPECT_FALSE(CharacterReadings::latencyOf(QStringLiteral("x"), strings).has_value());
}

// Shown while the value exists, fading in and out over 0.25 s; it keeps its last value while it fades.
TEST(CharacterLatency, TheRowFadesInAndOutWithItsValue) {
    Strings strings;
    strings.setChoice(QStringLiteral("en"));
    CharacterReadings r;
    double t = 0;
    r.apply(setWith(1500.0, {}), t, strings);
    EXPECT_TRUE(r.busy(t));
    stepFor(r, t, 0.15);
    EXPECT_NEAR(r.latencyOpacity(), 0.6, 1e-6);
    stepFor(r, t, 0.2);
    EXPECT_EQ(r.latencyOpacity(), 1);
    EXPECT_FALSE(r.busy(t));
    r.apply(setWith(QVariant(), {}), t, strings);
    stepFor(r, t, 0.1);
    ASSERT_TRUE(r.latency().has_value());
    EXPECT_EQ(r.latency()->number, QStringLiteral("1.5"));
    stepFor(r, t, 0.2);
    EXPECT_EQ(r.latencyOpacity(), 0);
    EXPECT_FALSE(r.latency().has_value());
}

TEST(CharacterModels, RowsComeFromTheSetInItsOrder) {
    const auto rows = CharacterReadings::modelsOf({modelRow("gpt-6.1-sol", 106, "openai"), modelRow("claude-opus-5-5", 100, "anthropic"),
                                                   modelRow("", 50), modelRow("gemini-4-argon", -1, "google")});
    ASSERT_EQ(rows.size(), 3u);
    EXPECT_EQ(rows[0], (CharacterReadings::Model{QStringLiteral("gpt-6.1-sol"), 106, QStringLiteral("openai")}));
    EXPECT_EQ(rows[1].model, QStringLiteral("claude-opus-5-5"));
    EXPECT_EQ(rows[2].speed, -1);
    EXPECT_EQ(rows[2].vendor, QStringLiteral("google"));
}

// Loud while a model runs; the card keeps its last rows for the 10 s after, then fades out and lets them go.
TEST(CharacterModels, TheCardKeepsItsLastRowsForTenSeconds) {
    Strings strings;
    CharacterReadings r;
    double t = 0;
    r.apply(setWith(QVariant(), {modelRow("claude-opus-5-5", 120), modelRow("gpt-6.1-sol", 80)}), t, strings);
    stepFor(r, t, 0.5);
    EXPECT_EQ(r.modelsOpacity(), 1);
    r.apply(setWith(QVariant(), {modelRow("claude-opus-5-5", 118)}), t, strings);
    EXPECT_EQ(r.models().size(), 1u);
    r.apply(setWith(QVariant(), {}), t, strings);
    EXPECT_NEAR(r.nextWake(t), t + 10, 1e-9);
    stepFor(r, t, 9.5);
    EXPECT_EQ(r.modelsOpacity(), 1);
    ASSERT_EQ(r.models().size(), 1u);
    EXPECT_EQ(r.models()[0].speed, 118);
    stepFor(r, t, 0.8);
    EXPECT_LT(r.modelsOpacity(), 1);
    EXPECT_EQ(r.models().size(), 1u);
    stepFor(r, t, 0.7);
    EXPECT_EQ(r.modelsOpacity(), 0);
    EXPECT_EQ(r.models().size(), 1u);  // while the card itself fades out
    EXPECT_EQ(r.nextWake(t), -1);
    stepFor(r, t, 0.5);
    EXPECT_TRUE(r.models().empty());
}

// Every running model, drawn or not, the full id, joined by list.separator; the model alone without a speed.
TEST(CharacterModels, AccessibilityReadsEveryRunningModel) {
    Strings strings;
    CharacterReadings r;
    QVariantList models;
    for (const char* id : {"claude-sonnet-4-5-20250929", "gpt-6.1-sol", "gemini-4-argon", "kimi-k3", "glm-5.1"}) models.push_back(modelRow(id, 90));
    models.push_back(modelRow("deepseek-v4", -1));
    r.apply(setWith(QVariant(), models), 0, strings);
    const char* items = "claude-sonnet-4-5-20250929 %1 90 %2、gpt-6.1-sol %1 90 %2、gemini-4-argon %1 90 %2、kimi-k3 %1 90 %2、glm-5.1 %1 90 %2、deepseek-v4";
    const auto local = [&](const char* perSecond, const char* tokens) { return QString::fromUtf8(items).arg(QString::fromUtf8(perSecond), QString::fromUtf8(tokens)); };
    strings.setChoice(QStringLiteral("zh-Hant"));
    EXPECT_EQ(r.spokenModels(strings), QStringLiteral("執行中的模型：") + local("每秒", "個 token"));
    strings.setChoice(QStringLiteral("zh-Hans"));
    EXPECT_EQ(r.spokenModels(strings), QStringLiteral("运行中的模型：") + local("每秒", "个 token"));
    strings.setChoice(QStringLiteral("ja"));
    EXPECT_EQ(r.spokenModels(strings), QStringLiteral("実行中のモデル：") + local("毎秒", "トークン"));
    strings.setChoice(QStringLiteral("en"));
    EXPECT_EQ(r.spokenModels(strings),
              QStringLiteral("running models: claude-sonnet-4-5-20250929 at 90 tokens per second, gpt-6.1-sol at 90 tokens per second, "
                             "gemini-4-argon at 90 tokens per second, kimi-k3 at 90 tokens per second, glm-5.1 at 90 tokens per second, "
                             "deepseek-v4"));
    EXPECT_TRUE(CharacterReadings().spokenModels(strings).isEmpty());
}

TEST(CharacterReadouts, TheNewStringsExistInEveryLanguage) {
    Strings strings;
    for (const char* language : {"zh-Hant", "zh-Hans", "en", "ja"}) {
        strings.setChoice(QString::fromLatin1(language));
        for (const char* key : {"character.readouts.latency", "character.readouts.models", "character.latency.glyph", "character.models.more",
                                "a11y.orb.latency", "a11y.orb.models", "a11y.orb.modelSpeed"}) {
            EXPECT_NE(strings.t(QString::fromLatin1(key)), QString::fromLatin1(key)) << key << " " << language;
        }
    }
    strings.setChoice(QStringLiteral("zh-Hant"));
    EXPECT_EQ(strings.fmt(QStringLiteral("character.models.more"), {3}), QStringLiteral("+3 個模型"));
}
