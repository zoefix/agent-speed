#include "orbs/character_demo.hpp"

#include <QStringList>
#include <QVariantList>
#include <algorithm>
#include <cmath>
#include <vector>

#include "agentspeed/money.hpp"
#include "agentspeed/traffic.hpp"
#include "format.hpp"

namespace character_demo {

namespace {
constexpr double kMicroUsdPerToken = 15.0;  // OrbRuntime.previewMicroUsdPerToken
}

bool enabled() { return qEnvironmentVariableIsSet("AGENT_SPEED_ORB_READOUTS"); }

QVariantMap switches() {
    QVariantMap out;
    for (const auto& item : qEnvironmentVariable("AGENT_SPEED_ORB_READOUTS").split(QLatin1Char(','), Qt::SkipEmptyParts)) {
        const bool off = item.startsWith(QLatin1Char('-'));
        out.insert(off ? item.mid(1) : item, !off);
    }
    return out;
}

void apply(QVariantMap& orb, double time, const QString& language) {
    const double value = orb.value(QStringLiteral("liveOutput")).toDouble();
    const double wobble = 1 + 0.35 * std::sin(time * 1.7) + 0.15 * std::sin(time * 4.3);
    const double rx = value * 52 * wobble, tx = 0.22 * rx + 120 * std::max(0.0, std::sin(time * 0.9));
    QVariantMap traffic = orb.value(QStringLiteral("traffic")).toMap();
    traffic.insert(QStringLiteral("enabled"), true);
    traffic.insert(QStringLiteral("available"), true);
    traffic.insert(QStringLiteral("machine"), true);
    traffic.insert(QStringLiteral("agentsKnown"), true);
    for (const char* scope : {"agents", "machine"}) {
        const QString s = QString::fromLatin1(scope);
        traffic.insert(s + QStringLiteral("Rx"), rx);
        traffic.insert(s + QStringLiteral("Tx"), tx);
        traffic.insert(s + QStringLiteral("RxText"), QString::fromStdString(agentspeed::traffic::formatRate(rx)));
        traffic.insert(s + QStringLiteral("TxText"), QString::fromStdString(agentspeed::traffic::formatRate(tx)));
    }
    orb.insert(QStringLiteral("traffic"), traffic);

    const bool generating = value > 1;
    QVariantMap set;
    if (generating) {
        const auto shown = static_cast<qint64>(760 + 3.2 * value + 40 * std::sin(time / 5));
        set.insert(QStringLiteral("form"), QStringLiteral("live"));
        set.insert(QStringLiteral("shown"), shown);
        set.insert(QStringLiteral("provisional"), value < 20);
    } else {
        set.insert(QStringLiteral("form"), QStringLiteral("none"));
        set.insert(QStringLiteral("shown"), -1);
    }
    const double thinking = generating ? value * (0.28 + 0.2 * std::sin(time / 3)) : 0;
    set.insert(QStringLiteral("generating"), generating);
    set.insert(QStringLiteral("thinkingText"), format::speed(thinking));
    // 首字 and the running models (METRICS 6.9 Readouts of a set): AGENT_SPEED_ORB_LATENCY fixes the latency (ms),
    // AGENT_SPEED_ORB_MODELS how many models run (3 by default; 0 to 8).
    const double fixedLatency = qEnvironmentVariableIntValue("AGENT_SPEED_ORB_LATENCY");
    set.insert(QStringLiteral("firstLatencyMs"), fixedLatency > 0 ? fixedLatency : 1200 + 380 * std::sin(time / 4));
    struct Demo {
        const char* model;
        const char* vendor;
        double share;
    };
    static constexpr Demo kModels[] = {
        {"claude-opus-5-5", "anthropic", 0.62},          {"gpt-6.1-sol", "openai", 0.41},
        {"claude-sonnet-4-5-20250929", "anthropic", 0.33}, {"gemini-4-argon", "google", 0.24},
        {"deepseek-v4-reasoner", "deepseek", 0},          {"kimi-k3", "moonshot", 0.18},
        {"glm-5.1", "zhipu", 0.12},                       {"grok-5-fast-reasoning-2026-09-01", "xai", 0.09},
    };
    const bool countSet = qEnvironmentVariableIsSet("AGENT_SPEED_ORB_MODELS");
    const int count = std::clamp(countSet ? qEnvironmentVariableIntValue("AGENT_SPEED_ORB_MODELS") : 3, 0, 8);
    struct Row {
        QString model, vendor;
        qint64 speed;
    };
    std::vector<Row> rows;
    for (int i = 0; i < count; ++i) {
        const auto& m = kModels[i];
        const double wave = 1 + 0.2 * std::sin(time * (0.7 + 0.3 * i) + i);
        rows.push_back({QString::fromLatin1(m.model), QString::fromLatin1(m.vendor),
                        m.share > 0 ? static_cast<qint64>(std::lround(std::max(value, 40.0) * m.share * wave)) : -1});
    }
    std::stable_sort(rows.begin(), rows.end(), [](const Row& a, const Row& b) {
        if ((a.speed < 0) != (b.speed < 0)) return a.speed >= 0;
        return a.speed > b.speed;
    });
    QVariantList models;
    for (const auto& r : rows) {
        models.push_back(QVariantMap{{QStringLiteral("model"), r.model}, {QStringLiteral("speed"), r.speed}, {QStringLiteral("vendor"), r.vendor}});
    }
    set.insert(QStringLiteral("models"), models);
    orb.insert(QStringLiteral("scoreSets"), QVariantList{set});

    namespace money = agentspeed::money;
    const auto& currency = money::CurrencyTable::bundled().currencyOrUsd("USD");
    const auto live = money::format(money::ratePerSecond(value * kMicroUsdPerToken, money::Decimal(1)), currency,
                                    language.toStdString(), money::Form::Rate);
    orb.insert(QStringLiteral("costNumber"), QString::fromStdString(live.number));
    orb.insert(QStringLiteral("costSymbol"), QString::fromStdString(live.symbol));
    orb.insert(QStringLiteral("costUnknown"), false);
    orb.insert(QStringLiteral("costPartial"), false);
    orb.insert(QStringLiteral("costSubscriptionOnly"), false);
}

}  // namespace character_demo
