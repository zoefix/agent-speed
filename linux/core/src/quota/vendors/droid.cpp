#include <array>

#include "agentspeed/quota/vendors/parsers.hpp"
#include "agentspeed/quota/vendors/values.hpp"

// QUOTAS 9.7: Factory Droid's API key line and its billing limits.
namespace agentspeed::quota::vendors {

namespace {

constexpr std::string_view kSpace = " \t\r\n\f\v";
constexpr std::string_view kVariable = "FACTORY_API_KEY=";

std::string_view strip(std::string_view text) {
    const auto first = text.find_first_not_of(kSpace);
    if (first == std::string_view::npos) return {};
    return text.substr(first, text.find_last_not_of(kSpace) - first + 1);
}

}  // namespace

std::optional<std::string> droidKeyFromEnvFile(std::string_view text) {
    std::size_t start = 0;
    while (start <= text.size()) {
        auto end = text.find_first_of("\r\n", start);
        if (end == std::string_view::npos) end = text.size();
        auto line = strip(text.substr(start, end - start));
        start = end + 1;
        if (line.substr(0, 7) == "export ") line = strip(line.substr(7));
        if (line.substr(0, kVariable.size()) != kVariable) continue;
        auto value = strip(line.substr(kVariable.size()));
        if (value.size() >= 2 && value.front() == value.back() && (value.front() == '"' || value.front() == '\'')) {
            value = value.substr(1, value.size() - 2);
        }
        if (value.empty()) return std::nullopt;
        return std::string(value);
    }
    return std::nullopt;
}

std::vector<Window> droidWindows(const json::Value& body, std::int64_t nowMs) {
    const auto limits = body["limits"];
    std::vector<Window> out;
    if (!limits.isObject()) return out;
    constexpr std::array<std::pair<std::string_view, Kind>, 3> kWindows = {
        {{"fiveHour", Kind::FiveHour}, {"weekly", Kind::SevenDay}, {"monthly", Kind::Month}}};
    for (const auto& [pool, name] : {std::pair<std::string_view, std::optional<std::string>>{"standard", std::nullopt},
                                     std::pair<std::string_view, std::optional<std::string>>{"core", "Core"}}) {
        const auto p = limits[pool];
        if (!p.isObject()) continue;
        for (const auto& [key, kind] : kWindows) {
            const auto w = p[key];
            if (!w.isObject()) continue;
            auto used = number(w["usedPercent"]);
            if (!used) continue;
            const auto remaining = number(w["secondsRemaining"]);
            const auto end = timeMs(w["windowEnd"]);
            std::optional<std::int64_t> resets;
            if (remaining) {
                resets = nowMs + truncated(*remaining * 1000);
            } else if (end && *end <= nowMs) {
                used = 0.0;
            } else {
                resets = end;
            }
            out.push_back(makeWindow(kind, *used, resets, name));
        }
    }
    return out;
}

}  // namespace agentspeed::quota::vendors
