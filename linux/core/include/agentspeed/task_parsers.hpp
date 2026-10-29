#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "agentspeed/json.hpp"
#include "agentspeed/model.hpp"

// The task-score parsers (spec/TASK-PARSERS.md): pure functions that turn one tool call, its command text, its result
// and its edit input into the small facts METRICS 6.9 keeps. Inputs are read in memory and never kept.
namespace agentspeed::tasks {

// read, search, edit, command, delegate, web, test, build, check, other (2, 3.9).
std::string actionKind(SourceKind source, const std::optional<std::string>& tool, const json::Value& input,
                       const std::optional<std::string>& cwd);
std::optional<std::string> commandText(SourceKind source, const std::optional<std::string>& tool, const json::Value& input);

struct CommandInfo {
    std::string cls = "other";  // test, build, check, other
    std::optional<std::string> suite;
    bool targeted = false;
    std::string key;  // 16-hex digest
    bool masked = false;
    bool background = false;
    bool errorFilter = false;
};
CommandInfo commandInfo(const std::string& text, const std::optional<std::string>& cwd);

struct MaskedStatus {
    std::optional<std::int64_t> exitCode;
    std::optional<bool> failed;
};
std::optional<MaskedStatus> maskedStatus(const std::string& text, const std::string& output, const std::optional<std::string>& cwd);

struct Outcome {
    std::optional<std::int64_t> exitCode;
    std::optional<bool> failed;
    std::optional<std::int64_t> t;
};
Outcome outcome(SourceKind source, const json::Value& result, std::optional<std::int64_t> t, const std::optional<std::string>& tool,
                const json::Value& input, const std::optional<std::string>& cwd);
std::string resultText(SourceKind source, const json::Value& result);
// (background, masked) of a shell call: the input's own background flag or the command's (4.2).
std::pair<bool, bool> shellCallState(SourceKind source, const std::optional<std::string>& tool, const json::Value& input,
                                     const std::optional<std::string>& cwd);
std::optional<std::string> continuation(SourceKind source, const std::optional<std::string>& tool, const json::Value& input);
std::optional<std::string> runningSession(SourceKind source, const json::Value& result);

struct Notification {
    std::string session;
    std::optional<std::int64_t> exitCode;
};
std::optional<Notification> notification(SourceKind source, const json::Value& line);

struct CodeCall {
    std::string tool;
    std::string input;  // compact JSON
    std::string digest;
};
struct CodeScript {
    std::vector<CodeCall> calls;
    bool plain = false;
    std::vector<std::pair<int, std::string>> prints;
};
std::optional<CodeScript> codeScript(SourceKind source, const std::optional<std::string>& tool, const json::Value& input);
std::optional<std::vector<std::optional<std::string>>> codeItems(SourceKind source, const json::Value& result);

struct FailureReport {
    std::vector<std::string> fingerprints;
    std::optional<std::int64_t> failed, passed;
    std::optional<std::string> framework;
    std::vector<std::string> canonical;
};
FailureReport failures(const std::optional<CommandInfo>& info, const std::string& output, const std::optional<std::string>& cwd,
                       std::optional<bool> failed);

struct EditHunk {
    std::string file;
    std::optional<std::string> oldDigest, newDigest;
};
std::vector<EditHunk> editHunks(SourceKind source, const std::optional<std::string>& tool, const json::Value& input,
                                const std::optional<std::string>& cwd);

}  // namespace agentspeed::tasks
