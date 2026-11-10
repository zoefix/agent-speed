#include "agentspeed/quota/paths.hpp"

#include <cstdlib>

namespace agentspeed::quota {

namespace {

std::optional<std::string> variable(const Environment& environment, const std::string& name) {
    if (!environment) return std::nullopt;
    auto value = environment(name);
    if (!value || value->empty()) return std::nullopt;
    return value;
}

std::string joined(std::string_view folder, std::string_view name) {
    std::string out(folder);
    while (out.size() > 1 && out.back() == '/') out.pop_back();
    if (out.empty() || out.back() != '/') out += '/';
    out += name;
    return out;
}

// A leading `~` or `~/` is the home folder.
std::string expanded(const std::string& path, const Environment& environment) {
    if (path == "~") return homeFolder(environment);
    if (path.rfind("~/", 0) == 0) return joined(homeFolder(environment), path.substr(2));
    return path;
}

// XDG base directories must be absolute; anything else is ignored (XDG Base Directory Specification).
std::optional<std::string> xdgFolder(const Environment& environment, const std::string& name) {
    auto value = variable(environment, name);
    if (!value || value->front() != '/') return std::nullopt;
    return value;
}

}  // namespace

Environment processEnvironment() {
    return [](const std::string& name) -> std::optional<std::string> {
        const char* value = std::getenv(name.c_str());
        if (!value) return std::nullopt;
        return std::string(value);
    };
}

std::string homeFolder(const Environment& environment) { return variable(environment, "HOME").value_or("/"); }

std::string dataFolder(const Environment& environment) {
    if (auto xdg = xdgFolder(environment, "XDG_DATA_HOME")) return joined(*xdg, "agent-speed");
    return joined(homeFolder(environment), ".local/share/agent-speed");
}

std::string quotaFolderOf(std::string_view data) { return joined(data, "quota"); }

std::string helperCopyOf(std::string_view data) { return joined(joined(data, "bin"), kHelperName); }

std::string claudeConfigFolder(const Environment& environment) {
    if (auto folder = variable(environment, "CLAUDE_CONFIG_DIR")) return expanded(*folder, environment);
    return joined(homeFolder(environment), ".claude");
}

std::string claudeSettingsFile(const Environment& environment) { return joined(claudeConfigFolder(environment), "settings.json"); }

std::string claudeAccountFile(const Environment& environment) {
    if (auto folder = variable(environment, "CLAUDE_CONFIG_DIR")) return joined(expanded(*folder, environment), ".claude.json");
    return joined(homeFolder(environment), ".claude.json");
}

std::string codexAuthFile(const Environment& environment) {
    if (auto home = variable(environment, "CODEX_HOME")) return joined(expanded(*home, environment), "auth.json");
    return joined(homeFolder(environment), ".codex/auth.json");
}

std::string desktopCacheFolder(const Environment& environment) {
    if (auto xdg = xdgFolder(environment, "XDG_CONFIG_HOME")) return joined(*xdg, "Claude/Cache/Cache_Data");
    return joined(homeFolder(environment), ".config/Claude/Cache/Cache_Data");
}

}  // namespace agentspeed::quota
