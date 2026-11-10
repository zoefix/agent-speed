#include <gtest/gtest.h>

#include <QDateTime>

#include "quota/quota_text.hpp"
#include "quota/quota_view.hpp"
#include "strings.hpp"

// The quota lines as the dashboard and the orb card get them (DESIGN 4 Quota lines, 2a.6 額度), the Settings status
// lines (DESIGN 5), the listing rule, the invitation, and the hosts' lines (DESIGN 13 Host quotas).
namespace {

namespace quota = agentspeed::quota;
namespace remote = agentspeed::remote;
using agentspeed::SourceKind;

constexpr std::int64_t kHour = 3'600'000;

std::int64_t nowMs() { return QDateTime::currentMSecsSinceEpoch(); }

QuotaSourceState state(SourceKind source, std::vector<quota::Window> windows, std::int64_t observedMs, bool running = true, bool signedIn = true) {
    return {source, quota::Snapshot{source, observedMs, std::move(windows)}, running, signedIn};
}

QVariantMap lineAt(const QVariantList& lines, int i) { return lines.at(i).toMap(); }

}  // namespace

TEST(QuotaText, LinesCarryEveryColumnInQuotasTwoOrder) {
    Strings strings;
    strings.setChoice(QStringLiteral("zh-Hant"));
    const auto now = nowMs();
    const quota::Snapshot snapshot{SourceKind::ClaudeDesktop, now - 2 * kHour,
                                   {quota::makeWindow(quota::Kind::ModelWeek, 27, now + 3 * 24 * kHour, "Fable"),
                                    quota::makeWindow(quota::Kind::SevenDay, 96.4, now + 3 * kHour + 12 * 60'000),
                                    quota::makeWindow(quota::Kind::FiveHour, 81, now - 1)}};
    const auto lines = quota_text::lines(snapshot, now, strings, quota_text::format(strings));
    ASSERT_EQ(lines.size(), 3);
    // The passed 5-hour window: 0 %, no reset, no pace.
    EXPECT_EQ(lineAt(lines, 0).value("label").toString(), QStringLiteral("5h"));
    EXPECT_EQ(lineAt(lines, 0).value("percentText").toString(), QStringLiteral("0%"));
    EXPECT_TRUE(lineAt(lines, 0).value("reset").toString().isEmpty());
    EXPECT_LT(lineAt(lines, 0).value("pace").toDouble(), 0);
    EXPECT_TRUE(lineAt(lines, 0).value("passed").toBool());
    EXPECT_EQ(lineAt(lines, 0).value("level").toString(), QStringLiteral("normal"));
    // 96 %: critical, the long and short resets, the pace in the tooltip.
    const auto week = lineAt(lines, 1);
    EXPECT_EQ(week.value("label").toString(), QStringLiteral("7d"));
    EXPECT_EQ(week.value("percent").toInt(), 96);
    EXPECT_EQ(week.value("level").toString(), QStringLiteral("critical"));
    EXPECT_NEAR(week.value("fraction").toDouble(), 0.964, 1e-9);
    EXPECT_EQ(week.value("reset").toString(), QStringLiteral("3 小時 12 分後重置"));
    EXPECT_EQ(week.value("short").toString(), QStringLiteral("3h"));
    EXPECT_GT(week.value("pace").toDouble(), 0.98);
    EXPECT_TRUE(week.value("tooltip").toString().startsWith(QStringLiteral("更新於 ")));
    EXPECT_TRUE(week.value("tooltip").toString().contains(QStringLiteral("時間進度 98%")));
    EXPECT_EQ(week.value("spoken").toString(), QStringLiteral("7d 已用 96%，3 小時 12 分後重置"));
    // Older than an hour: the age.
    EXPECT_EQ(week.value("age").toString(), QStringLiteral("2 小時前更新"));
    EXPECT_EQ(lineAt(lines, 2).value("label").toString(), QStringLiteral("Fable 7d"));
    EXPECT_EQ(quota_text::level(80), QStringLiteral("warning"));
    EXPECT_EQ(quota_text::level(79), QStringLiteral("normal"));
}

TEST(QuotaText, MonthAndCreditsFollowTheLanguage) {
    Strings strings;
    const auto now = nowMs();
    const quota::Snapshot snapshot{SourceKind::Kimi, now,
                                   {quota::makeWindow(quota::Kind::Month, 13, std::nullopt, "MCP"), quota::makeWindow(quota::Kind::Credits, 5, std::nullopt)}};
    strings.setChoice(QStringLiteral("en"));
    auto lines = quota_text::lines(snapshot, now, strings, quota_text::format(strings));
    EXPECT_EQ(lineAt(lines, 0).value("label").toString(), QStringLiteral("credits"));
    EXPECT_EQ(lineAt(lines, 1).value("label").toString(), QStringLiteral("MCP mo"));
    EXPECT_EQ(lineAt(lines, 1).value("spoken").toString(), QStringLiteral("MCP mo 13% used"));
    strings.setChoice(QStringLiteral("zh-Hant"));
    lines = quota_text::lines(snapshot, now, strings, quota_text::format(strings));
    EXPECT_EQ(lineAt(lines, 0).value("label").toString(), QStringLiteral("點數"));
    EXPECT_EQ(lineAt(lines, 1).value("label").toString(), QStringLiteral("MCP 月"));
}

TEST(QuotaStatusLines, EveryRouteAndBridgeState) {
    Strings strings;
    strings.setChoice(QStringLiteral("zh-Hant"));
    const auto now = nowMs();
    const auto ok = quota_status::route(strings, QStringLiteral("ok"), {}, {}, now, now);
    EXPECT_TRUE(ok.text.startsWith(QStringLiteral("已更新 ")));
    EXPECT_TRUE(ok.ok);
    EXPECT_FALSE(ok.warning);
    EXPECT_EQ(quota_status::route(strings, QStringLiteral("denied"), {}, QStringLiteral("api.kimi.com"), 0, now).text,
              QStringLiteral("api.kimi.com 拒絕了請求，登入變更後再試"));
    EXPECT_TRUE(quota_status::route(strings, QStringLiteral("noBrowser"), QStringLiteral("Qoder"), {}, 0, now).warning);
    EXPECT_FALSE(quota_status::route(strings, QStringLiteral("notRunning"), QStringLiteral("Kimi Code CLI"), {}, 0, now).warning);
    EXPECT_TRUE(quota_status::route(strings, QStringLiteral("networkOff"), {}, {}, 0, now).warning);
    EXPECT_TRUE(quota_status::route(strings, QStringLiteral("unknown"), {}, {}, 0, now).text.isEmpty());
    EXPECT_EQ(quota_status::bridge(strings, QStringLiteral("waiting"), {}, std::nullopt, now).text,
              QStringLiteral("已接上，等待 Claude Code 下一次回應"));
    EXPECT_TRUE(quota_status::bridge(strings, QStringLiteral("replaced"), {}, std::nullopt, now).warning);
    EXPECT_EQ(quota_status::bridge(strings, QStringLiteral("unreadable"), QStringLiteral("~/.claude/settings.json"), std::nullopt, now).text,
              QStringLiteral("無法讀取 ~/.claude/settings.json（不是有效的 JSON），沒有做任何變更"));
    EXPECT_TRUE(quota_status::bridge(strings, QStringLiteral("off"), {}, std::nullopt, now).text.isEmpty());
}

TEST(QuotaViewTest, ListsSourcesObservedWithinADayAndOnlyThoseWithWindows) {
    Strings strings;
    QuotaView view(strings);
    int changed = 0;
    QObject::connect(&view, &QuotaView::changed, [&] { ++changed; });
    // Windows without a reset time: their texts and pace do not move with the clock between the two calls.
    const auto now = nowMs();
    view.setLocal({state(SourceKind::Codex, {quota::makeWindow(quota::Kind::SevenDay, 23, std::nullopt)}, now - 2 * kHour),
                   state(SourceKind::ClaudeDesktop, {quota::makeWindow(quota::Kind::SevenDay, 90, std::nullopt)}, now - 25 * kHour),
                   state(SourceKind::Kimi, {}, now)});
    EXPECT_EQ(view.listed(), QStringList{QStringLiteral("codex")});
    EXPECT_TRUE(view.lines().contains(QStringLiteral("claudeDesktop")));
    EXPECT_FALSE(view.lines().contains(QStringLiteral("kimi")));
    EXPECT_EQ(changed, 1);
    view.setLocal({state(SourceKind::Codex, {quota::makeWindow(quota::Kind::SevenDay, 23, std::nullopt)}, now - 2 * kHour),
                   state(SourceKind::ClaudeDesktop, {quota::makeWindow(quota::Kind::SevenDay, 90, std::nullopt)}, now - 25 * kHour),
                   state(SourceKind::Kimi, {}, now)});
    EXPECT_EQ(changed, 1) << "nothing visible changed";
    view.setInvite(true);
    EXPECT_TRUE(view.invite());
}

TEST(QuotaViewTest, TheCardListsWhatRunsAndIsSignedInWithinTheOrb) {
    Strings strings;
    QuotaView view(strings);
    const auto now = nowMs();
    const auto week = [&](double used) { return std::vector<quota::Window>{quota::makeWindow(quota::Kind::SevenDay, used, now + kHour)}; };
    view.setLocal({state(SourceKind::Codex, week(10), now), state(SourceKind::ClaudeCode, week(20), now, false, true),
                   state(SourceKind::ClaudeDesktop, week(30), now, true, false), state(SourceKind::Kimi, week(40), now)});
    auto keys = [](const QVariantList& items) {
        QStringList out;
        for (const auto& i : items) out.push_back(i.toMap().value(QStringLiteral("key")).toString());
        return out;
    };
    EXPECT_EQ(keys(view.cardItems({}, {}, true)), (QStringList{QStringLiteral("codex"), QStringLiteral("kimi")}));
    EXPECT_EQ(keys(view.cardItems({QStringLiteral("kimi")}, {}, true)), QStringList{QStringLiteral("kimi")});
    EXPECT_TRUE(view.cardItems({}, {QStringLiteral("8C0D6B0A-1111-4222-8333-444455556666")}, false).isEmpty());

    // A connected host's source shows under `<source> · <host>` while the host's switches allow it.
    const QString host = QStringLiteral("8C0D6B0A-1111-4222-8333-444455556666");
    remote::HostQuota q;
    q.hostId = host.toStdString();
    q.snapshot = quota::Snapshot{SourceKind::Codex, now, week(55)};
    q.running = true;
    q.signedIn = true;
    view.applyHost(q);
    view.setHosts({{host, QStringLiteral("devbox")}}, {host}, {{host, {QStringLiteral("codex")}}});
    const auto items = view.cardItems({}, {}, true);
    ASSERT_EQ(keys(items).size(), 3);
    EXPECT_EQ(items.at(2).toMap().value(QStringLiteral("name")).toString(), strings.t(QStringLiteral("source.codex")) + QStringLiteral(" · devbox"));
    EXPECT_TRUE(view.hostLines().contains(host));
    // Its switch off on the app (or Claude Code without the host's bridge): nothing from that host.
    view.setHosts({{host, QStringLiteral("devbox")}}, {host}, {{host, {}}});
    EXPECT_EQ(keys(view.cardItems({}, {}, true)).size(), 2);
    EXPECT_FALSE(view.hostLines().contains(host));
    // A disconnected host leaves the card; a removed host forgets its lines.
    view.setHosts({{host, QStringLiteral("devbox")}}, {}, {{host, {QStringLiteral("codex")}}});
    EXPECT_EQ(keys(view.cardItems({}, {}, true)).size(), 2);
    view.setHosts({}, {}, {});
    EXPECT_FALSE(view.hostBridge(host).has_value());
}

TEST(QuotaViewTest, HostBridgeLinesFollowTheRelaysState) {
    Strings strings;
    strings.setChoice(QStringLiteral("en"));
    QuotaView view(strings);
    const QString host = QStringLiteral("8C0D6B0A-1111-4222-8333-444455556666");
    view.setHosts({{host, QStringLiteral("devbox")}}, {host}, {{host, {QStringLiteral("claudeCode")}}});
    EXPECT_TRUE(view.hostBridgeLine(host).isEmpty());
    EXPECT_EQ(view.hostBridgePath(host), QStringLiteral("~/.claude/settings.json"));
    view.applyHost(remote::HostQuotaBridge{host.toStdString(), remote::HostQuotaBridge::State::Unreadable, "~/.config/claude/settings.json"});
    const auto line = view.hostBridgeLine(host);
    EXPECT_EQ(line.value(QStringLiteral("text")).toString(), QStringLiteral("Couldn't read ~/.config/claude/settings.json (not valid JSON). Nothing was changed"));
    EXPECT_TRUE(line.value(QStringLiteral("warning")).toBool());
    EXPECT_EQ(view.hostBridgePath(host), QStringLiteral("~/.config/claude/settings.json"));
}

TEST(QuotaStrings, TheLinuxConsentTextExistsInEveryLanguage) {
    Strings strings;
    for (const auto* language : {"zh-Hant", "zh-Hans", "en", "ja"}) {
        strings.setChoice(QString::fromLatin1(language));
        const auto text = strings.fmt(QStringLiteral("linux.settings.quota.cookies.confirm.message"), {QStringLiteral("Qoder"), QStringLiteral("qoder.com")});
        EXPECT_TRUE(text.contains(QStringLiteral("Qoder")) && text.contains(QStringLiteral("qoder.com"))) << language;
        EXPECT_FALSE(text.contains(QStringLiteral("macOS"))) << language;
    }
}
