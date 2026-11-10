#pragma once

#include "agentspeed/model.hpp"

namespace agentspeed {

// The families of roots of METRICS 2; each also names one location list of METRICS 2.1 (Claude Code and Claude
// Desktop share `Claude`).
enum class RootFamily {
    Codex,
    Claude,
    DeepSeek,
    Gemini,
    Omp,
    Antigravity,
    WorkBuddy,
    WorkBuddyAi,
    OpenCode,
    ZCode,
    Kimi,
    GrokBuild,
    Pi,
    OpenClaw,
    Hermes,
    Droid,
    Devin,
    Qoder,
    QoderCli,
};

RootFamily familyOf(SourceKind source);

}  // namespace agentspeed
