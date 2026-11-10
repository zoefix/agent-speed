#pragma once

#include <cstdint>
#include <initializer_list>
#include <optional>
#include <string>
#include <string_view>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/window.hpp"

// The value rules the phase 2 readers share (QUOTAS 9), as spec/tools/quota_vendors.py applies them.
namespace agentspeed::quota::vendors {

// A finite JSON number; never a boolean.
std::optional<double> strictNumber(const json::Value& value);
// A finite number, or a string that reads as one (`"150"`, `" 2.5 "`).
std::optional<double> number(const json::Value& value);
std::optional<double> numberText(std::string_view text);

// Python's int() of a finite double, saturated to the signed 64-bit range.
std::int64_t truncated(double value);
// Epoch seconds, or epoch milliseconds above 1e11, as epoch milliseconds.
std::int64_t epochMs(double value);
// ISO 8601 as `datetime.fromisoformat` reads it: an offset or `Z`, else UTC; a date alone is its midnight.
std::optional<std::int64_t> isoMs(std::string_view text);
// ISO 8601 text, epoch seconds or epoch milliseconds (numbers or digit strings).
std::optional<std::int64_t> timeMs(const json::Value& value);

// `str(value or "")`: the text a reader matches words in.
std::string text(const json::Value& value);
// Python truthiness of a member.
bool truthy(const json::Value& value);

// 290-310 minutes is the 5-hour window, 10000-10160 the weekly one, anything else `other`.
Kind kindForMinutes(std::int64_t minutes);

// The first member that is present and not null; an invalid value when none is.
json::Value pick(const json::Value& object, std::initializer_list<std::string_view> keys);

}  // namespace agentspeed::quota::vendors
