#include "agentspeed/parents.hpp"

#include <algorithm>
#include <set>

namespace agentspeed::parent_links {

namespace {

std::string folderOf(const std::string& path) {
    const auto cut = path.find_last_of("/\\");
    return cut == std::string::npos ? std::string() : path.substr(0, cut);
}

bool isPathKind(LinkKind kind) {
    return kind == LinkKind::Path || kind == LinkKind::OmpPath || kind == LinkKind::PiPath ||
           kind == LinkKind::WorkBuddyPath || kind == LinkKind::WorkBuddyAiPath || kind == LinkKind::DroidPath;
}

bool createdBefore(const AgentLink& a, const AgentLink& b) {
    if (a.rank != b.rank) return a.rank < b.rank;
    const auto ta = a.createdMs.value_or(INT64_MAX), tb = b.createdMs.value_or(INT64_MAX);
    if (ta != tb) return ta < tb;
    return a.id < b.id;
}

// Copies of one conversation in several stores: the one in the child's own folder first, then the ordinal path.
const AgentLink& preferredCopy(const std::vector<const AgentLink*>& copies, const std::string& child, bool group) {
    auto folder = [group](const std::string& p) { return group ? folderOf(folderOf(p)) : folderOf(p); };
    const std::string want = folder(child);
    const AgentLink* best = nullptr;
    for (const auto* copy : copies) {
        if (!best) {
            best = copy;
            continue;
        }
        const bool same = folder(copy->id) == want;
        const bool currentSame = folder(best->id) == want;
        if (same != currentSame ? same : copy->id < best->id) best = copy;
    }
    return *best;
}

void dropCycles(std::map<std::string, std::string>& parents) {
    std::vector<std::string> ids;
    for (const auto& [id, _] : parents) ids.push_back(id);
    for (const auto& id : ids) {
        std::set<std::string> seen;
        std::optional<std::string> current = id;
        while (current && seen.insert(*current).second) {
            const auto it = parents.find(*current);
            current = it == parents.end() ? std::nullopt : std::optional<std::string>(it->second);
        }
        if (current) parents.erase(id);
    }
}

}  // namespace

std::optional<std::string> claudeParentPath(const std::string& path) {
    const auto name = path.find_last_of("/\\");
    if (name == std::string::npos) return std::nullopt;
    const std::string folder = path.substr(0, name);
    const auto start = folder.find_last_of("/\\");
    if (start == std::string::npos || folder.substr(start + 1) != "subagents") return std::nullopt;
    return folder.substr(0, start) + ".jsonl";
}

std::map<std::string, std::string> resolve(const std::vector<AgentLink>& agents) {
    std::map<std::string, const AgentLink*> byPath;
    std::map<std::pair<LinkKind, std::string>, const AgentLink*> byThread;
    std::map<std::string, std::vector<const AgentLink*>> conversations, grokSessions;
    for (const auto& agent : agents) {
        byPath.try_emplace(agent.id, &agent);
        const bool hasThread = agent.threadId && !agent.threadId->empty();
        if (agent.kind == LinkKind::AntigravityConversation) {
            if (hasThread) conversations[*agent.threadId].push_back(&agent);
        } else if (agent.kind == LinkKind::GrokBuildSession && hasThread) {
            grokSessions[*agent.threadId].push_back(&agent);
        } else if (!isPathKind(agent.kind) && hasThread) {
            const auto key = std::make_pair(agent.kind, *agent.threadId);
            const auto known = byThread.find(key);
            if (known == byThread.end() || createdBefore(agent, *known->second)) byThread[key] = &agent;
        }
    }
    std::map<std::string, std::string> parents;
    for (const auto& agent : agents) {
        std::optional<std::string> parent;
        const bool hasParent = agent.parentThreadId && !agent.parentThreadId->empty();
        switch (agent.kind) {
            case LinkKind::Path:
                if (const auto candidate = claudeParentPath(agent.id)) {
                    if (const auto it = byPath.find(*candidate); it != byPath.end()) parent = it->second->id;
                }
                break;
            case LinkKind::OmpPath:
            case LinkKind::PiPath:
            case LinkKind::DroidPath:
            case LinkKind::WorkBuddyPath:
            case LinkKind::WorkBuddyAiPath:
                if (agent.parentThreadId) {
                    const auto it = byPath.find(*agent.parentThreadId);
                    if (it != byPath.end() && it->second->kind == agent.kind) parent = it->second->id;
                }
                break;
            case LinkKind::GrokBuildSession:
                if (hasParent) {
                    if (const auto it = grokSessions.find(*agent.parentThreadId); it != grokSessions.end()) {
                        parent = preferredCopy(it->second, agent.id, true).id;
                    }
                }
                break;
            case LinkKind::AntigravityConversation:
                if (hasParent) {
                    if (const auto it = conversations.find(*agent.parentThreadId); it != conversations.end()) {
                        parent = preferredCopy(it->second, agent.id, false).id;
                    }
                }
                break;
            default:
                if (hasParent) {
                    const auto it = byThread.find({agent.kind, *agent.parentThreadId});
                    if (it != byThread.end()) parent = it->second->id;
                }
                break;
        }
        if (parent && *parent != agent.id) parents[agent.id] = *parent;
    }
    dropCycles(parents);
    return parents;
}

}  // namespace agentspeed::parent_links
