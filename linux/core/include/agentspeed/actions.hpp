#pragma once

#include <cstdint>
#include <deque>
#include <functional>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/json.hpp"
#include "agentspeed/model.hpp"
#include "agentspeed/transcript_parser.hpp"

// The actions of METRICS 6.10 (spec/tools/tool_actions.py): an agent's tool calls with their tool, input digest, call
// time, the time of the first result, whether it failed and the model of the response that issued it. Inputs and
// outputs are read in memory only.
namespace agentspeed::work {

struct Action {
    std::int64_t callMs = 0;
    std::optional<std::int64_t> resultMs;
    std::string tool, digest;
    std::optional<bool> failed;  // nullopt: unknown
    std::optional<std::string> model;
    bool seeded = false;
    std::uint64_t serial = 0;
};

// Claude (both), Codex, omp, Pi, OpenClaw and Factory Droid; other sources record no actions.
bool hasActions(SourceKind source);

// A tool call or a tool result of one line, by call id, as the source writes it (no parser state).
struct ToolEvent {
    enum class Kind : std::uint8_t { Call, Result } kind = Kind::Call;
    std::string id;
    std::string tool, digest;    // calls
    std::optional<bool> failed;  // results; nullopt: unknown
};
std::vector<ToolEvent> toolEvents(SourceKind source, const json::Value& line);

// An agent's actions in file order: results matched to their call by id, only the first result of a call counted.
class ActionLog {
public:
    static constexpr std::size_t kStored = 64;

    // `cap` actions are kept, the newest in file order (0: all of them).
    explicit ActionLog(std::size_t cap = kStored) : cap_(cap) {}

    void record(const std::vector<ToolEvent>& events, std::int64_t at, const std::optional<std::string>& model, bool seeded = false);
    // Called when a call's first result is read: the order in which tool calls complete (6.11).
    void setResultHandler(std::function<void(const Action&)> handler) { onResult_ = std::move(handler); }
    const std::deque<Action>& actions() const { return actions_; }
    // Changes whenever an action is added or completed.
    std::uint64_t version() const { return version_; }
    void clear();

private:
    std::size_t cap_;
    std::deque<Action> actions_;
    std::map<std::string, std::uint64_t> byCall_;
    std::uint64_t nextSerial_ = 1;
    std::uint64_t version_ = 0;
    std::function<void(const Action&)> onResult_;
};

// The actions of one agent (a log file or an OpenClaw transcript window), from the lines its parser handled: a line
// the parser skips adds nothing, and the calls of an assistant line whose key another file owns are not actions.
class ActionReader : public LineObserver {
public:
    explicit ActionReader(std::string path, std::size_t cap = ActionLog::kStored) : path_(std::move(path)), log_(cap) {}

    void onLine(const TranscriptParser& parser, const json::Value& line, std::string_view raw) override;
    void onReset(const TranscriptParser&) override { clear(); }

    // Lines read while seeding add seeded actions (METRICS 6.10 Storage).
    void setSeeding(bool seeding) { seeding_ = seeding; }
    void setResultHandler(std::function<void(const Action&)> handler) { log_.setResultHandler(std::move(handler)); }
    const ActionLog& log() const { return log_; }
    const std::deque<Action>& actions() const { return log_.actions(); }
    void clear();

private:
    // Whether the line counts, and whether its calls do (an assistant line whose key another file owns adds none).
    struct Gate {
        std::optional<std::int64_t> at;
        bool calls = true;
    };
    Gate claude(const TranscriptParser& p, const json::Value& obj) const;
    Gate codex(const TranscriptParser& p, const json::Value& obj) const;
    Gate piFamily(const TranscriptParser& p, const json::Value& obj);
    Gate droid(const TranscriptParser& p, const json::Value& obj) const;
    bool ownedByOther(const TranscriptParser& p, const std::string& key) const;

    std::string path_;
    ActionLog log_;
    bool headerSeen_ = false;
    bool seeding_ = false;
};

// METRICS 6.10 digest: a string input's text, an object's or array's JSON exactly as the line stores it, an absent
// or null input as empty text; the first 16 lowercase hex digits of SHA-256.
std::string inputDigest(const json::Value& value);

// `exit_code` of the leading JSON object of a Codex exec output (in the first `input_text` item of an array), or
// nullopt.
std::optional<std::int64_t> codexExitCode(const json::Value& output);

}  // namespace agentspeed::work
