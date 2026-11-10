#include <algorithm>

#include "agentspeed/quota/vendors/parsers.hpp"
#include "agentspeed/quota/vendors/values.hpp"

// QUOTAS 9.9: Qoder's big-model credits and WorkBuddy's resource packages (browser sessions).
namespace agentspeed::quota::vendors {

std::vector<Window> qoderWindows(const json::Value& body) {
    if (!body.isObject()) return {};
    double used = 0, limit = 0;
    bool seen = false;
    std::vector<double> percents;
    for (const auto key : {"totalQuota", "sharedQuota"}) {
        const auto summary = body[key]["quotaSummary"];
        if (!summary.isObject()) continue;
        const auto value = number(pick(summary, {"usedValue", "used_value"}));
        const auto cap = number(pick(summary, {"limitValue", "limit_value"}));
        if (value && cap && *cap > 0) {
            used += *value;
            limit += *cap;
            seen = true;
        } else if (const auto percent = number(pick(summary, {"usagePercentage", "usage_percentage"}))) {
            percents.push_back(*percent);
        }
    }
    const auto resetValue = pick(body, {"nextResetAt", "next_reset_at"});
    const auto resets = resetValue.valid() ? timeMs(resetValue) : std::nullopt;
    if (seen) return {makeWindow(Kind::Credits, used / limit * 100, resets)};
    if (!percents.empty()) return {makeWindow(Kind::Credits, *std::max_element(percents.begin(), percents.end()), resets)};
    return {};
}

std::optional<std::vector<Window>> workBuddyWindows(const json::Value& body) {
    const auto code = strictNumber(body["code"]);
    if (!code || *code != 0) return std::nullopt;
    double total = 0, remain = 0;
    bool seen = false;
    for (const auto& package : body["data"]["Packages"].elements()) {
        if (!package.isObject() || !package["CapacityUnit"].equals("credits")) continue;
        const auto cycle = number(package["CycleTotalCapacity"]);
        const auto left = number(package["CycleRemainCapacity"]);
        if (!cycle || !left) continue;
        total += *cycle;
        remain += *left;
        seen = true;
    }
    if (!seen || total <= 0) return std::vector<Window>{};
    return std::vector<Window>{makeWindow(Kind::Month, (total - remain) / total * 100, std::nullopt)};
}

std::string chromeUserAgent(std::string_view platformToken, int major) {
    return "Mozilla/5.0 (" + std::string(platformToken) + ") AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" +
           std::to_string(major) + ".0.0.0 Safari/537.36";
}

std::string firefoxUserAgent(std::string_view platformToken, std::string_view version) {
    const std::string v(version);
    return "Mozilla/5.0 (" + std::string(platformToken) + "; rv:" + v + ") Gecko/20100101 Firefox/" + v;
}

}  // namespace agentspeed::quota::vendors
