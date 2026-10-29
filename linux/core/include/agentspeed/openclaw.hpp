#pragma once

#include <functional>
#include <map>
#include <memory>
#include <set>

#include "agentspeed/pi_parser.hpp"
#include "agentspeed/sqlite.hpp"

// METRICS 4.14: OpenClaw keeps every transcript window in one SQLite database per OpenClaw agent, as Pi entries.
namespace agentspeed::openclaw {

struct Window {
    std::string sessionId;
    std::optional<std::int64_t> createdMs, updatedMs;
    std::optional<std::string> spawnedBy;
    std::string runtime;
    bool acpOwned = false;

    bool delegated() const;
    bool mirrorsOnly() const;
    bool isSubagent() const { return spawnedBy && !spawnedBy->empty(); }
    bool operator==(const Window&) const = default;
};

class Parser : public TranscriptParser {
public:
    Parser(const std::string& databasePath, Window window, Deduper& deduper);
    static std::string agentIdOf(const std::string& databasePath, const std::string& sessionId) {
        return databasePath + "#" + sessionId;
    }
    void feed(std::string_view line, ResponseSink& sink) override;
    void apply(const pi::Line& line, ResponseSink& sink);
    void reset() override;
    AgentLink link() const override;

    const std::string& databasePath() const { return databasePath_; }
    const std::string& sessionId() const { return window_.sessionId; }
    const Window& window() const { return window_; }
    const std::optional<std::string>& parentId() const { return parentId_; }
    std::optional<std::int64_t> headerMs() const { return headerMs_; }
    void updateWindow(const Window& window);
    bool runtimeChanged(const Window& window) const;
    void setParent(const std::optional<std::string>& parentId);

private:
    bool isMirror(const pi::Line& line) const;
    void applyAssistant(const pi::Line& line, std::int64_t ts, ResponseSink& sink);

    std::string databasePath_;
    Window window_;
    std::optional<std::string> parentId_;
    std::set<std::string> pendingToolCalls_;
    std::map<std::string, ParsedResponse> responses_;
    bool headerSeen_ = false;
    std::optional<std::int64_t> headerMs_;
};

// One row's entry: a parsed line, a malformed row, or an entry too long to read (skipped like a long line).
struct Entry {
    enum class Status { Valid, Malformed, Skipped } status = Status::Skipped;
    pi::Line line;
};

class Database {
public:
    static constexpr std::int64_t kMaxEventBytes = 4 * 1024 * 1024;
    static constexpr std::size_t kMaxEntryBytes = 32u << 20;

    // Throws sqlite::Error.
    static std::unique_ptr<Database> open(const std::string& path);
    ~Database();
    bool supported() const { return supported_; }
    bool immutable() const { return immutable_; }
    void begin();
    void commit();
    std::vector<Window> windows();
    std::map<std::string, std::string> currentSessions();
    std::map<std::string, std::string> watermarks();
    std::optional<std::int64_t> maxSeq(const std::string& sessionId);
    // Feeds rows with seq > afterSeq in order until `onRow` returns false; returns the last seq read.
    std::int64_t rows(const std::string& sessionId, std::int64_t afterSeq,
                      const std::function<bool(std::int64_t, const Entry&)>& onRow);
    // The first entry of type `session`.
    std::optional<pi::Line> header(const std::string& sessionId);

private:
    Database(std::unique_ptr<sqlite::Connection> connection, bool immutable, const std::set<std::string>& tables);
    std::unique_ptr<sqlite::Connection> connection_;
    bool immutable_;
    bool supported_;
    bool hasNodes_, hasWatermarks_;
    std::string windowsSql_, rowsSql_;
};

// Every window of a database read completely (history, fixtures); nullopt for an unsupported database.
std::optional<std::vector<std::unique_ptr<Parser>>> readAll(
    const std::string& path, Deduper& deduper, const std::function<ResponseSink&(Parser&)>& sinkFor,
    const std::function<void(Parser&, std::int64_t seq)>& afterRow = {});

struct Update : ResponseSink {
    std::vector<std::pair<ParsedResponse, bool>> responses;
    std::vector<std::string> removed;
    bool changed = false;
    std::int64_t bytesRead = 0;
    int databasesRead = 0;
    void onResponse(const ParsedResponse& r, bool completion) override { responses.emplace_back(r, completion); }
};

// The live side: databases from `discover`, windows read incrementally by seq with rewrite detection.
class Source {
public:
    Source(std::function<std::vector<std::string>()> discover, Deduper& deduper);
    ~Source();
    Update seed(std::int64_t todayStartMs);
    Update refresh(bool discover = false);
    std::vector<Parser*> parsers() const;
    bool unsupported() const;
    std::int64_t malformedLines() const;
    bool isTracked(const std::string& databasePath) const { return databases_.contains(databasePath); }

private:
    struct WindowState {
        std::unique_ptr<Parser> parser;
        std::int64_t lastSeq = INT64_MIN;
        std::optional<std::int64_t> updatedMs;
        std::optional<std::string> generation;
        bool needsHeader = false;
    };
    struct Tracked {
        std::string path;
        std::map<std::string, WindowState> windows;
        std::optional<sqlite::DatabaseStamp> stamp;
        std::optional<std::pair<std::int64_t, std::int64_t>> identity;
        bool opened = false, unsupported = false, unreadable = false;
    };

    void read(Tracked& tracked, Update& update);
    void readWindows(Tracked& tracked, Database& database, Update& update);
    void readRows(WindowState& state, Database& database, Update& update);
    void removeWindow(Tracked& tracked, const std::string& sessionId, Update& update);
    void drop(Tracked& tracked, Update& update);
    std::vector<std::pair<std::string, sqlite::DatabaseStamp>> discovered();

    std::function<std::vector<std::string>()> discover_;
    Deduper& deduper_;
    std::map<std::string, Tracked> databases_;
    std::int64_t removedMalformedLines_ = 0;
    std::int64_t thresholdMs_ = 0;
};

void linkParents(const std::string& path, const std::vector<Parser*>& parsers, const std::map<std::string, std::string>& current);

}  // namespace agentspeed::openclaw
