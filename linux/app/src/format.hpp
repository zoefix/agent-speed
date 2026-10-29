#pragma once

#include <QString>
#include <cstdint>
#include <utility>

namespace format {

// Live speed as shown: one decimal below 10 tok/s, whole numbers above, thousands as 1.2k.
QString speed(double tokS);
// The compact form that fits the tray icon: at most four characters.
QString compactSpeed(double tokS);
// Token counts as 12.3k, 4.56M.
QString tokens(std::int64_t count);

// DESIGN 12 Number formats of the performance score, from the rounded integer: `full` groups up to 99,999 (1,286),
// `compact` keeps three significant digits from 10,000 (12.8k, 1.28M); a comma every three digits in every language.
QString perfFull(std::int64_t value);
QString perfCompact(std::int64_t value);
// The text every compact cell is measured with.
inline constexpr const char* kPerfMeasureText = "8,888";
// DESIGN 12 Log scale: f = clamp(log2(v / 250) / 4, 0, 1); 1000 is half.
double perfScale(double value);
// The score card's values (DESIGN 12 Components): a duration from milliseconds (ms below 1 s, s with one decimal
// below 10 s and whole up to 60 s, min from there) as {number, unit key}; a rate with one decimal below 10; a whole
// percent that never shows 100 % below 100 %.
std::pair<QString, QString> perfDuration(double ms);
QString oneDecimalBelowTen(double value);
QString cappedPercent(double share);

}  // namespace format
