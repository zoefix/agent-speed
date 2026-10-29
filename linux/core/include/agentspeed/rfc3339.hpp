#pragma once

#include <cstdint>
#include <optional>
#include <string_view>

namespace agentspeed {

// METRICS 3.1: `YYYY-MM-DD(T|t| )hh:mm:ss[.fraction](Z|z|±hh:mm|±hhmm)` to Unix milliseconds, computed arithmetically
// for every valid string (also before 1970 and after 9999 UTC). Anything else is not a timestamp.
std::optional<std::int64_t> parseTimestamp(std::string_view text);

// Days from 1970-01-01 to the given proleptic Gregorian date.
std::int64_t daysFromCivil(std::int64_t year, unsigned month, unsigned day);

}  // namespace agentspeed
