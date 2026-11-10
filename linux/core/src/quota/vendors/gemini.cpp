#include <algorithm>
#include <cctype>
#include <map>

#include "agentspeed/quota/vendors/parsers.hpp"
#include "agentspeed/quota/vendors/values.hpp"

// QUOTAS 9.6: Gemini CLI's Code Assist project and its daily quota per model family.
namespace agentspeed::quota::vendors {

namespace {

int familyRank(const std::string& family) {
    if (family == "Pro") return 0;
    if (family == "Flash") return 1;
    if (family == "Flash Lite") return 2;
    return 3;
}

}  // namespace

std::optional<std::string> geminiProject(const json::Value& body) {
    const auto project = body["cloudaicompanionProject"];
    if (project.isString()) return project.nonEmptyString();
    if (project.isObject() && project["id"].isString()) return project["id"].string();
    return std::nullopt;
}

std::string geminiFamily(std::string_view modelId) {
    std::string folded(modelId);
    for (char& c : folded) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    if (folded.find("flash-lite") != std::string::npos) return "Flash Lite";
    if (folded.find("flash") != std::string::npos) return "Flash";
    if (folded.find("pro") != std::string::npos) return "Pro";
    return std::string(modelId);
}

std::vector<Window> geminiWindows(const json::Value& body) {
    struct Lowest {
        double remaining;
        std::optional<std::int64_t> resets;
    };
    std::map<std::string, Lowest> best;
    for (const auto& bucket : body["buckets"].elements()) {
        if (!bucket.isObject()) continue;
        const auto remaining = strictNumber(bucket["remainingFraction"]);
        if (!remaining) continue;
        const auto family = geminiFamily(bucket["modelId"].string().value_or(""));
        if (family.empty()) continue;
        const auto found = best.find(family);
        if (found == best.end() || *remaining < found->second.remaining) best[family] = {*remaining, timeMs(bucket["resetTime"])};
    }
    std::vector<std::string> names;
    for (const auto& [name, lowest] : best) names.push_back(name);
    std::stable_sort(names.begin(), names.end(), [](const std::string& a, const std::string& b) {
        const int ra = familyRank(a), rb = familyRank(b);
        return ra != rb ? ra < rb : a < b;
    });
    std::vector<Window> out;
    for (const auto& name : names) {
        const auto& lowest = best.at(name);
        out.push_back(makeWindow(Kind::Other, (1 - lowest.remaining) * 100, lowest.resets, name, kDayMinutes));
    }
    return out;
}

}  // namespace agentspeed::quota::vendors
