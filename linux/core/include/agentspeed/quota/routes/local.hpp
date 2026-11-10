#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/quota/routes/environment.hpp"
#include "agentspeed/quota/vendors/parsers.hpp"
#include "agentspeed/quota/window.hpp"

// QUOTAS 9.2 (log) and 9.3: the local routes. No network; each file is read again only when it changed.
namespace agentspeed::quota {

// Grok Build's `$GROK_HOME/logs/unified.jsonl`, else `~/.grok/logs/unified.jsonl`: its last 2 MiB, the first, cut
// line dropped.
class GrokLogReader {
public:
    static constexpr std::size_t kTailBytes = 2u << 20;
    static std::string defaultFile(const VendorEnvironment& env);

    // True when the snapshot changed.
    bool refresh(const VendorEnvironment& env);
    const std::optional<Snapshot>& snapshot() const { return snapshot_; }

private:
    std::optional<std::pair<std::int64_t, std::int64_t>> stamp_;
    std::string file_;
    std::optional<Snapshot> snapshot_;
};

// The tail's lines; the first is dropped when the tail does not start at the file's start.
std::vector<std::string_view> tailLines(std::string_view tail, bool fromStart);

// Devin Desktop's cached plan: the newest of `~/.config/Devin`, `~/.config/Windsurf` ($XDG_CONFIG_HOME respected)
// that has `User/globalStorage/state.vscdb`, queried read-only and immutable, timed by its newest launch folder.
class DevinPlanReader {
public:
    static std::vector<std::string> defaultFolders(const VendorEnvironment& env);
    static std::string database(const std::string& folder);
    // `windsurf.reactSettings.cachedPlanInfoData%`, else `windsurf.settings.cachedPlanInfo%`.
    static std::vector<vendors::DevinRow> rows(const std::string& path);

    bool refresh(const VendorEnvironment& env);
    const std::optional<Snapshot>& snapshot() const { return snapshot_; }

private:
    std::vector<std::int64_t> stamp_;
    std::optional<Snapshot> snapshot_;
};

}  // namespace agentspeed::quota
