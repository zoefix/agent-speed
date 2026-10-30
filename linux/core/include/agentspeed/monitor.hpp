#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <set>
#include <string>
#include <vector>

#include "agentspeed/dedupe.hpp"
#include "agentspeed/discovery.hpp"
#include "agentspeed/engine.hpp"
#include "agentspeed/locations.hpp"
#include "agentspeed/actions.hpp"
#include "agentspeed/mode_context.hpp"
#include "agentspeed/snapshot.hpp"
#include "agentspeed/tailer.hpp"
#include "agentspeed/report_perf.hpp"
#include "agentspeed/traffic_service.hpp"

namespace agentspeed {

namespace opencode {
class Source;
struct Update;
}
namespace zcode {
class Source;
struct Update;
}
namespace openclaw {
class Source;
struct Update;
}
namespace hermes {
class Source;
struct Update;
}
class TimeZone;
namespace pricing {
class PriceBook;
}

struct MonitorConfig {
    std::set<SourceKind> enabled = allSources();
    SourceLocations locations;
    // Null: a book with the bundled table only.
    std::shared_ptr<pricing::PriceBook> prices;

    static std::set<SourceKind> allSources();
    // METRICS 2 roots from the process environment and the user's folder settings.
    static MonitorConfig fromEnvironment(const RootSettings& settings = {}, std::set<SourceKind> enabled = allSources());
};

// What the engine knows about one agent (METRICS 6.7).
struct AgentInfo {
    std::string id;
    SourceKind source = SourceKind::Codex;
    std::optional<std::string> project, model;
    bool isSubagent = false;
    Phase phase = Phase::Idle;
    std::optional<std::int64_t> inFlightStartMs, lastLineMs;
    ExecutionModes modes;
    // A session-current fact and configured values (EXECUTION-MODES, Current values).
    ExecutionModes sessionModes, configuredModes;
};

// Discovers every enabled source, reads it as METRICS 5 says and keeps what the engine needs. Single-threaded.
class Monitor {
public:
    explicit Monitor(MonitorConfig config);
    ~Monitor();
    Monitor(const Monitor&) = delete;
    Monitor& operator=(const Monitor&) = delete;

    // Startup: discovery and the backlog of files changed since todayStart − 15 min.
    void start(std::int64_t nowMs, std::int64_t todayStartMs);
    // Change detection (about once a second): recent files and the databases; a full rediscovery every 10 seconds.
    // True when a read changed the engine (an observation of METRICS 11.9).
    bool poll(std::int64_t nowMs, std::int64_t todayStartMs);
    Snapshot snapshot(std::int64_t nowMs, std::int64_t todayStartMs);
    // responseCompleted events since the last call (METRICS 6.4); never the startup backlog.
    std::vector<Completion> takeCompletions();

    std::size_t trackedFiles() const { return files_.size(); }
    int sessionCount(SourceKind kind, std::int64_t todayStartMs) const;
    // Pi's new custom session folder covers an omp root: the caller should start a new monitor.
    bool restartRequested() const { return restartRequested_; }
    const std::map<std::string, AgentInfo>& agents() const { return agents_; }
    // An agent's parent link (METRICS 6.7) as resolved now.
    std::optional<std::string> parentOf(const std::string& agentId);
    // Every response held (today and the retention window), latest version per key.
    std::vector<ParsedResponse> responses() const;
    // A newer price table arrived: today's unpriced responses are priced again.
    void repriceToday();
    // 比平常 (METRICS 6.11 Baseline): the 14-day pooled score of each (source, raw model), as the history store last
    // saved it.
    void setPerfBaselines(std::map<std::pair<SourceKind, std::string>, double> baselines) { perfBaselines_ = std::move(baselines); }
    // METRICS 10: sampled on every poll while enabled.
    void setTrafficEnabled(bool enabled) { traffic_->setEnabled(enabled); }
    std::vector<traffic::HistoryRow> trafficToday(std::int64_t nowMs) const { return traffic_->todayRows(nowMs); }
    void restoreTraffic(const std::vector<traffic::HistoryRow>& rows) { traffic_->restore(rows); }
    // METRICS 11.10 Watch: completions read while it is on are counted; `responses` and `calls` are how many of this
    // generation's the reports already took.
    void setPerfWatch(bool on, std::int64_t responses, std::int64_t calls) {
        perfLedger_.setWatch(on);
        perfLedger_.setTaken(responses, calls);
    }
    // The engine instance live reporting's counters belong to (METRICS 11.4).
    std::int64_t epoch() const { return epoch_; }
    // The source sets whose 性能 and 思考 every snapshot computes (nullopt: every enabled source).
    void setScoreSets(std::vector<std::optional<std::vector<SourceKind>>> sets) { scoreSets_ = std::move(sets); }
    // The source sets of the floating orbs whose peakToday_A every snapshot computes (nullopt: peakToday).
    void setPeakSets(std::vector<std::optional<std::vector<SourceKind>>> sets) { peakSets_ = std::move(sets); }

private:
    struct Root {
        RootFamily family;
        std::string path;
        std::string detectionPath;
        bool enabled = false;
        bool available = false;
        bool seeded = false;
    };
    struct Tracked {
        std::size_t root = 0;
        std::unique_ptr<Tailer> tailer;
        std::unique_ptr<TranscriptParser> parser;
        std::unique_ptr<work::ActionReader> actions;
        std::int64_t knownLength = -1, knownWriteNs = 0;
        std::uint64_t pushedVersion = UINT64_MAX;
        bool unreadable = false, dirty = false;
    };
    struct Plan {
        Tracked* file;
        std::int64_t writeNs;
        std::int64_t startOffset;
        Alignment alignment;
    };
    class Store;

