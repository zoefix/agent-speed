#include "agentspeed/quota/estimate/plan_inference.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <tuple>

#include "agentspeed/quota/estimate/model.hpp"

namespace agentspeed::quota::estimate {

namespace {

struct KnownPlan {
    std::string_view plan;
    std::optional<std::string_view> tier;
    double multiplier;
};
constexpr std::array<KnownPlan, 3> kPlans{{
    {"claude_pro", std::nullopt, 1},
    {"claude_max", "default_claude_max_5x", 5},
    {"claude_max", "default_claude_max_20x", 20},
}};
constexpr double kTolerance = 1.5;
constexpr std::string_view kVendorPrefix = "anthropic/";

bool has(const std::vector<std::string>& kinds, std::string_view kind) { return std::find(kinds.begin(), kinds.end(), kind) != kinds.end(); }

}  // namespace

std::string_view inferRuleId(InferRule rule) { return rule == InferRule::Shape ? "shape" : "ratio"; }

std::optional<double> claudeMultiplier(std::string_view plan, const std::optional<std::string>& tier) {
    if (plan == "claude_pro") return 1.0;
    for (const auto& p : kPlans) {
        if (p.plan == plan && p.tier.has_value() == tier.has_value() && (!tier || *p.tier == *tier)) return p.multiplier;
    }
    return std::nullopt;
}

bool usableForInference(const Estimate* estimate) {
    return estimate && (estimate->confidence == Confidence::High || estimate->confidence == Confidence::Medium) && estimate->cap && *estimate->cap != 0;
}

std::optional<InferredPlan> inferClaudePlan(const std::vector<std::string>& kinds, const Estimate* own, const std::vector<PlanAnchor>& anchors) {
    if (has(kinds, "sevenDay") && !has(kinds, "fiveHour")) return InferredPlan{"claude_ultra", std::nullopt, InferRule::Shape};
    if (!usableForInference(own)) return std::nullopt;
    const auto limit = std::log(kTolerance);
    std::optional<double> bestDistance;
    const KnownPlan* best = nullptr;
    for (const auto& anchor : anchors) {
        const auto m = claudeMultiplier(anchor.plan, anchor.tier);
        if (!m || !usableForInference(&anchor.estimate)) continue;
        const auto scaled = *m * *own->cap / *anchor.estimate.cap;
        for (const auto& candidate : kPlans) {
            const auto distance = std::fabs(std::log(scaled / candidate.multiplier));
            if (distance <= limit && (!bestDistance || distance < *bestDistance)) {
                bestDistance = distance;
                best = &candidate;
            }
        }
    }
    if (!best) return std::nullopt;
    return InferredPlan{std::string(best->plan), best->tier ? std::optional<std::string>(std::string(*best->tier)) : std::nullopt, InferRule::Ratio};
}

std::vector<PlanAnchor> planAnchors(const std::map<CapClass, Estimate>& estimates) {
    const auto fiveHour = windowId("fiveHour", std::nullopt, 300);
    std::vector<PlanAnchor> out;
    for (const auto& [cls, e] : estimates) {
        if (!cls.byPlan || cls.window != fiveHour || !cls.id.starts_with(kVendorPrefix)) continue;
        out.push_back({cls.id.substr(kVendorPrefix.size()), cls.tier, e});
    }
    std::stable_sort(out.begin(), out.end(), [](const PlanAnchor& a, const PlanAnchor& b) {
        const auto aTier = a.tier.value_or(""), bTier = b.tier.value_or("");
        return std::tie(a.plan, aTier) < std::tie(b.plan, bTier);
    });
    return out;
}

}  // namespace agentspeed::quota::estimate
