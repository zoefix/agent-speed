#pragma once

#include <sys/types.h>

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

#include "agentspeed/quota/ordered_json.hpp"
#include "agentspeed/quota/paths.hpp"

// The Claude CLI's status line bridge (QUOTAS 5.1–5.4): installs the helper copy, points Claude Code's user settings
// at it and restores them, and tells its status. Off by default; nothing here runs until the user turns it on.
namespace agentspeed::quota::bridge {

// QUOTAS 5.4 and REMOTE 11.1 `state`.
enum class Status : std::uint8_t { Off, Waiting, Ok, Replaced, Unreadable, Failed };
std::string_view statusId(Status status);

// ---- The settings edits as text in, text out (QUOTAS 5.2–5.4).

// macOS and Linux: the absolute path in single quotes, `'` written as `'\''`.
std::string posixCommand(std::string_view path);
// A `statusLine` object with `type == "command"` and a `command` containing `agent-speed-statusline`.
bool isOurs(const ordered_json::Value* statusLine);

struct TurnOnEdit {
    // False: the file is not a JSON object (QUOTAS 5.2 step 1), change nothing.
    bool readable = false;
    std::optional<std::string> settings;  // the new file text
    // The sidecar to keep: `{"version": 1, "statusLine": <current>}` (or without it) unless the line is ours already,
    // when the existing sidecar stays.
    std::optional<ordered_json::Value> previous;
    bool wasOurs = false;
};
// `settings`: the file's text, nullopt when it does not exist (`{}`).
TurnOnEdit turnOnEdit(const std::optional<std::string>& settings, std::string_view command,
                      const std::optional<ordered_json::Value>& sidecar);
// The restored text while the line is still ours (the previous value back, or the member removed when the sidecar has
// none); nullopt leaves the file alone.
std::optional<std::string> turnOffEdit(const std::optional<std::string>& settings, const std::optional<ordered_json::Value>& previous);
// Waiting / Ok (ours, without / with a quota file), Replaced, Unreadable.
Status statusOf(const std::optional<std::string>& settings, bool hasQuotaFile);

// ---- On disk.

struct Paths {
    // `$CLAUDE_CONFIG_DIR/settings.json`, else `~/.claude/settings.json`.
    std::string settingsFile;
    // `<data>/quota`: sidecar, backup and quota file.
    std::string quotaFolder;
    // `<data>/bin/agent-speed-statusline` (a host: `<folder>/agent-speed-statusline`, REMOTE 11.3).
    std::string helperCopy;

    std::string quotaFile() const;
    std::string sidecar() const;
    std::string backup() const;

    // This computer's: `<data>` = dataFolder(environment).
    static Paths forComputer(const Environment& environment);
    static Paths forData(std::string settingsFile, const std::string& data);
};

enum class TurnOnResult : std::uint8_t {
    On,
    Unreadable,  // the settings file is not a JSON object: nothing was changed
    Failed,      // a file could not be read or written: the settings are as they were
};

class Bridge {
public:
    // `command` defaults to posixCommand(paths.helperCopy).
    explicit Bridge(Paths paths, std::optional<std::string> command = std::nullopt);

    const Paths& paths() const { return paths_; }
    const std::string& command() const { return command_; }

    // When the bridge is turned on, and at every launch while it is on: the helper shipped beside the app's executable
    // is copied to `helperCopy` (temporary file, then rename) only when the bytes differ. False without a helper.
    bool installHelper(const std::string& helperSource, mode_t mode = 0755) const;
    // QUOTAS 5.2: copies the helper, keeps the current status line in the sidecar and the settings in the backup (unless
    // the line is ours already), then points `statusLine` at the copy. A failure leaves the settings as they were.
    // `helperMode`: a host's copy is 0700 (REMOTE 11.3).
    TurnOnResult turnOn(const std::string& helperSource, mode_t helperMode = 0755) const;
    // QUOTAS 5.3: the previous status line back while ours is still there (a line the user set meanwhile stays), then
    // the sidecar, the backup and the quota file go. Allowed whatever the source's switches say. False when the settings
    // could not be written back: the sidecar and the backup then stay for another attempt.
    bool turnOff() const;
    // QUOTAS 5.4, checked every 60 s while on and when Settings opens.
    Status status() const;

private:
    enum class Read : std::uint8_t { Missing, Text, Unreadable, Failed };
    Read readSettings(std::string& text) const;
    std::optional<ordered_json::Value> readSidecar() const;
    bool writeSettings(const std::string& text) const;

    Paths paths_;
    std::string command_;
};

}  // namespace agentspeed::quota::bridge
