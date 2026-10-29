#include "agentspeed/parents.hpp"

#include <set>

namespace agentspeed {

namespace {

bool isSeparator(char c) { return c == '/' || c == '\\'; }

// `<dir>/<session>/subagents/<name>.jsonl` → `<dir>/<session>.jsonl`.
std::optional<std::string> claudeParentPath(const std::string& path) {
    const auto nameStart = path.find_last_of("/\\");
    if (nameStart == std::string::npos) return std::nullopt;
    const auto subagents = path.substr(0, nameStart);
    const auto subStart = subagents.find_last_of("/\\");
    if (subStart == std::string::npos || subagents.substr(subStart + 1) != "subagents") return std::nullopt;
    const auto session = subagents.substr(0, subStart);
    if (session.empty() || isSeparator(session.back())) return std::nullopt;
    return session + ".jsonl";
}

}  // namespace

std::map<std::string, std::optional<std::string>> resolveParents(const std::vector<ParentFacts>& agents) {
    std::map<std::string, std::optional<std::string>> parents;
    std::set<std::string> tracked;
    for (const auto& a : agents) tracked.insert(a.agentId);
    for (const auto& a : agents) {
        std::optional<std::string> parent;
        if (a.source == SourceKind::ClaudeCode || a.source == SourceKind::ClaudeDesktop) {
            if (auto candidate = claudeParentPath(a.agentId); candidate && tracked.contains(*candidate)) parent = candidate;
        } else if (a.source == SourceKind::Codex && a.codexParentThreadId) {
            const ParentFacts* best = nullptr;
            for (const auto& b : agents) {
                if (b.source != SourceKind::Codex || b.codexThreadId != a.codexParentThreadId) continue;
                if (!best || b.createdMs.value_or(0) < best->createdMs.value_or(0) ||
                    (b.createdMs.value_or(0) == best->createdMs.value_or(0) && b.agentId < best->agentId)) {
                    best = &b;
                }
            }
            if (best) parent = best->agentId;
        }
        parents[a.agentId] = parent && *parent != a.agentId ? parent : std::nullopt;
    }
    for (auto& [id, parent] : parents) {
        std::set<std::string> seen;
        std::optional<std::string> current = id;
        while (current && !seen.contains(*current)) {
            seen.insert(*current);
            auto it = parents.find(*current);
            current = it == parents.end() ? std::nullopt : it->second;
        }
        if (current) parent.reset();
    }
    return parents;
}

}  // namespace agentspeed
