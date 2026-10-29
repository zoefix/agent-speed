#pragma once

#include <QString>
#include <cstdint>

namespace format {

// Live speed as shown: one decimal below 10 tok/s, whole numbers above, thousands as 1.2k.
QString speed(double tokS);
// The compact form that fits the tray icon: at most four characters.
QString compactSpeed(double tokS);
// Token counts as 12.3k, 4.56M.
QString tokens(std::int64_t count);

}  // namespace format
