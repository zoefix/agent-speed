#include "format.hpp"

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

}  // namespace format
