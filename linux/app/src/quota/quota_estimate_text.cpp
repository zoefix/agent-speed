#include "quota/quota_estimate_text.hpp"

#include <algorithm>
#include <cmath>

#include "agentspeed/money.hpp"
#include "strings.hpp"

namespace est = agentspeed::quota::estimate;
namespace money = agentspeed::money;

namespace quota_estimate_text {

namespace {

const money::Currency& currencyOf(const Money& m) { return money::CurrencyTable::bundled().currencyOrUsd(m.currency.toStdString()); }

money::Decimal rateOf(const Money& m) {
    if (m.currency == QLatin1String("USD")) return money::Decimal(1);
    return money::Decimal::parse(m.rate.toStdString()).value_or(money::Decimal(1));
}

bool isUsd(const Money& m) { return currencyOf(m).code == "USD"; }

std::string compactNumber(std::int64_t microUsd, const money::Currency& currency, const money::Decimal& rate) {
    return money::number(money::amount(microUsd, rate), currency.digits, money::Form::Rate);
}

// A tooltip's amount, and beside another currency the US dollars (DESIGN 9 Tooltips: `NT$1,235（US$37.94）`).
QString amountWithUsd(std::int64_t microUsd, const Money& m, const QString& language) {
    const auto text = amount(microUsd, m, language);
    if (isUsd(m)) return text;
    const auto& usd = money::CurrencyTable::bundled().currencyOrUsd("USD");
    const auto dollars = QString::fromStdString(money::withSymbol(usd.symbolFor(language.toStdString()), compactNumber(microUsd, usd, money::Decimal(1))));
    return language == QLatin1String("en") ? text + QStringLiteral(" (") + dollars + QLatin1Char(')') : text + QStringLiteral("（") + dollars + QStringLiteral("）");
}

}  // namespace

const est::Entry* find(const std::vector<est::Entry>& entries, const agentspeed::quota::Window& window) {
    const auto kind = std::string(agentspeed::quota::kindId(window.kind));
    const auto minutes = window.minutes.value_or(agentspeed::quota::defaultMinutes(window.kind).value_or(0));
    for (const auto& e : entries) {
        if (e.kind == kind && e.name == window.name && e.minutes == minutes) return &e;
    }
    return nullptr;
}

// The compact digits of a quota line (the rate form: `4,938`, `4.20`), with the symbol in front unless `symbol` is false.
QString amount(std::int64_t microUsd, const Money& m, const QString& language, bool symbol) {
    const auto& currency = currencyOf(m);
    const auto number = compactNumber(microUsd, currency, rateOf(m));
    if (!symbol) return QString::fromStdString(number);
    return QString::fromStdString(money::withSymbol(currency.symbolFor(language.toStdString()), number));
}

Decoration decorate(const est::Entry& e, std::int64_t nowMs, const Strings& strings, const Money& m) {
    Decoration d;
    const auto language = strings.language();
    d.percent = e.liveExact;
    d.estimated = e.liveEstimated;
    d.resetsAtMs = e.resetsAtMs && *e.resetsAtMs > nowMs ? e.resetsAtMs : std::nullopt;
    d.passed = !e.usedPercent && !d.resetsAtMs;
    const bool hasEstimate = e.confidence != est::Confidence::None;
    if (e.usedMicroUsd > 0 || hasEstimate) {
        const QString partial = e.unpricedRequests > 0 && e.usedMicroUsd > 0 ? QStringLiteral("≥") : QString();
        d.amount = partial + amount(e.usedMicroUsd, m, language);
        QString full;
        if (e.capMicroUsd) {
            full = strings.fmt(QStringLiteral("quota.amount.cap"), {d.amount, amount(*e.capMicroUsd, m, language, false)});
        } else if (hasEstimate && e.lowMicroUsd) {
            full = strings.fmt(QStringLiteral("quota.amount.atLeast"), {d.amount, amount(*e.lowMicroUsd, m, language, false)});
        }
        if (full.startsWith(d.amount)) d.cap = full.mid(d.amount.size());
        const auto spokenUsed = amountWithUsd(e.usedMicroUsd, m, language);
        d.amountSpoken = e.capMicroUsd ? strings.fmt(QStringLiteral("a11y.quota.amount"), {spokenUsed, amountWithUsd(*e.capMicroUsd, m, language)})
                                       : strings.fmt(QStringLiteral("a11y.quota.amountNoCap"), {spokenUsed});
    }
    if (!hasEstimate) return d;
    d.details.push_back(strings.fmt(QStringLiteral("quota.estimate.used"), {amountWithUsd(e.usedMicroUsd, m, language)}));
    if (e.capMicroUsd && e.lowMicroUsd && e.highMicroUsd) {
        d.details.push_back(strings.fmt(QStringLiteral("quota.estimate.cap"),
                                        {amountWithUsd(*e.capMicroUsd, m, language), amount(*e.lowMicroUsd, m, language),
                                         amount(*e.highMicroUsd, m, language, false)}));
    } else if (e.lowMicroUsd) {
        d.details.push_back(strings.fmt(QStringLiteral("quota.estimate.atLeast"), {amountWithUsd(*e.lowMicroUsd, m, language)}));
    }
    if (e.capMicroUsd && e.accountUsedMicroUsd) {
        d.details.push_back(strings.fmt(QStringLiteral("quota.estimate.account"), {amountWithUsd(*e.accountUsedMicroUsd, m, language)}));
    }
    if (e.coverage) {
        d.details.push_back(strings.fmt(QStringLiteral("quota.estimate.coverage"), {static_cast<int>(std::lround(*e.coverage * 100))}));
    }
    const auto confidence = strings.t(QStringLiteral("quota.estimate.confidence.") + QString::fromUtf8(est::confidenceId(e.confidence).data()));
    d.details.push_back(confidence + QStringLiteral(" · ") + strings.fmt(QStringLiteral("quota.estimate.windows"), {e.windows}));
    if (e.plan) {
        auto plan = QString::fromStdString(*e.plan);
        if (e.tier) plan += QStringLiteral(" · ") + QString::fromStdString(*e.tier);
        d.details.push_back(strings.fmt(QStringLiteral("quota.estimate.plan"), {plan}));
    }
    if (d.estimated) d.details.push_back(strings.t(QStringLiteral("quota.estimate.live")));
    d.note = strings.t(QStringLiteral("quota.estimate.note"));
    return d;
}

}  // namespace quota_estimate_text
