#pragma once

#include <cstdint>
#include <deque>
#include <map>
#include <optional>
#include <string>

#include "agentspeed/json.hpp"
#include "agentspeed/model.hpp"

// Work signals (METRICS 6.10, spec/tools/work_signals.py): an agent's tool calls without their content, and the
// looping, repeating, novelty and recovery read from them.
namespace agentspeed::work {

struct Action {
    std::int64_t at = 0;
    std::string tool, digest;
    std::optional<bool> failed;  // nullopt: outcome unknown
    std::uint64_t serial = 0;
};

// The last 64 actions of one file; results are matched to their call by id.
class ActionLog {
public:
    static constexpr std::size_t kMaxActions = 64;

    void call(const json::Value& callId, std::int64_t at, const json::Value& tool, const json::Value& input);
    void result(const json::Value& callId, std::optional<bool> failed);
    // One line of a source with actions (Claude, Codex, omp, Pi, OpenClaw, Droid); others add nothing.
    void feed(SourceKind source, const json::Value& line, std::int64_t at);
    const std::deque<Action>& actions() const { return actions_; }
    void clear();

private:
    std::deque<Action> actions_;
    std::map<std::string, std::uint64_t> byCall_;
    std::uint64_t nextSerial_ = 1;
};

struct Signals {
    std::optional<std::int64_t> looping, repeating;
    std::optional<double> novelty;
    std::int64_t failed = 0, recovered = 0;
};
Signals signalsOf(const std::deque<Action>& actions, std::int64_t now);

// `exit_code` of the leading JSON object of a Codex exec output, or nullopt.
std::optional<std::int64_t> codexExitCode(const json::Value& output);

}  // namespace agentspeed::work
