#pragma once

#include <functional>
#include <optional>
#include <string>

// METRICS 4.20: where CodeBuddy Code, the standalone CLI that writes WorkBuddy's transcript format (4.9), keeps its data.
namespace agentspeed::codebuddy {

using Environment = std::function<std::optional<std::string>(const std::string&)>;

// `$CODEBUDDY_CONFIG_DIR` when the variable is not blank (Python whitespace), used as it is; else `~/.codebuddy`.
std::string configFolder(const Environment& environment, const std::string& home);
// The automatic entry's root: the config folder's `projects`.
std::string automaticRoot(const Environment& environment, const std::string& home);
// A user location (a config folder or its `projects` folder) read as that `projects` folder.
std::string projectsOf(const std::string& location);
// The config folder that holds a root's `projects`, whose `settings.json` gives the configured effort.
std::string configFolderOf(const std::string& root);

}  // namespace agentspeed::codebuddy
