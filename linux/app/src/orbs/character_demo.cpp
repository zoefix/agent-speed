#include "orbs/character_demo.hpp"

#include <QStringList>
#include <QVariantList>
#include <cmath>

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