    void addRoot(RootFamily family, const std::string& path);
    bool familyEnabled(RootFamily family) const;
    void seedRoots(std::int64_t todayStartMs);
    std::optional<Plan> planInitialRead(Tracked& file, const discovery::DiscoveredFile& found, std::int64_t todayStartMs);
    Tracked& track(std::size_t root, const std::string& path);
    bool readLive(Tracked& file, bool seeded = false);
    bool addRuntimeFile(std::size_t root, const discovery::DiscoveredFile& found, std::int64_t todayStartMs);
    void remove(const std::string& path);
    void pushAgent(Tracked& file);
    void pushInfo(AgentInfo info, const AgentLink& link);
    // The parser's session-current fact, or the stores', and the agent's configured values.
    void fillContext(AgentInfo& info, const Tracked& file) const;
    // Checks the config files and `workbuddy.db`; agents take what changed.
    bool refreshModeContext();
    ModeAttribution currentEffort(const std::string& agentId) const;
    void removeAgent(const std::string& id);
    bool pollRecent(std::int64_t nowMs);
    bool rediscover(std::int64_t nowMs, std::int64_t todayStartMs);
    void resolveParents();
    std::optional<std::pair<std::int64_t, std::int64_t>> stampOf(const Tracked& file) const;
    void addNewOmpRoots();
    void addNewPiRoots();
    void addNewCodexHomeRoots();
    void refreshAvailability();

    // Database and log sources (monitor_sources.cpp).
    void initSources();
    void seedSources(std::int64_t todayStartMs);
    bool refreshSources(bool discover, std::int64_t nowMs, std::int64_t todayStartMs);
    bool applyOpenCode(const opencode::Update& update, bool seeded);
    bool applyZCode(const zcode::Update& update, bool seeded);
    bool applyOpenClaw(const openclaw::Update& update, bool seeded);
    bool applyHermes(const hermes::Update& update, bool seeded);
    void upsert(const ParsedResponse& response, bool completion, bool seeded);
    std::pair<std::string, bool> sourceStatus(SourceKind kind) const;

    MonitorConfig config_;
    Environment environment_;
    std::string home_;
    Deduper deduper_;
    std::unique_ptr<Store> store_;
    std::vector<Root> roots_;
    std::map<std::string, std::unique_ptr<Tracked>> files_;
    std::map<std::string, AgentInfo> agents_;
    std::map<std::string, AgentLink> links_;
    std::map<std::string, std::string> parents_;
    ModeContext modeContext_;
    bool linksChanged_ = false;
    bool partialHistory_ = false;
    bool restartRequested_ = false;
    std::int64_t removedMalformed_ = 0;
    std::int64_t lastRediscoveryMs_ = 0;

    std::unique_ptr<opencode::Source> openCode_;
    std::unique_ptr<zcode::Source> zcode_;
    std::unique_ptr<openclaw::Source> openClaw_;
    std::unique_ptr<hermes::Source> hermes_;
    std::shared_ptr<const TimeZone> zone_;
    std::map<std::string, std::uint64_t> openClawPushed_, hermesPushed_;
    std::map<std::string, std::unique_ptr<work::ActionReader>> windowActions_;
    std::unique_ptr<traffic::Service> traffic_ = std::make_unique<traffic::Service>();
    std::vector<traffic::AgentFacts> trafficAgents(std::int64_t nowMs);
    std::map<std::pair<SourceKind, std::string>, double> perfBaselines_;
    std::vector<std::optional<std::vector<SourceKind>>> scoreSets_{std::nullopt};
    std::vector<std::optional<std::vector<SourceKind>>> peakSets_;
    work::ActionReader* actionsOf(const std::string& id) const;
    // An agent's reader: tool results read live are counted for live reporting (METRICS 11.10).
    std::unique_ptr<work::ActionReader> makeActions(const std::string& id, SourceKind source);
    void scoreAgents(Snapshot& snap, const std::vector<engine::Response>& recent, const std::vector<engine::Agent>& agents,
                     std::int64_t nowMs);
    bool openCodeSeeded_ = false, zcodeSeeded_ = false, openClawSeeded_ = false, hermesSeeded_ = false;
    const std::int64_t epoch_;
    std::shared_ptr<const report::Counters> reportCounters_ = std::make_shared<report::Counters>();
    std::uint64_t reportCountersVersion_ = 0;
    report::Facts reportFacts(std::int64_t nowMs, const Snapshot& snap);
    std::int64_t clockMs_ = 0;
    report::PerfLedger perfLedger_;
    std::shared_ptr<const report::PerfRows> perfRows_ = std::make_shared<report::PerfRows>();
    std::uint64_t perfRowsVersion_ = UINT64_MAX;
    engine::Thinking lastThinking_;
};

}  // namespace agentspeed
