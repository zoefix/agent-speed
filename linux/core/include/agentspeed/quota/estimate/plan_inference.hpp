#pragma once

#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/quota/estimate/vote.hpp"

// QUOTAS 10.2 Inferred plan: the plan of an account Claude Desktop spends on as its own, from its windows and caps
// (Desktop names no plan, and its credentials stay unread).
namespace agentspeed::quota::estimate {

enum class InferRule { Shape, Ratio };
std::string_view inferRuleId(InferRule rule);

struct InferredPlan {
    std::string plan;
    std::optional<std::string> tier;
    InferRule rule = InferRule::Shape;
    bool operator==(const InferredPlan&) const = default;
};

// A 5-hour cap class of a Claude plan.
struct PlanAnchor {
    std::string plan;
    std::optional<std::string> tier;
    Estimate estimate;
};

// The 5-hour usage of a Claude plan relative to Pro: Pro of any tier 1, Max only with a known tier.
std::optional<double> claudeMultiplier(std::string_view plan, const std::optional<std::string>& tier);
// A cap at confidence medium or high.
bool usableForInference(const Estimate* estimate);
// `kinds`: the window kinds of the account's newest snapshot; `own`: its 5-hour estimate (null without one);
// `anchors` sorted by plan, then tier with null first.
std::optional<InferredPlan> inferClaudePlan(const std::vector<std::string>& kinds, const Estimate* own, const std::vector<PlanAnchor>& anchors);
// Every 5-hour class of a Claude plan (`anthropic/<plan>`), in that order.
std::vector<PlanAnchor> planAnchors(const std::map<CapClass, Estimate>& estimates);

}  // namespace agentspeed::quota::estimate
