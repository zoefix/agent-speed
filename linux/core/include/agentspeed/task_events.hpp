#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <variant>
#include <vector>

#include "agentspeed/task_score.hpp"
#include "agentspeed/transcript_parser.hpp"

// The task score's event composer (METRICS 6.9 Composing the stream, spec/tools/task_events.py): one agent's lines,
// after its source parser read them, become prompt and action events, with results matched to their calls and
// running commands stitched back (TASK-PARSERS 4.3). Inputs and outputs are read in memory only.
namespace agentspeed::tasks {

struct ComposerStats {
    std::int64_t lines = 0, prompts = 0, withdrawn = 0, calls = 0, results = 0, orphanResults = 0, stitched = 0,
                 continuationResults = 0, scripts = 0, scriptsMapped = 0;
};

class Composer : public LineObserver {
public:
    Composer(std::string path, SourceKind source);
    ~Composer() override;

    void onLine(const TranscriptParser& parser, const json::Value& line, std::string_view raw) override;
    void reset();

    const std::vector<Event>& events() const { return events_; }
    const std::vector<ModelSpan>& models() const { return models_; }
    const ComposerStats& stats() const { return stats_; }
    const std::string& path() const { return path_; }
    // The last line was an assistant line whose key another file owns (METRICS 4.1 step 2, 4.7 step 2).
    bool lastDuplicate() const { return duplicate_; }
    // Changes whenever an event is added, withdrawn or completed.
    std::uint64_t version() const { return version_; }

    struct Held;

private:
    struct Call {
        std::uint64_t serial = 0;
        std::optional<std::string> tool;
        std::shared_ptr<Held> value;
        std::optional<std::string> cwd;
        std::optional<CommandInfo> info;
        std::optional<std::string> continues;
        bool answered = false;
    };
    struct Script {
        std::vector<Call> subs;
        std::vector<std::pair<int, std::string>> prints;
    };
    struct Running {
        std::uint64_t serial = 0;
        std::optional<CommandInfo> info;
        std::optional<std::string> cwd;
        bool masked = false;
        std::vector<std::string> texts;
    };

    std::optional<std::int64_t> claude(const TranscriptParser& p, const json::Value& obj);
    std::optional<std::int64_t> codex(const TranscriptParser& p, const json::Value& obj);
    std::optional<std::int64_t> piFamily(const TranscriptParser& p, const json::Value& obj);
    std::optional<std::int64_t> droid(const TranscriptParser& p, const json::Value& obj);

    void lineKind(const PromptLine& found, std::int64_t t);
    void prompt(std::int64_t t, bool continuation);
    void assistant(bool duplicate);
    void withdraw();
    void callsIn(const json::Value& content, std::string_view blockType, std::string_view field, std::int64_t t);
    void call(const json::Value& callId, std::int64_t t, const json::Value& tool, const json::Value& value);
    void script(const json::Value& callId, std::int64_t t, const CodeScript& script);
    Call action(std::int64_t t, const std::string& tool, std::shared_ptr<Held> value, const std::string& digest);
    void result(const json::Value& callId, const json::Value& result, std::int64_t t);
    void answer(Call& call, const json::Value& result, std::int64_t t);
    void finish(const std::string& key, const Outcome& out, const std::optional<std::string>& text, std::int64_t t);
    void track(const TranscriptParser& p, std::int64_t t);
    Event* find(std::uint64_t serial);

    std::string path_;
    SourceKind source_;
    std::optional<std::string> cwd_;
    std::vector<Event> events_;
    std::vector<ModelSpan> models_;
    std::map<std::string, std::variant<Call, Script>> calls_;
    std::map<std::string, Running> sessions_;
    std::optional<std::pair<std::string, std::uint64_t>> pending_;  // (prompt or command, event serial)
    bool headerSeen_ = false;
    bool duplicate_ = false;
    std::uint64_t nextSerial_ = 1;
    std::uint64_t version_ = 0;
    ComposerStats stats_;
};

// ---- values at an instant (6.9 Values)

// 6.7 step 1: a phase older than 300 s without a new line is idle.
Phase effectivePhase(Phase phase, std::optional<std::int64_t> lastLineMs, std::int64_t now);

struct ScoredAgent {
    std::string id;
    SourceKind source = SourceKind::Codex;
    Phase phase = Phase::Idle;  // after the staleness rule
    std::optional<std::int64_t> lastLineMs;
    std::optional<std::string> parentId;
    const std::vector<Event>* events = nullptr;
};

struct AgentRow {
    ScoredAgent agent;
    AgentValue value;
    std::optional<TaskResult> task;
    std::optional<std::int64_t> taskStartMs;
    std::optional<Predictions> predictions;
    Models models;
};
AgentRow agentRow(const ScoredAgent& agent, std::int64_t now, const Models& models);

struct SetValue {
    std::optional<std::vector<SourceKind>> sources;  // nullopt: every source
    Highest value;
};
struct GroupValue {
    std::string root;
    std::vector<std::string> members;
    Highest value;
};
struct SetValues {
    std::vector<std::string> listed;  // the agents list (6.7), ordinal order
    std::vector<SetValue> sets;
    std::vector<GroupValue> groups;   // board groups, roots in list order
};
// The whole set, the given source sets (by default every scored source with a listed agent) and the board groups.
SetValues setValues(const std::vector<AgentRow>& rows, std::int64_t now,
                    const std::optional<std::vector<std::optional<std::vector<SourceKind>>>>& sets = std::nullopt);

// METRICS 6.10 digest of a tool input: a string's text, an object's or array's JSON exactly as the line stores it, an
// absent or null input as empty text.
std::string inputDigest(const json::Value& value);

}  // namespace agentspeed::tasks
