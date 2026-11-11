#include "quota/quota_text.hpp"

#include <QDateTime>
#include <QStringList>
#include <algorithm>
#include <QTimeZone>

#include "strings.hpp"

namespace quota = agentspeed::quota;

namespace quota_text {

namespace {

std::int64_t utcOffsetMs(std::int64_t unixMs) {
    const auto at = QDateTime::fromMSecsSinceEpoch(unixMs, QTimeZone::systemTimeZone());
    return static_cast<std::int64_t>(QTimeZone::systemTimeZone().offsetFromUtc(at)) * 1000;
}

QString text(const std::string& s) { return QString::fromStdString(s); }

}  // namespace

quota::QuotaFormat format(const Strings& strings) {
    auto lookup = [&strings](std::string_view key) {
        return strings.t(QString::fromUtf8(key.data(), static_cast<qsizetype>(key.size()))).toStdString();
    };
    return quota::QuotaFormat(quota::QuotaStrings::from(lookup), strings.language().toStdString(), utcOffsetMs);
}

QString level(int percent) {
    if (percent >= 95) return QStringLiteral("critical");
    if (percent >= 80) return QStringLiteral("warning");
    return QStringLiteral("normal");
}

QString updatedTime(std::int64_t observedMs, std::int64_t nowMs) {
    const auto at = QDateTime::fromMSecsSinceEpoch(observedMs, QTimeZone::systemTimeZone());
    const auto now = QDateTime::fromMSecsSinceEpoch(nowMs, QTimeZone::systemTimeZone());
    const auto time = at.toString(QStringLiteral("HH:mm"));
    if (at.date() == now.date()) return time;
    return QStringLiteral("%1/%2 %3").arg(at.date().month()).arg(at.date().day()).arg(time);
}

QVariantList lines(const quota::Snapshot& snapshot, std::int64_t nowMs, const Strings& strings, const quota::QuotaFormat& format,
                   const std::vector<agentspeed::quota::estimate::Entry>* estimates, const quota_estimate_text::Money& money) {
    QVariantList out;
    const auto age = format.age(snapshot.observedMs, nowMs);
    const auto updated = strings.fmt(QStringLiteral("quota.updated"), {updatedTime(snapshot.observedMs, nowMs)});
    const auto separator = strings.t(QStringLiteral("list.separator"));
    for (const auto& shown : quota::shown(snapshot, nowMs)) {
        QVariantMap line;
        const auto label = text(format.label(shown.window));
        const auto pace = quota::pace(shown.window, nowMs);
        int percent = shown.percent;
        double bar = shown.bar;
        auto resetsAtMs = shown.resetsAtMs;
        bool passed = shown.passed;
        std::optional<quota_estimate_text::Decoration> d;
        if (const auto* e = estimates ? quota_estimate_text::find(*estimates, shown.window) : nullptr) {
            d = quota_estimate_text::decorate(*e, nowMs, strings, money);
            passed = d->passed;
            resetsAtMs = d->resetsAtMs;
            percent = passed ? 0 : quota::displayPercent(d->percent);
            bar = passed ? 0 : std::clamp(d->percent, 0.0, 100.0);
        }
        line.insert(QStringLiteral("label"), label);
        line.insert(QStringLiteral("percent"), percent);
        line.insert(QStringLiteral("percentText"), QString::number(percent) + QLatin1Char('%'));
        line.insert(QStringLiteral("fraction"), bar / 100.0);
        line.insert(QStringLiteral("pace"), pace ? *pace : -1.0);
        line.insert(QStringLiteral("level"), level(percent));
        const auto reset = resetsAtMs ? text(format.resetLong(*resetsAtMs, nowMs)) : QString();
        line.insert(QStringLiteral("reset"), reset);
        line.insert(QStringLiteral("short"), resetsAtMs ? text(format.resetShort(*resetsAtMs, nowMs)) : QString());
        line.insert(QStringLiteral("age"), age ? text(*age) : QString());
        QStringList tooltip;
        if (!reset.isEmpty()) tooltip.push_back(reset);
        tooltip.push_back(updated);
        if (pace) tooltip.push_back(text(format.paceText(*pace)));
        line.insert(QStringLiteral("tooltip"), tooltip.join(QLatin1Char('\n')));
        const auto window = resetsAtMs ? strings.fmt(QStringLiteral("a11y.quota.window"), {label, percent, reset})
                                       : strings.fmt(QStringLiteral("a11y.quota.windowNoReset"), {label, percent});
        QStringList spoken{window}, spokenCard{window};
        const bool estimated = d && d->estimated && !passed;
        if (d && !d->amount.isEmpty()) spoken.push_back(d->amountSpoken);
        if (estimated) {
            spoken.push_back(strings.t(QStringLiteral("a11y.quota.estimated")));
            spokenCard.push_back(strings.t(QStringLiteral("a11y.quota.estimated")));
        }
        line.insert(QStringLiteral("spoken"), spoken.join(separator));
        line.insert(QStringLiteral("spokenCard"), spokenCard.join(separator));
        line.insert(QStringLiteral("estimated"), estimated);
        line.insert(QStringLiteral("amount"), d ? d->amount : QString());
        line.insert(QStringLiteral("cap"), d ? d->cap : QString());
        line.insert(QStringLiteral("details"), d ? d->details : QStringList());
        line.insert(QStringLiteral("note"), d ? d->note : QString());
        line.insert(QStringLiteral("passed"), passed);
        out.push_back(line);
    }
    return out;
}

QString spoken(const QVariantList& lines, const Strings& strings) {
    QStringList parts;
    for (const auto& line : lines) parts.push_back(line.toMap().value(QStringLiteral("spoken")).toString());
    return parts.join(strings.t(QStringLiteral("list.separator")));
}

}  // namespace quota_text
