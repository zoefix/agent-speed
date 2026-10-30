#pragma once

#include <map>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/modes.hpp"

// Values only a config file holds (EXECUTION-MODES, Configured values). They reach an agent's current values and nothing
// else. Files are regular, at most 1 MiB, UTF-8 (one leading U+FEFF removed); only the named key is taken, and nothing
// else of a file is kept or logged.
namespace agentspeed::configured {

// `<home>/.gemini/settings.json`: the effort of each model an override names.
struct GeminiEfforts {
    std::map<std::string, ExecutionModes> byModel;
    ExecutionModes modes(const std::optional<std::string>& model) const;
    bool operator==(const GeminiEfforts&) const = default;
};

std::string codexConfigPath(const std::string& sessionsRoot);
ExecutionModes codex(const std::string& path);
// `userSettings.vertexServiceTier` of `config/config.json` in the folder that holds an Antigravity store.
ExecutionModes antigravity(const std::string& path);
// `reasoningEffort` of a WorkBuddy data folder's `settings.json`.
ExecutionModes workBuddy(const std::string& path);
GeminiEfforts gemini(const std::string& path);

}  // namespace agentspeed::configured

// Session-current facts (EXECUTION-MODES, Current values): a session's latest value, overwritten in place, held apart
// from every response. One short read of another program's database: a regular file opened read-only with the rules of
// METRICS 4.5, closed right after the read.
namespace agentspeed::session_modes {

// `workbuddy.db` `sessions.thought_level` of each session id. Only these two columns are ever selected.
std::map<std::string, ExecutionModes> workBuddy(const std::string& databasePath, const std::vector<std::string>& sessions);
// `state.db` `sessions.model_config.reasoning_config` of each session id: `enabled: false` is `off`, else its `effort`.
std::map<std::string, ExecutionModes> hermes(const std::string& databasePath, const std::vector<std::string>& sessions);

}  // namespace agentspeed::session_modes
