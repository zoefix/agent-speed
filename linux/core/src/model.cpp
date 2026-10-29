#include "agentspeed/model.hpp"

#include <array>

namespace agentspeed {

namespace {
constexpr std::array<std::string_view, 17> kSourceIds = {
    "codex", "claudeCode", "claudeDesktop", "deepseek", "opencode", "gemini", "omp", "antigravity", "workbuddy",
    "workbuddyAi", "zcode", "kimi", "grokBuild", "pi", "openclaw", "hermes", "droid"};
}

std::string_view sourceId(SourceKind source) { return kSourceIds[static_cast<std::size_t>(source)]; }

std::optional<SourceKind> sourceFromId(std::string_view id) {
    for (std::size_t i = 0; i < kSourceIds.size(); ++i) {
        if (kSourceIds[i] == id) return static_cast<SourceKind>(i);
    }
    return std::nullopt;
}

std::string_view phaseId(Phase phase) {
    switch (phase) {
        case Phase::Generating: return "generating";
        case Phase::Tools: return "tools";
        case Phase::Idle: break;
    }
    return "idle";
}

}  // namespace agentspeed
