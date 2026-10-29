#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/model.hpp"

namespace agentspeed::engine {

// The engine's view of a response (METRICS 3.3, 6) and of an agent (6.7).
struct Response {
    std::string key;
    std::string agentId;
    SourceKind source = SourceKind::Codex;
    std::optional<std::string> model;
    std::optional<std::int64_t> startMs;
    std::int64_t endMs = 0;
    std::int64_t input = 0;
    std::int64_t output = 0;
    std::int64_t reasoning = 0;
    bool final = true;
    std::optional<std::int64_t> costMicroUsd;
    std::int64_t requestCount = 1;
};

struct Agent {
    std::string id;
    SourceKind source = SourceKind::Codex;
    std::optional<std::string> model;
    Phase phase = Phase::Idle;
    std::optional<std::int64_t> inFlightStartMs;
    std::optional<std::int64_t> lastLineMs;
    std::optional<std::string> parentId;
};

constexpr std::int64_t kWindowMs = 3000;

// The widened valid interval of METRICS 3.3, or nullopt.
std::optional<std::pair<std::int64_t, std::int64_t>> interval(const Response& r);

struct Speed {
    double output = 0;
    double total = 0;
};

// METRICS 6.2: the agent's recent speed, else the model's, else the source's; nullopt without a reference.
std::optional<Speed> estimatedSpeed(const std::vector<const Response*>& valid, std::int64_t now, const Agent& agent);

struct LiveRate {
    double output = 0;
    double total = 0;
    double estimatedShare = 0;
};
LiveRate liveRate(const std::vector<Response>& responses, const std::vector<Agent>& agents, std::int64_t now);

struct LiveCost {
    double microUsdPerSecond = 0;
    double knownShare = 1;
};
LiveCost liveCost(const std::vector<Response>& responses, const std::vector<Agent>& agents, std::int64_t now);

struct AgentList {
    std::vector<std::string> order;
    std::map<std::string, std::string> parents;  // only agents whose tree parent is listed
};
AgentList agentList(const std::vector<Response>& responses, const std::vector<Agent>& agents, std::int64_t now);

// METRICS 6.5: measured output per one-second bucket start in [first, last].
std::map<std::int64_t, double> buckets(const std::vector<Response>& responses, std::int64_t first, std::int64_t last);

struct TodayStats {
    std::int64_t requests = 0;
    std::int64_t output = 0;
    std::int64_t input = 0;
    std::int64_t unfinalized = 0;
    std::optional<double> average;
    double peak = 0;
    std::map<std::int64_t, double> buckets;
    std::int64_t costMicroUsd = 0;
    std::int64_t unpricedRequests = 0;
};
TodayStats todayStats(const std::vector<Response>& responses, std::int64_t todayStart, std::int64_t now);

// METRICS 6.9 Live thinking: the weighted sums of the generating output tokens of the 3-second window and their
// reasoning part, for the whole set, per agent and per raw model ("" when unknown).
struct ThinkingWindow {
    double weight = 0, reasoning = 0, reasoningWeight = 0;
    void add(SourceKind source, double w, double r);
    void merge(const ThinkingWindow& other);
    double liveThinking() const { return reasoning / (static_cast<double>(kWindowMs) / 1000.0); }
    double thinkingShare() const { return reasoningWeight > 0 ? reasoning / reasoningWeight : 0.0; }
};
struct Thinking {
    ThinkingWindow total;
    std::map<std::string, ThinkingWindow> perAgent;
    std::map<std::string, ThinkingWindow> perModel;
};
Thinking thinking(const std::vector<Response>& responses, const std::vector<Agent>& agents, std::int64_t now);

// DESIGN 2c groups: every root (no parent, or a parent that is not listed) with its descendants, the root first and
// then depth first in list order, keyed by the root's id; roots in list order.
std::vector<std::pair<std::string, std::vector<std::string>>> boardGroups(const std::vector<Agent>& agents);

}  // namespace agentspeed::engine
