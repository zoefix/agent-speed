#include "agentspeed/codebuddy.hpp"

#include "agentspeed/fs_util.hpp"
#include "agentspeed/path_text.hpp"
#include "agentspeed/text.hpp"

namespace agentspeed::codebuddy {

namespace {

constexpr const char* kProjects = "projects";

}  // namespace

std::string configFolder(const Environment& environment, const std::string& home) {
    if (auto value = environment("CODEBUDDY_CONFIG_DIR"); value && text::hasVisibleText(*value)) return *value;
    return fsx::join(home, ".codebuddy");
}

std::string automaticRoot(const Environment& environment, const std::string& home) {
    return fsx::join(configFolder(environment, home), kProjects);
}

std::string projectsOf(const std::string& location) {
    auto folder = fsx::trimTrailing(location);
    if (folder.empty()) folder = location;
    return path_text::basename(folder) == kProjects ? folder : fsx::join(folder, kProjects);
}

std::string configFolderOf(const std::string& root) { return fsx::parentOf(fsx::trimTrailing(root)); }

}  // namespace agentspeed::codebuddy
