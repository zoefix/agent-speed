#include <cmath>

#include "agentspeed/quota/vendors/parsers.hpp"
#include "agentspeed/quota/vendors/values.hpp"

// QUOTAS 9.8: the GLM Coding Plan's limits (z.ai, BigModel).
namespace agentspeed::quota::vendors {

namespace {

constexpr std::int64_t kFiveHourResetCapMs = 301 * 60'000;

std::optional<std::int64_t> unitMinutes(const json::Value& unit) {
    const auto n = strictNumber(unit);
    if (!n || std::trunc(*n) != *n) return std::nullopt;
    if (*n == 1) return kDayMinutes;
    if (*n == 3) return 60;
    if (*n == 5) return 1;
    if (*n == 6) return kWeekMinutes;
    return std::nullopt;
}

}  // namespace

std::optional<std::vector<Window>> zaiWindows(const json::Value& body, std::int64_t nowMs) {
    const auto code = strictNumber(body["code"]);
    if (!body["success"].isTrue() || !code || *code != 200) return std::nullopt;
    std::vector<Window> out;
    for (const auto& item : body["data"]["limits"].elements()) {
        if (!item.isObject()) continue;
        const auto usage = number(item["usage"]);
        const auto current = number(item["currentValue"]);
        const auto used = usage && current && *usage > 0 ? std::optional<double>(*current / *usage * 100)
                                                         : number(item["percentage"]);
        if (!used) continue;
        auto resets = timeMs(item["nextResetTime"]);
        const auto type = item["type"];
        if (type.equals("TIME_LIMIT")) {
            out.push_back(makeWindow(Kind::Month, *used, resets, "MCP"));
            continue;
        }
        if (!type.equals("TOKENS_LIMIT") && !type.equals("CREDIT_LIMIT")) continue;
        const auto unit = unitMinutes(item["unit"]);
        const auto count = number(item["number"]);
        if (!unit || !count) continue;
        const std::int64_t minutes = truncated(*count * static_cast<double>(*unit));
        const Kind kind = kindForMinutes(minutes);
        if (kind == Kind::FiveHour && resets && *resets - nowMs > kFiveHourResetCapMs) resets.reset();
        out.push_back(makeWindow(kind, *used, resets, std::nullopt,
                                 kind == Kind::Other ? std::optional<std::int64_t>(minutes) : std::nullopt));
    }
    return out;
}

}  // namespace agentspeed::quota::vendors
