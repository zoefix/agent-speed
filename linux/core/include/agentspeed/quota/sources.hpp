#pragma once

#include <optional>
#include <span>
#include <string_view>

#include "agentspeed/model.hpp"

// QUOTAS 9.1 Routes and switches: the sources that have a quota, their switch (DESIGN 5 Storage) and its default.
namespace agentspeed::quota {

enum class Route : std::uint8_t {
    CodexRollouts,   // QUOTAS 3
    DesktopCache,    // QUOTAS 4
    StatusLine,      // QUOTAS 5
    LocalFile,       // Grok Build's log, Devin
    Loopback,        // Antigravity
    AgentSignIn,     // Kimi Code, Gemini CLI, Factory Droid
    UserKey,         // ZCode
    BrowserSession,  // Qoder, WorkBuddy
};

struct SourceRoute {
    SourceKind source;
    std::string_view switchKey;
    bool defaultOn;
    Route route;
};

// Every source with a quota, in source order (METRICS 1). Qoder IDE and Qoder CLI share `quotaQoder`; WorkBuddy AI has
// none (QUOTAS 9.9: the international edition's endpoint is unknown).
std::span<const SourceRoute> sourceRoutes();
const SourceRoute* routeOf(SourceKind source);
inline bool hasQuota(SourceKind source) { return routeOf(source) != nullptr; }

// Grok Build's live request (`quotaGrokBuildLive`, off), a second switch under `quotaGrokBuild`.
constexpr std::string_view kGrokLiveSwitch = "quotaGrokBuildLive";

}  // namespace agentspeed::quota
