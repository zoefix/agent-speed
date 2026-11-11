#include "agentspeed/quota/estimate/model.hpp"

#include <charconv>
#include <cmath>

namespace agentspeed::quota::estimate {

std::int64_t floorDiv(std::int64_t a, std::int64_t b) {
    const auto q = a / b;
    return (a % b != 0 && ((a < 0) != (b < 0))) ? q - 1 : q;
}

std::string windowId(std::string_view kind, const std::optional<std::string>& name, std::int64_t minutes) {
    std::string out(kind);
    out += ':';
    if (name) out += *name;
    out += ':';
    out += std::to_string(minutes);
    return out;
}

std::int64_t minutesOfWindowId(std::string_view id) {
    const auto colon = id.rfind(':');
    if (colon == std::string_view::npos) return 0;
    const auto digits = id.substr(colon + 1);
    if (digits.empty()) return 0;
    std::int64_t value = 0;
    for (const char c : digits) {
        if (c < '0' || c > '9') return 0;
        value = value * 10 + (c - '0');
    }
    return value;
}

double pyRound(double x, int digits) {
    if (!std::isfinite(x) || std::fabs(x) >= 1e300) return x;
    // to_chars and from_chars ignore the C locale, which the Qt app sets from the environment.
    char buffer[400];
    const auto written = std::to_chars(buffer, buffer + sizeof buffer, x, std::chars_format::fixed, digits);
    double out = x;
    std::from_chars(buffer, written.ptr, out);
    return out;
}

std::int64_t pyRoundToInt(double x) { return static_cast<std::int64_t>(std::nearbyint(x)); }

double roundHalfAway(double x, int digits) {
    double m = 1;
    for (int i = 0; i < digits; ++i) m *= 10;
    const double v = std::floor(std::fabs(x) * m + 0.5 + 1e-9) / m;
    return std::copysign(v, x);
}

std::int64_t micro(double x) { return static_cast<std::int64_t>(std::floor(x + 0.5)); }

std::string floatText(double x) {
    if (!std::isfinite(x)) return "null";
    if (x == std::floor(x) && std::fabs(x) < 1e16) {
        char buffer[40];
        const auto result = std::to_chars(buffer, buffer + sizeof buffer, x, std::chars_format::fixed, 1);
        return std::string(buffer, result.ptr);
    }
    char buffer[40];
    const auto result = std::to_chars(buffer, buffer + sizeof buffer, x);
    return std::string(buffer, result.ptr);
}

}  // namespace agentspeed::quota::estimate
