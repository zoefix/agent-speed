#include <gtest/gtest.h>

#include <QDateTime>
#include <QVariantList>

#include "orb/character/character_quota.hpp"
#include "quota/quota_view.hpp"
#include "strings.hpp"

// The 額度 card's content of a `character` orb (DESIGN 2a.6, 額度): its sources and lines from the quota view's items,
// a remote host's source (DESIGN 13 Host quotas) and what accessibility reads.
namespace {

using orb::CharacterQuota;

QVariantMap window(const char* label, int percent, double fraction, const char* shortReset, const char* spoken, bool passed = false) {
    return {{QStringLiteral("label"), QString::fromUtf8(label)},       {QStringLiteral("percent"), percent},
            {QStringLiteral("percentText"), QString::number(percent) + QLatin1Char('%')},
            {QStringLiteral("fraction"), fraction},                    {QStringLiteral("short"), QString::fromUtf8(shortReset)},
            {QStringLiteral("spoken"), QString::fromUtf8(spoken)},      {QStringLiteral("passed"), passed}};
}

QVariantMap item(const char* key, const char* source, const QString& name, QVariantList lines) {
    return {{QStringLiteral("key"), QString::fromUtf8(key)},
            {QStringLiteral("source"), QString::fromUtf8(source)},
            {QStringLiteral("name"), name},
            {QStringLiteral("lines"), lines}};
}

Strings& english() {
    static Strings strings;
    strings.setChoice(QStringLiteral("en"));
    return strings;
}

}  // namespace

TEST(CharacterQuota, ItemsBecomeTheCardsSourcesWithOnlyTheirWindows) {
    auto& strings = english();
    const QVariantList items = {
        item("codex", "codex", QStringLiteral("Codex"), {window("7d", 23, 0.234, "Sun", "7d 23% used, resets Sun 09:00")}),
        item("claudeCode", "claudeCode", QStringLiteral("Claude Code"),
             {window("5h", 0, 0, "", "5h 0% used", true), window("7d", 103, 1.03, "19h", "7d 100% used, resets in 19 h")}),
    };
    const auto quota = CharacterQuota::of(items, strings);
    ASSERT_EQ(quota.sources->size(), 2u);
    const auto& codex = (*quota.sources)[0];
    EXPECT_EQ(codex.key, "codex");
    EXPECT_EQ(codex.name, "Codex");
    EXPECT_TRUE(codex.host.empty());
    EXPECT_EQ(codex.color, 0x34D399u) << "the source's color (DESIGN 1)";
    ASSERT_EQ(codex.lines.size(), 1u) << "a weekly window alone is one line, no placeholder";
    EXPECT_EQ(codex.lines[0].label, "7d");
    EXPECT_EQ(codex.lines[0].percent, 23);
    EXPECT_NEAR(codex.lines[0].bar, 0.234, 1e-9);
    EXPECT_EQ(codex.lines[0].reset, "Sun");
    const auto& claude = (*quota.sources)[1];
    EXPECT_EQ(claude.color, 0xFF8A65u);
    ASSERT_EQ(claude.lines.size(), 2u);
    EXPECT_TRUE(claude.lines[0].passed);
    EXPECT_TRUE(claude.lines[0].reset.empty());
    // The bar and the percent never leave 0…100 %.
    EXPECT_EQ(claude.lines[1].percent, 100);
    EXPECT_EQ(claude.lines[1].bar, 1);
    EXPECT_FALSE(quota.empty());
    EXPECT_TRUE(CharacterQuota::of({}, strings).empty());
    EXPECT_TRUE(CharacterQuota::of({}, strings).spoken.isEmpty());
}

// DESIGN 13: a host's source reads `<source> · <host>`; the card keeps the host apart so it can follow the name or
// take its own line, and accessibility reads the whole header.
TEST(CharacterQuota, AHostsSourceKeepsItsHostApart) {
    auto& strings = english();
    const QVariantList items = {item("codex@8C0D", "codex", QStringLiteral("Codex · devbox · 2"), {window("7d", 58, 0.58, "Sat", "7d 58% used")})};
    const auto quota = CharacterQuota::of(items, strings);
    ASSERT_EQ(quota.sources->size(), 1u);
    EXPECT_EQ((*quota.sources)[0].key, "codex@8C0D");
    EXPECT_EQ((*quota.sources)[0].name, "Codex");
    EXPECT_EQ((*quota.sources)[0].host, "devbox · 2");
    EXPECT_EQ(quota.spoken, QStringLiteral("Quota: Codex · devbox · 2 7d 58% used"));
}

// Accessibility (after the other cards): `a11y.orb.quota` with each source's name followed by its windows, every window
// and source joined by `list.separator`.
TEST(CharacterQuota, AccessibilityReadsEverySourceWithItsWindows) {
    auto& strings = english();
    const QVariantList items = {
        item("codex", "codex", QStringLiteral("Codex"), {window("7d", 25, 0.25, "Mon", "7d 25% used, resets Mon 09:00")}),
        item("claudeCode", "claudeCode", QStringLiteral("Claude Code"),
             {window("5h", 0, 0, "", "5h 0% used", true), window("7d", 86, 0.86, "", "7d 86% used")}),
    };
    EXPECT_EQ(CharacterQuota::of(items, strings).spoken,
              QStringLiteral("Quota: Codex 7d 25% used, resets Mon 09:00, Claude Code 5h 0% used, 7d 86% used"));
    Strings chinese;
    chinese.setChoice(QStringLiteral("zh-Hant"));
    EXPECT_EQ(CharacterQuota::of({items[0]}, chinese).spoken, QStringLiteral("額度：Codex 7d 25% used, resets Mon 09:00"));
}

// The quota view's own items (its demo): this computer's sources in source order, then the demo host's, read whole.
TEST(CharacterQuota, TheQuotaViewsItemsReadWhole) {
    auto& strings = english();
    QuotaView view(strings);
    view.loadDemo(QDateTime::currentMSecsSinceEpoch());
    const auto quota = CharacterQuota::of(view.cardItems({QStringLiteral("codex"), QStringLiteral("claudeCode")}, {}, true), strings);
    std::vector<std::string> keys, hosts;
    for (const auto& s : *quota.sources) {
        keys.push_back(s.key.substr(0, s.key.find('@')));
        hosts.push_back(s.host);
        EXPECT_FALSE(s.lines.empty());
        for (const auto& l : s.lines) EXPECT_TRUE(l.bar >= 0 && l.bar <= 1);
    }
    ASSERT_EQ(keys.size(), 4u);
    EXPECT_EQ(keys, (std::vector<std::string>{"codex", "claudeCode", "codex", "claudeCode"}));
    EXPECT_EQ(hosts, (std::vector<std::string>{"", "", "devbox", "devbox"}));
    EXPECT_EQ((*quota.sources)[2].name, "Codex");
    EXPECT_TRUE(quota.spoken.startsWith(QStringLiteral("Quota: Codex 7d 23% used")));
    EXPECT_TRUE(quota.spoken.contains(QStringLiteral("Claude Code · devbox 5h 67% used")));
    // This computer alone.
    const auto local = CharacterQuota::of(view.cardItems({QStringLiteral("codex")}, {QStringLiteral("local")}, false), strings);
    ASSERT_EQ(local.sources->size(), 1u);
    EXPECT_EQ((*local.sources)[0].lines.size(), 1u);
}
