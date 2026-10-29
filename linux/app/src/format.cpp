#include "format.hpp"

#include <algorithm>
#include <cmath>

namespace format {

QString speed(double tokS) {
    if (tokS < 0.05) return QStringLiteral("0");
    if (tokS < 10) return QString::number(tokS, 'f', 1);
    if (tokS < 10'000) return QString::number(std::lround(tokS));
    return QString::number(tokS / 1000.0, 'f', 1) + QLatin1Char('k');
}

QString compactSpeed(double tokS) {
    if (tokS < 0.5) return QStringLiteral("0");
    if (tokS < 1000) return QString::number(std::lround(tokS));
    if (tokS < 9950) return QString::number(tokS / 1000.0, 'f', 1) + QLatin1Char('k');
    return QString::number(std::lround(tokS / 1000.0)) + QLatin1Char('k');
}

QString tokens(std::int64_t count) {
    const double value = static_cast<double>(count);
    if (count < 1000) return QString::number(count);
    if (count < 1'000'000) return QString::number(value / 1000.0, 'f', value < 10'000 ? 2 : 1) + QLatin1Char('k');
    if (count < 1'000'000'000) return QString::number(value / 1e6, 'f', value < 1e7 ? 2 : 1) + QLatin1Char('M');
    return QString::number(value / 1e9, 'f', 2) + QLatin1Char('B');
}

namespace {

// `value / divisor` rounded half away from zero, for value ≥ 0.
std::int64_t roundedBy(std::int64_t value, std::int64_t divisor) { return (value + divisor / 2) / divisor; }

QString decimal(std::int64_t scaled, int places) {
    auto text = QString::number(scaled);
    while (text.size() <= places) text.prepend(QLatin1Char('0'));
    text.insert(text.size() - places, QLatin1Char('.'));
    return text;
}

QString grouped(std::int64_t value) {
    const auto digits = QString::number(value < 0 ? -value : value);
    QString out;
    for (qsizetype i = 0; i < digits.size(); ++i) {
        if (i > 0 && (digits.size() - i) % 3 == 0) out += QLatin1Char(',');
        out += digits[i];
    }
    return value < 0 ? QLatin1Char('-') + out : out;
}

double halfAway(double value, double scale) { return std::floor(value * scale + 0.5) / scale; }

}  // namespace

QString perfFull(std::int64_t value) { return value >= 100'000 ? perfCompact(value) : grouped(value); }

QString perfCompact(std::int64_t value) {
    if (value < 10'000) return grouped(value);
    if (const auto tenths = roundedBy(value, 100); tenths < 1000) return decimal(tenths, 1) + QLatin1Char('k');
    if (const auto thousands = roundedBy(value, 1000); thousands < 1000) return QString::number(thousands) + QLatin1Char('k');
    if (const auto hundredths = roundedBy(value, 10'000); hundredths < 1000) return decimal(hundredths, 2) + QLatin1Char('M');
    if (const auto tenths = roundedBy(value, 100'000); tenths < 1000) return decimal(tenths, 1) + QLatin1Char('M');
    return QString::number(roundedBy(value, 1'000'000)) + QLatin1Char('M');
}

double perfScale(double value) {
    if (!(value > 0) || !std::isfinite(value)) return 0;
    return std::clamp(std::log2(value / 250) / 4, 0.0, 1.0);
}

std::pair<QString, QString> perfDuration(double ms) {
    const auto whole = static_cast<std::int64_t>(std::floor(ms + 0.5));
    if (whole < 1000) return {QString::number(whole), QStringLiteral("perf.unit.ms")};
    if (const double s = halfAway(ms / 1000, 10); s < 10) return {QString::number(s, 'f', 1), QStringLiteral("perf.unit.s")};
    if (const auto s = static_cast<std::int64_t>(std::floor(ms / 1000 + 0.5)); s < 60) return {QString::number(s), QStringLiteral("perf.unit.s")};
    if (const double m = halfAway(ms / 60'000, 10); m < 10) return {QString::number(m, 'f', 1), QStringLiteral("perf.unit.min")};
    return {QString::number(static_cast<std::int64_t>(std::floor(ms / 60'000 + 0.5))), QStringLiteral("perf.unit.min")};
}

QString oneDecimalBelowTen(double value) {
    if (const double tenth = halfAway(value, 10); tenth < 10) return QString::number(tenth, 'f', 1);
    return QString::number(static_cast<std::int64_t>(std::floor(value + 0.5)));
}

QString cappedPercent(double share) {
    auto whole = static_cast<std::int64_t>(std::floor(share * 100 + 0.5));
    if (share < 1 && whole >= 100) whole = 99;
    return QString::number(whole) + QLatin1Char('%');
}

}  // namespace format
