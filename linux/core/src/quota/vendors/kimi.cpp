#include <algorithm>
#include <array>

#include "agentspeed/quota/vendors/parsers.hpp"
#include "agentspeed/quota/vendors/values.hpp"

// QUOTAS 9.5: Kimi Code's usages, in the current `usages` shape or the legacy `limits` one.
namespace agentspeed::quota::vendors {

namespace {

std::optional<std::int64_t> unitMinutes(const json::Value& unit) {
    if (unit.equals("TIME_UNIT_MINUTE")) return 1;
    if (unit.equals("TIME_UNIT_HOUR")) return 60;
    if (unit.equals("TIME_UNIT_DAY")) return kDayMinutes;
    if (unit.equals("TIME_UNIT_WEEK")) return kWeekMinutes;
    return std::nullopt;
}

std::optional<std::int64_t> reset(const json::Value& detail) {
    for (const auto key : {"resetTime", "reset_time", "resetAt", "reset_at"}) {
        const auto v = detail[key];
        if (v.valid() && !v.isNull()) return timeMs(v);
    }
    return std::nullopt;
}

std::optional<double> usedPercent(const json::Value& detail) {
    const auto limit = number(detail["limit"]);
    auto used = number(detail["used"]);
    const auto remaining = number(detail["remaining"]);
    if (!limit || *limit <= 0) return std::nullopt;
    if (!used && remaining) used = *limit - *remaining;
    if (!used) return std::nullopt;
    return *used / *limit * 100;
}

std::vector<Window> current(const json::Value& usages) {
    std::vector<Window> out;
    if (!usages.isObject()) return out;
    constexpr std::array<std::pair<std::string_view, Kind>, 2> kFixed = {{{"limit_5h", Kind::FiveHour}, {"limit_7d", Kind::SevenDay}}};
    for (const auto& [key, kind] : kFixed) {
        const auto w = usages[key];
        if (!w.isObject()) continue;
        if (const auto ratio = number(w["used_ratio"])) out.push_back(makeWindow(kind, *ratio * 100, reset(w)));
    }
    for (const auto key : {"limit_month_total", "limit_month"}) {
        const auto w = usages[key];
        if (!w.isObject()) continue;
        if (const auto ratio = number(w["used_ratio"])) {
            out.push_back(makeWindow(Kind::Month, *ratio * 100, reset(w)));
            break;
        }
    }
    return out;
}

}  // namespace

std::vector<Window> kimiWindows(const json::Value& body) {
    auto out = current(body["usages"]);
    if (!out.empty()) return out;
    for (const auto& item : body["limits"].elements()) {
        if (!item.isObject()) continue;
        const auto window = item["window"];
        const auto detail = item["detail"].isObject() ? item["detail"] : json::Value();
        const auto duration = number(window["duration"]);
        const auto unit = unitMinutes(window["timeUnit"]);
        const auto used = usedPercent(detail);
        if (!duration || !unit || !used) continue;
        const std::int64_t minutes = truncated(*duration * static_cast<double>(*unit));
        const Kind kind = kindForMinutes(minutes);
        out.push_back(makeWindow(kind, *used, reset(detail), std::nullopt,
                                 kind == Kind::Other ? std::optional<std::int64_t>(minutes) : std::nullopt));
    }
    const auto usage = body["usage"];
    const bool hasWeek = std::any_of(out.begin(), out.end(), [](const Window& w) { return w.kind == Kind::SevenDay; });
    if (usage.isObject() && !hasWeek) {
        if (const auto used = usedPercent(usage)) out.push_back(makeWindow(Kind::SevenDay, *used, reset(usage)));
    }
    return out;
}

}  // namespace agentspeed::quota::vendors
