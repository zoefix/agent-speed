#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <vector>

namespace agentspeed {

enum class LinkKind {
    Path,
    CodexThread,
    DshSession,
    OpenCodeSession,
    GeminiSession,
    OmpPath,
    AntigravityConversation,
    WorkBuddyPath,
    WorkBuddyAiPath,
    ZCodeSession,
    KimiSession,
    GrokBuildSession,
    PiPath,
    OpenClawWindow,
    DroidPath,
};

// What METRICS 6.7 needs from a tracked agent to find its parent.
struct AgentLink {
    std::string id;
    LinkKind kind = LinkKind::Path;
    std::optional<std::string> threadId;
    std::optional<std::string> parentThreadId;
    std::optional<std::int64_t> createdMs;
    int rank = 0;
};

namespace parent_links {

// METRICS 6.7 parent links (agent id → parent id); self references and cycles are dropped.
std::map<std::string, std::string> resolve(const std::vector<AgentLink>& agents);
// `<dir>/<session>/subagents/<name>.jsonl` → `<dir>/<session>.jsonl`.
std::optional<std::string> claudeParentPath(const std::string& path);

}  // namespace parent_links

}  // namespace agentspeed
