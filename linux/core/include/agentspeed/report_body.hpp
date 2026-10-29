#pragma once

#include <array>
#include <cstdint>
#include <map>
#include <optional>
#include <set>
#include <string>

#include "agentspeed/report_json.hpp"
#include "agentspeed/report_scheduler.hpp"
#include "agentspeed/report_perf.hpp"
#include "agentspeed/report_values.hpp"
#include "agentspeed/snapshot.hpp"

// The report body of REPORTING 6 from a window, the tick's values and the options (METRICS 11.2, 11.7).
namespace agentspeed::report {

enum class Section { Sources, Models, Cost, Traffic };
inline constexpr std::array kAllSections = {Section::Sources, Section::Models, Section::Cost, Section::Traffic};
const char* sectionName(Section section);
std::optional<Section> sectionFromName(std::string_view name);

// `client` of every body (REPORTING 6.3).
struct ClientInfo {
    ClientInfo(std::string version, std::string platform, int osMajor);
    std::string version, platform;
    int osMajor = 0;
    // MAJOR.MINOR.PATCH[-PRERELEASE], at most 32 characters.
    static bool isVersion(std::string_view text);
    std::string userAgent() const { return "AgentSpeed/" + version + " (" + platform + ")"; }
    Json json() const;
};

// Who and when a report is (REPORTING 6.3). sentAtMs: Unix ms of the build on the corrected clock (METRICS 11.6).
struct Envelope {
    ClientInfo client{"0.0.0", "Linux", 1};
    std::string deviceId, username, runId;
    int seq = 0;
    std::int64_t sentAtMs = 0, windowMs = 0;
    Activity activity = Activity::Active;
    int titleLevel = 0;
};

struct Extensions {
    bool perfScore = false, taskScore = false, executionModes = false, modelHarnessRequests = false;
};

// A WebSocket body (METRICS 11.9): it ends with `realtime` and its message must fit too.
struct StreamBody {
    std::optional<std::int64_t> observationAgeMs;
    int maxMessageBytes = 65'536;
};

// What a report may contain and how large it may be (REPORTING 3.2, 6.1, 6.8).
struct Options {
    std::set<Section> sections;  // the optional sections both the service lists and the user's switches allow
    int maxModels = 16;
    int bodyLimit = 16'384;
    bool totalMetric = false;
    double redline = 2000;
    int priceVersion = 1;
    Extensions extensions;
    std::optional<StreamBody> stream;
    bool modelsDroppedForSize = false;
    bool modelsByConsent() const { return maxModels >= 1 && (modelsDroppedForSize || sections.contains(Section::Models)); }
    // METRICS 11.10 Active, and the retired taskScore (thinking only) when the service advertises it but not perfScore.
    bool perfScoreActive() const { return extensions.perfScore && modelsByConsent(); }
    bool legacyTaskScoreActive() const { return !extensions.perfScore && extensions.taskScore && modelsByConsent(); }
};

struct Rate {
    double output = 0, total = 0;
};

// One source's instantaneous values (METRICS 11.2).
struct SourceSample {
    Rate live;
    double estimatedShare = 0, liveCost = 0, knownShare = 1;
};

enum class TrafficStatus { Ok, Off, Unavailable };

struct Bytes {
    std::int64_t rx = 0, tx = 0;
};

// Cumulative bytes of every source (rxSelf, txSelf, rxTools, txTools), disabled ones included, so a baseline stays valid
// across a toggle; `epoch` is the engine instance they belong to.
struct TrafficTotals {
    std::int64_t epoch = 0;
    std::map<SourceKind, std::array<std::uint64_t, 4>> sources;
};

// Traffic as live reporting reads it (METRICS 11.2): rates of the latest sample and cumulative bytes.
struct TrafficSample {
    TrafficStatus status = TrafficStatus::Off;
    bool includesTools = true;
    std::optional<traffic::Rate> rate;  // Σ over the reported sources; nullopt while the series has fewer than two samples
    std::map<SourceKind, traffic::Rate> sourceRates;
    TrafficTotals totals;
    std::set<SourceKind> enabled;  // the sources whose bytes are reported (a disabled source reports no traffic)

    static TrafficSample from(const traffic::View& view, bool includeTools, const std::set<SourceKind>& enabled, std::int64_t epoch);
    // Agent bytes of the window: own plus tools of every reported source (own only without tools).
    std::map<SourceKind, Bytes> agentBytes(const TrafficTotals& baseline) const;
};

// The instantaneous values of one tick (METRICS 11.2).
struct Sample {
    Rate live;
    double estimatedShare = 0, liveCost = 0, knownShare = 1;
    std::map<SourceKind, SourceSample> sources;
    Facts facts;
    TrafficSample traffic;

