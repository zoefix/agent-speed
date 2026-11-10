#include <gtest/gtest.h>

#include "agentspeed/quota/format.hpp"
#include "agentspeed/quota/window.hpp"
#include "agentspeed/tz.hpp"
#include "quota_test_support.hpp"

// QUOTAS 2 and 7 through spec/fixtures/quota-cases.json: labels, order, percents, shown, pace, reset and age texts.
using namespace agentspeed;
using namespace agentspeed::quota;
using quota_test::section;

namespace {

constexpr const char* kLanguages[] = {"zh-Hant", "zh-Hans", "en", "ja"};

const fixtures::JsonFile& strings() {
    static const fixtures::JsonFile file(fixtures::specDir() / "strings.json");
    return file;
}

QuotaFormat formatFor(const std::string& language) {
    const auto lookup = [language](std::string_view key) { return *strings().root()[key][language].string(); };
    const auto zone = TimeZone::load(*quota_test::cases().root()["timeZone"].string());
    EXPECT_TRUE(zone) << "Asia/Taipei is not installed";
    return QuotaFormat(QuotaStrings::from(lookup), language, [zone](std::int64_t ms) { return zone ? zone->offsetMs(ms) : 0; });
}

}  // namespace

TEST(QuotaFixtures, Labels) {
    for (const auto& item : section("labels").elements()) {
        const auto w = quota_test::windowOf(item.at(0));
        for (const auto* language : kLanguages) {
            SCOPED_TRACE(std::string(language) + " " + fixtures::compactJson(item.at(0)));
            EXPECT_EQ(formatFor(language).label(w), *item.at(1)[language].string());
        }
        EXPECT_EQ(englishLabel(w), *item.at(1)["en"].string());
    }
}

TEST(QuotaFixtures, Order) {
    const auto order = section("order");
    std::vector<std::string> labels;
    for (const auto& w : ordered(quota_test::windowsOf(order["windows"]))) labels.push_back(englishLabel(w));
    std::vector<std::string> expected;
    for (const auto& item : order["expected"].elements()) expected.push_back(*item.string());
    EXPECT_EQ(labels, expected);
}

TEST(QuotaFixtures, Percents) {
    for (const auto& item : section("percents").elements()) {
        EXPECT_EQ(displayPercent(*item.at(0).number()), *item.at(1).truncated()) << item.at(0).raw();
    }
}

TEST(QuotaFixtures, Shown) {
    const auto fixture = section("shown");
    quota::Snapshot snapshot;
    snapshot.observedMs = *fixture["snapshot"]["observedMs"].truncated();
    snapshot.windows = quota_test::windowsOf(fixture["snapshot"]["windows"]);
    const auto got = shown(snapshot, quota_test::fixtureNow());
    const auto expected = fixture["expected"].elements();
    ASSERT_EQ(got.size(), expected.size());
    for (std::size_t i = 0; i < got.size(); ++i) {
        SCOPED_TRACE(i);
        EXPECT_EQ(englishLabel(got[i].window), *expected[i]["label"].string());
        EXPECT_EQ(got[i].percent, *expected[i]["percent"].truncated());
        EXPECT_DOUBLE_EQ(got[i].bar, *expected[i]["bar"].number());
        EXPECT_EQ(got[i].resetsAtMs, fixtures::optInt(expected[i]["resetsAtMs"]));
        EXPECT_EQ(got[i].passed, expected[i]["passed"].isTrue());
    }
}

TEST(QuotaFixtures, Pace) {
    for (const auto& item : section("pace").elements()) {
        SCOPED_TRACE(fixtures::compactJson(item["window"]));
        const auto got = pace(quota_test::windowOf(item["window"]), quota_test::fixtureNow());
        if (item["expected"].isNull()) {
            EXPECT_FALSE(got.has_value());
        } else {
            ASSERT_TRUE(got.has_value());
            EXPECT_NEAR(*got, *item["expected"].number(), 1e-12);
        }
    }
}

TEST(QuotaFixtures, ResetTexts) {
    const auto now = quota_test::fixtureNow();
    for (const auto* language : kLanguages) {
        const auto format = formatFor(language);
        for (const auto& item : section("resetTexts").elements()) {
            const auto resets = *item["resetsAtMs"].truncated();
            SCOPED_TRACE(std::string(language) + " " + std::to_string(resets - now));
            EXPECT_EQ(format.resetLong(resets, now), *item["long"][language].string());
            EXPECT_EQ(format.resetShort(resets, now), *item["short"][language].string());
        }
    }
}

TEST(QuotaFixtures, AgeTexts) {
    const auto now = quota_test::fixtureNow();
    for (const auto* language : kLanguages) {
        const auto format = formatFor(language);
        for (const auto& item : section("ageTexts").elements()) {
            SCOPED_TRACE(std::string(language) + " " + std::string(item["observedMs"].raw()));
            EXPECT_EQ(format.age(*item["observedMs"].truncated(), now), fixtures::optString(item["expected"][language]));
        }
    }
}

TEST(QuotaFormat, TemplatesAndPace) {
    EXPECT_EQ(fill("%d h %d min", {"3", "12"}), "3 h 12 min");
    EXPECT_EQ(fill("%d%% of %@", {"60", "it"}), "60% of it");
    EXPECT_EQ(fill("100%", {}), "100%");
    EXPECT_EQ(formatFor("zh-Hant").paceText(0.6), "時間進度 60%");
    EXPECT_EQ(formatFor("en").paceText(0.004), "0% of the window has passed");
    EXPECT_EQ(formatFor("ja").duration(QuotaFormat::kHourMs * 2), "2時間");
    EXPECT_EQ(weekdayNames("fr")[4], "Fri");
}
