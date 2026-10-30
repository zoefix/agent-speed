#pragma once

#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <set>

#include "agentspeed/dedupe.hpp"
#include "agentspeed/line_splitter.hpp"
#include "agentspeed/model.hpp"
#include "agentspeed/tz.hpp"

// METRICS 4.15: Hermes Agent, read from its logs (logs/agent.log and rotated copies), one log set per log folder.
namespace agentspeed::hermes {

constexpr std::size_t kTimestampLength = 23;

// Paths (Python's posixpath / ntpath rules, host independent).
std::string home(const std::function<std::optional<std::string>(const std::string&)>& environment, bool windows,
                 const std::string& userHome);
std::vector<std::string> logFolders(const std::string& home);
std::vector<std::string> settingFolders(const std::string& setting);
std::vector<std::string> logFiles(const std::string& folder);
bool isLogName(std::string_view name);
std::optional<std::string> configPath(const std::string& folder, bool windows = false);

// The Codex runtime switch of a profile's config.yaml.
bool selectsCodexRuntime(std::string text);
// The scalars `reasoning_effort` and `service_tier` of a top-level `agent` block: the last key line of each decides, and
// anything but one plain scalar gives nullopt. A tolerant line reader, not a YAML parser.
std::map<std::string, std::optional<std::string>> agentScalars(std::string text);
// The configured facts of the `agent` block.
ExecutionModes configuredModes(const std::string& text);
// What a profile's config.yaml says (METRICS 4.15).
struct ProfileConfig {
    bool codexRuntime = false;
    ExecutionModes configuredModes;
};
enum class ConfigCheck { Off, Read, Unchanged, Failed };
struct ConfigSignature {
    std::int64_t mtimeNs = 0, size = 0;
    bool operator==(const ConfigSignature&) const = default;
};
ConfigCheck checkConfig(const std::optional<std::string>& path, const std::optional<ConfigSignature>& known,
                        std::optional<ConfigSignature>& signature, ProfileConfig& config);

// A zone-less wall time converted like Python's datetime.timestamp() of a naive local time with fold = 0.
std::optional<std::int64_t> toUnixMs(int year, int month, int day, int hour, int minute, int second, int millisecond,
                                     const TimeZone& zone);
std::optional<std::int64_t> timestampOf(std::string_view line, const TimeZone& zone);

struct Agent {
    std::string agentId, sessionId;
    Phase phase = Phase::Idle;
    std::optional<std::int64_t> inFlightStartMs;
    std::optional<std::string> model;
    bool isSubagent = false;
    std::optional<std::int64_t> lastLineMs, lastMainTurnMs;
    std::uint64_t version = 0;
    // A session-current fact and the profile's configured values (EXECUTION-MODES, Current values).
    ExecutionModes sessionModes, configuredModes;

    void touch(std::int64_t ts);
    void startTurn(std::int64_t ts, const std::string& model, bool subagent, bool codexRuntime);
    void call(std::int64_t ts, const std::string& model);
    void endTurn() { setPhase(Phase::Idle, std::nullopt); }

private:
    void setModel(const std::string& m);
    void setPhase(Phase p, std::optional<std::int64_t> inFlight);
};

class LogSet {
public:
    LogSet(std::string folder, Deduper& deduper, std::shared_ptr<const TimeZone> zone);
    static std::string agentIdOf(const std::string& folder, const std::string& sessionId) { return folder + "#" + sessionId; }

    const std::string& folder() const { return folder_; }
    const std::vector<Agent*>& agents() const { return ordered_; }
    Agent* agent(const std::string& sessionId);
    std::int64_t malformedLines() const { return malformedLines_; }
    bool codexRuntime() const { return codexRuntime_; }
    const std::optional<std::string>& config() const { return configPath_; }
    void setZone(std::shared_ptr<const TimeZone> zone) { zone_ = std::move(zone); }
    const std::optional<std::string>& statePath() const { return statePath_; }
    // Returns true when the switch or a configured value changed.
    bool checkConfig();
    void setCodexRuntime(bool on);
    void setConfiguredModes(const ExecutionModes& modes);
    // A session's session-current fact. True when it changed.
    bool setSessionModes(const std::string& sessionId, const ExecutionModes& modes);
    void line(std::string_view raw, ResponseSink& sink);

private:
    Agent& agentFor(const std::string& sessionId, std::int64_t ts);
    void call(const std::string& sessionId, const std::string& message, const std::string& stamp, std::int64_t ts,
              ResponseSink& sink);

    std::string folder_;
    Deduper& deduper_;
    std::shared_ptr<const TimeZone> zone_;
    std::optional<std::string> configPath_;
    std::map<std::string, std::unique_ptr<Agent>> agents_;
    std::vector<Agent*> ordered_;
    std::int64_t malformedLines_ = 0;
    bool codexRuntime_ = false;
    std::optional<ConfigSignature> configSignature_;
    // `state.db` beside `config.yaml`; none for a log folder of another name.
    std::optional<std::string> statePath_;
    ExecutionModes configuredModes_;
};

std::int64_t count(const std::string& text);
std::int64_t latencyMs(const std::string& text);

struct Update : ResponseSink {
    std::vector<std::pair<ParsedResponse, bool>> responses;
    std::vector<std::string> removed;
    bool changed = false;
    std::int64_t bytesRead = 0;
    int filesRead = 0;
    void onResponse(const ParsedResponse& r, bool completion) override { responses.emplace_back(r, completion); }
};

// The live side: every log set's files followed by identity across rotations.
class Source {
public:
    Source(std::string root, bool chosen, Deduper& deduper, std::shared_ptr<const TimeZone> zone);
    Update seed(std::int64_t todayStartMs);
    Update refresh(bool discover);
    std::vector<LogSet*> sets() const;
    std::int64_t malformedLines() const;
    bool partialHistory() const { return partialHistory_; }
    void setZone(std::shared_ptr<const TimeZone> zone);

private:
    struct Log {
        std::int64_t offset = 0;
        enum class Align { None, SkipPartialLine, SkipToNextLine } alignment = Align::None;
        LineSplitter lines;
        int missing = 0;
    };
    using FileKey = std::pair<std::int64_t, std::int64_t>;
    struct Tracked {
        std::unique_ptr<LogSet> set;
        std::map<FileKey, Log> logs;
        std::map<std::string, std::pair<std::int64_t, std::int64_t>> stamps;
        std::map<std::string, FileKey> keyOf;
        std::optional<std::pair<std::int64_t, std::int64_t>> stateStamp, stateWalStamp;
        std::set<std::string> queriedSessions;
    };

    std::vector<std::string> folders() const;
    Tracked& add(const std::string& folder);
    void readSet(Tracked& tracked, Update& update, std::optional<std::int64_t> thresholdMs, bool forget);
    void checkSessions(Tracked& tracked, Update& update);
    std::optional<std::pair<FileKey, std::pair<std::int64_t, std::int64_t>>> readFile(
        Tracked& tracked, const std::string& path, std::int64_t listedWriteNs, std::optional<std::int64_t> thresholdMs,
        Update& update);

    std::string root_;
    bool chosen_;
    Deduper& deduper_;
    std::shared_ptr<const TimeZone> zone_;
    std::vector<std::unique_ptr<Tracked>> sets_;
    std::set<std::string> unreadable_;
    std::vector<std::string> folders_;
    std::int64_t removedMalformedLines_ = 0;
    bool partialHistory_ = false;
};

}  // namespace agentspeed::hermes
