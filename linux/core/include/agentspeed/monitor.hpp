#pragma once

#include <cstdint>
#include <filesystem>
#include <map>
#include <memory>
#include <string>
#include <vector>

#include "agentspeed/dedupe.hpp"
#include "agentspeed/engine.hpp"
#include "agentspeed/line_splitter.hpp"
#include "agentspeed/snapshot.hpp"
#include "agentspeed/transcript_parser.hpp"

namespace agentspeed {

struct MonitorConfig {
    std::vector<std::filesystem::path> codexRoots;
    std::vector<std::filesystem::path> claudeRoots;

    // METRICS 2: $CODEX_HOME/sessions or ~/.codex/sessions; $CLAUDE_CONFIG_DIR/projects or ~/.claude/projects.
    static MonitorConfig fromEnvironment();
};

// Discovers transcripts, reads them as METRICS 5 says and keeps the responses the engine needs.
class Monitor {
public:
    explicit Monitor(MonitorConfig config);
    ~Monitor();

    // Startup: discovery and the backlog of files changed since todayStart − 15 min.
    void start(std::int64_t nowMs, std::int64_t todayStartMs);
    // Change detection (call about once a second); rediscovers every 10 seconds.
    void poll(std::int64_t nowMs, std::int64_t todayStartMs);
    Snapshot snapshot(std::int64_t nowMs, std::int64_t todayStartMs) const;
    // responseCompleted events since the last call (METRICS 6.4); never the startup backlog.
    std::vector<Completion> takeCompletions();

    std::size_t trackedFiles() const { return files_.size(); }

private:
    struct Tracked;
    class Store;

    void discover(std::int64_t nowMs, std::int64_t todayStartMs, bool startup);
    void track(const std::filesystem::path& path, SourceKind family, std::int64_t todayStartMs, bool startup);
    void readAppended(Tracked& file);
    void readWhole(Tracked& file);
    void prune(std::int64_t nowMs, std::int64_t todayStartMs);

    MonitorConfig config_;
    Deduper deduper_;
    std::unique_ptr<Store> store_;
    std::map<std::string, std::unique_ptr<Tracked>> files_;
    std::int64_t lastDiscoveryMs_ = 0;
};

}  // namespace agentspeed