    // Every enabled source together, never an orb's subset (METRICS 11.1).
    static Sample from(const Snapshot& snapshot, TrafficSample traffic);
    double value(bool total) const { return total ? live.total : live.output; }
    // An agent generates or runs tools (the activity rule of METRICS 11.5).
    bool working() const;
};

// What a built report takes from its window (METRICS 11.4, 11.5): the deltas since the last build or discard and the
// peaks of the ticks inside it.
struct WindowContent {
    Counters usage;
    PerfRows perf;  // the statistics ledger's rows a build takes (METRICS 11.10)
    std::map<SourceKind, Bytes> agentBytes;
    Rate peak;
    double peakCost = 0;
    std::optional<traffic::Rate> peakRate;
};

// The open window of a run: a baseline of the cumulative counters and the peaks since the window started.
class Window {
public:
    explicit Window(const Sample& sample);
    // The ledger received a token increment since the previous tick (the activity rule of METRICS 11.5).
    bool hasTokenIncrement(const Sample& sample) const;
    // Any increment since the previous tick — tokens, cost, unpriced requests and agent traffic when `traffic` (the
    // changed rule of METRICS 11.9).
    bool hasIncrement(const Sample& sample, bool traffic) const;
    // Empties the window. `keepingTick` keeps this tick's increments (the gap rule, METRICS 11.1).
    void discard(const Sample& sample, bool keepingTick);
    // Ends a tick: its values join the peaks unless the window started at this tick, and it becomes the previous tick.
    void observe(const Sample& sample, bool peaks = true);
    // The window's content as of `sample` (which it includes), without emptying it (the preview).
    WindowContent content(const Sample& sample) const;
    // Builds: the content, then an empty window starting at `sample`.
    WindowContent take(const Sample& sample);
    // The responses and calls of the current statistics generation the builds took (METRICS 11.10: the caps count the
    // rest).
    std::int64_t takenResponses() const { return takenResponses_; }
    std::int64_t takenCalls() const { return takenCalls_; }

private:
    std::shared_ptr<const Counters> baseline_, lastCounters_;
    std::int64_t baselineEpoch_ = 0, lastEpoch_ = 0;
    TrafficTotals trafficBaseline_, lastTraffic_;
    Rate peak_;
    double peakCost_ = 0;
    std::optional<traffic::Rate> peakRate_;
    std::shared_ptr<const PerfRows> perfBaseline_;
    std::uint64_t perfGeneration_ = 0;
    std::int64_t lastCounted_ = 0;
    std::uint64_t lastPerfGeneration_ = 0;
    std::int64_t takenResponses_ = 0, takenCalls_ = 0;
};

// The report title tracker (METRICS 11.2, REPORTING 6.4): the speed title level 0–13 of DESIGN 1 — entered at a
// threshold, left below 90 % of it, a change held for 0.8 s, idle after 3 s below 0.5 tok/s.
class TitleTracker {
public:
    static constexpr int kMaxLevel = 13;
    int level() const { return level_; }
    static double enterSpeed(int level, double redline);
    void update(double value, double redline, double nowSeconds);
    // min(1, (v / R)^0.6) of the unrounded live value.
    static double heat(double value, double redline);

private:
    int rawLevel(double value, double redline, double now);
    int level_ = 0, candidate_ = 0;
    double candidateSince_ = 0;
    std::optional<double> belowSince_;
};

// The WebSocket transport's `report` message (METRICS 11.9): the four signature headers of the report's v1 POST and the
// body as a JSON string.
std::string reportMessage(const Headers& headers, std::string_view body);
// Its UTF-8 size: the signature headers have fixed lengths, so only the body's escaping varies.
std::size_t messageBytes(std::string_view body);
// `realtime`: mixed when the report carries traffic with status ok, else log.
Json realtime(bool mixed, std::int64_t sentAtMs, std::optional<std::int64_t> observationAgeMs);

// YYYY-MM-DDTHH:MM:SS.sssZ of Unix milliseconds.
std::string formatInstant(std::int64_t ms);

struct Fitted {
    Json json;
    std::string data;
};
// The body as sent: within the body limit, dropping models and then sources when it would not fit (REPORTING 6.8).
Fitted fitted(const Envelope& envelope, const WindowContent& content, const Sample& sample, Options options);
Json body(const Envelope& envelope, const WindowContent& content, const Sample& sample, const Options& options);

}  // namespace agentspeed::report
