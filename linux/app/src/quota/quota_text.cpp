#include "quota/quota_text.hpp"

#include <QDateTime>
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

QVariantList lines(const quota::Snapshot& snapshot, std::int64_t nowMs, const Strings& strings, const quota::QuotaFormat& format) {
    QVariantList out;
    const auto age = format.age(snapshot.observedMs, nowMs);
    const auto updated = strings.fmt(QStringLiteral("quota.updated"), {updatedTime(snapshot.observedMs, nowMs)});
    for (const auto& shown : quota::shown(snapshot, nowMs)) {
        QVariantMap line;
        const auto label = text(format.label(shown.window));
        const auto pace = quota::pace(shown.window, nowMs);
        line.insert(QStringLiteral("label"), label);
        line.insert(QStringLiteral("percent"), shown.percent);
        line.insert(QStringLiteral("percentText"), QString::number(shown.percent) + QLatin1Char('%'));
        line.insert(QStringLiteral("fraction"), shown.bar / 100.0);
        line.insert(QStringLiteral("pace"), pace ? *pace : -1.0);
        line.insert(QStringLiteral("level"), level(shown.percent));
        const auto reset = shown.resetsAtMs ? text(format.resetLong(*shown.resetsAtMs, nowMs)) : QString();
        line.insert(QStringLiteral("reset"), reset);
        line.insert(QStringLiteral("short"), shown.resetsAtMs ? text(format.resetShort(*shown.resetsAtMs, nowMs)) : QString());
        line.insert(QStringLiteral("age"), age ? text(*age) : QString());
        line.insert(QStringLiteral("tooltip"), pace ? updated + QLatin1Char('\n') + text(format.paceText(*pace)) : updated);
        line.insert(QStringLiteral("spoken"), shown.resetsAtMs ? strings.fmt(QStringLiteral("a11y.quota.window"), {label, shown.percent, reset})
                                                               : strings.fmt(QStringLiteral("a11y.quota.windowNoReset"), {label, shown.percent}));
        line.insert(QStringLiteral("passed"), shown.passed);
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
