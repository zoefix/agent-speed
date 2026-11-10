#include <array>

#include "agentspeed/hermes.hpp"
#include "agentspeed/quota/vendors/parsers.hpp"
#include "agentspeed/quota/vendors/values.hpp"

// QUOTAS 9.3: the plan Devin Desktop caches in `state.vscdb`. `accountIdentityText` is never read.
namespace agentspeed::quota::vendors {

namespace {

constexpr std::size_t kMaxValueBytes = 1 << 20;

int digits(std::string_view text, std::size_t from, std::size_t count) {
    int value = 0;
    for (std::size_t i = from; i < from + count; ++i) value = value * 10 + (text[i] - '0');
    return value;
}

}  // namespace

std::vector<Window> devinWindows(const std::vector<DevinRow>& rows) {
    std::optional<double> bestEnd;
    json::Document best;
    std::string bestText;
    for (const auto& row : rows) {
        if (row.value.size() > kMaxValueBytes) continue;
        json::Document doc;
        if (!doc.parse(row.value) || !doc.root().isObject()) continue;
        const auto stated = number(doc.root()["endTimestamp"]);
        const double end = stated && *stated != 0 ? *stated : 0;
        if (!bestEnd || end > *bestEnd) {
            bestEnd = end;
            bestText = row.value;
        }
    }
    if (!bestEnd || !best.parse(bestText)) return {};
    const auto plan = best.root();
    struct Field {
        std::string_view hide, remaining, reset;
        Kind kind;
        std::optional<std::int64_t> minutes;
    };
    const std::array<Field, 2> fields = {{
        {"hideDailyQuota", "dailyRemainingPercent", "dailyResetAtUnix", Kind::Other, kDayMinutes},
        {"hideWeeklyQuota", "weeklyRemainingPercent", "weeklyResetAtUnix", Kind::SevenDay, std::nullopt},
    }};
    std::vector<Window> out;
    for (const auto& field : fields) {
        if (plan[field.hide].isTrue()) continue;
        const auto remaining = number(plan[field.remaining]);
        if (!remaining) continue;
        out.push_back(makeWindow(field.kind, 100 - *remaining, timeMs(plan[field.reset]), std::nullopt, field.minutes));
    }
    return out;
}

std::optional<std::int64_t> devinLaunchMs(std::string_view name, const TimeZone& zone) {
    if (name.size() != 15 || name[8] != 'T') return std::nullopt;
    for (std::size_t i = 0; i < name.size(); ++i) {
        if (i != 8 && (name[i] < '0' || name[i] > '9')) return std::nullopt;
    }
    return hermes::toUnixMs(digits(name, 0, 4), digits(name, 4, 2), digits(name, 6, 2), digits(name, 9, 2),
                            digits(name, 11, 2), digits(name, 13, 2), 0, zone);
}

}  // namespace agentspeed::quota::vendors
