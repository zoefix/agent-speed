#pragma once

#include <functional>
#include <optional>
#include <string>
#include <string_view>

// Where the quota routes of phase 1 read and write on Linux (QUOTAS 4.1, 5.1, 6), from an injected environment so tests
// and the relay can point them anywhere. Plain string work: the status line helper links it.
namespace agentspeed::quota {

// Reads one environment variable; nullopt or empty means unset. The same type as agentspeed::Environment.
using Environment = std::function<std::optional<std::string>(const std::string&)>;
Environment processEnvironment();

constexpr std::string_view kHelperName = "agent-speed-statusline";
constexpr std::string_view kQuotaFileName = "claude-cli.json";
constexpr std::string_view kSidecarFileName = "claude-statusline-previous.json";
constexpr std::string_view kBackupFileName = "claude-settings.backup.json";

// `$HOME`, else `/`.
std::string homeFolder(const Environment& environment);
// QUOTAS 5.1 `<data>`: `$XDG_DATA_HOME/agent-speed` (an absolute path), else `~/.local/share/agent-speed`.
std::string dataFolder(const Environment& environment);
// `<data>/quota` and `<data>/bin/agent-speed-statusline`.
std::string quotaFolderOf(std::string_view data);
std::string helperCopyOf(std::string_view data);
// `$CLAUDE_CONFIG_DIR`, else `~/.claude`; a leading `~/` is the home folder.
std::string claudeConfigFolder(const Environment& environment);
// QUOTAS 5.1: Claude Code's user settings, `<claude config>/settings.json`.
std::string claudeSettingsFile(const Environment& environment);
// QUOTAS 6: `$CLAUDE_CONFIG_DIR/.claude.json`, else `~/.claude.json`.
std::string claudeAccountFile(const Environment& environment);
// QUOTAS 6: `$CODEX_HOME/auth.json`, else `~/.codex/auth.json`.
std::string codexAuthFile(const Environment& environment);
// QUOTAS 4.1 (Linux, unverified): `$XDG_CONFIG_HOME/Claude/Cache/Cache_Data`, else `~/.config/Claude/Cache/Cache_Data`.
std::string desktopCacheFolder(const Environment& environment);

}  // namespace agentspeed::quota
