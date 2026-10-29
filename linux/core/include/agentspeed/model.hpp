#pragma once

#include <algorithm>
#include <cstdint>
#include <optional>
#include <string>

#include "agentspeed/modes.hpp"
#include <string_view>

namespace agentspeed {

// METRICS 1: the append-only source order.
enum class SourceKind : std::uint8_t {
    Codex, ClaudeCode, ClaudeDesktop, Deepseek, OpenCode, Gemini, Omp, Antigravity, WorkBuddy, WorkBuddyAi, ZCode,
    Kimi, GrokBuild, Pi, OpenClaw, Hermes, Droid,
};

std::string_view sourceId(SourceKind source);
std::optional<SourceKind> sourceFromId(std::string_view id);

enum class Phase : std::uint8_t { Idle, Generating, Tools };
std::string_view phaseId(Phase phase);

// METRICS 3.2: normalized token usage; every value is capped at 10^12.
struct TokenCounts {
    static constexpr std::int64_t kMax = 1'000'000'000'000;

    std::int64_t input = 0;
    std::int64_t cachedInput = 0;
    std::int64_t cacheWriteInput = 0;
    std::int64_t output = 0;
    std::int64_t reasoning = 0;
    std::int64_t cacheWrite1hInput = 0;

    std::int64_t total() const { return input + output; }

    static TokenCounts max(const TokenCounts& a, const TokenCounts& b) {
        return {std::max(a.input, b.input), std::max(a.cachedInput, b.cachedInput),
                std::max(a.cacheWriteInput, b.cacheWriteInput), std::max(a.output, b.output),
                std::max(a.reasoning, b.reasoning), std::max(a.cacheWrite1hInput, b.cacheWrite1hInput)};
    }
    static std::int64_t saturatingAdd(std::int64_t a, std::int64_t b) { return std::min(kMax, a + b); }
    bool operator==(const TokenCounts&) const = default;
};

// One response upsert produced by a parser (METRICS 4).
struct ParsedResponse {
    std::string key;
    SourceKind source = SourceKind::Codex;
    std::string agentId;
    std::optional<std::string> model;
    std::optional<std::int64_t> startMs;
    std::int64_t endMs = 0;
    TokenCounts tokens;
    bool final = true;
    std::optional<std::string> provider;
    bool fast = false;
    std::int64_t requestCount = 1;
    ExecutionModes modes;
};

class ResponseSink {
public:
    virtual ~ResponseSink() = default;
    // isCompletion: true for the first emission of a key, false for later updates.
    virtual void onResponse(const ParsedResponse& response, bool isCompletion) = 0;
};

}  // namespace agentspeed
