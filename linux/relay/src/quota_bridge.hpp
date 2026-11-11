#pragma once

#include <optional>
#include <string>

#include "agentspeed/quota/bridge.hpp"
#include "agentspeed/quota/paths.hpp"
#include "agentspeed/remote_messages.hpp"

// REMOTE 11.3: the Claude Code status line bridge of a host. The relay copies itself to `<folder>/agent-speed-statusline`
// and points the host's Claude Code settings at that copy exactly as QUOTAS 5.2–5.4 say; the sidecar, the backup and
// the quota file live in `<folder>/quota/`.
namespace agentspeed::relay {

class HostBridge {
public:
    using State = remote::HostQuotaBridge::State;

    // `folder`: the relay's folder (absolute); `self`: the relay's own executable.
    HostBridge(const std::string& folder, const quota::Environment& environment, std::string self);

    // The app's `claudeBridge` from one config: turning it on runs QUOTAS 5.2, turning it off 5.3. The session's first
    // config only installs the helper copy when the bridge was left on (its sidecar is there), turns it on otherwise,
    // and when off restores only a status line this folder's bridge set. Returns the state to report.
    State apply(bool on, bool first);
    // While on: QUOTAS 5.4 (every 60 s, and with every config).
    State check();
    // The settings file with `~` for the home (REMOTE 11.1 `settingsPath`).
    const std::string& settingsPath() const { return settingsPath_; }
    const quota::bridge::Paths& paths() const { return bridge_.paths(); }
    bool on() const { return on_; }

private:
    State turnOn();
    State turnOff();
    bool leftOn() const;

    quota::bridge::Bridge bridge_;
    std::string self_;
    std::string settingsPath_;
    bool on_ = false;
    std::optional<State> failure_;
};

// `'<folder>/agent-speed-statusline' --folder '<folder>'`.
std::string helperCommand(const std::string& folder);
// The folder made absolute against the working folder (it is already, from the remote shell).
std::string absoluteFolder(const std::string& folder);

}  // namespace agentspeed::relay
