#pragma once

#include <map>
#include <memory>
#include <set>

#include "agentspeed/transcript_parser.hpp"

namespace agentspeed {

namespace pi {

enum class Kind { Other, Session, Message, Usage, Summary, ThinkingLevelChange };
enum class Role { Other, User, Developer, Assistant, ToolResult };
enum class Stop { Aborted, Error, Pending, Deferred, Stop, ToolUse, Length, Other };

struct Call {
    std::optional<std::string> model;
    bool hasProvider = false;
    std::optional<std::string> provider;
    bool hasApi = false, apiIsCli = false, hasUsageObject = false, hasUsage = false, usageUnavailable = false;
    TokenCounts tokens;
    std::int64_t reasoningTokens = 0;
    bool guardedMessage() const { return model && hasProvider && hasApi && hasUsageObject; }
    bool guardedEntry() const { return model && hasProvider && hasUsageObject; }
};

struct Line {
    Kind kind = Kind::Other;
    std::optional<std::string> id, cwd;
    std::optional<std::int64_t> timestampMs;
    Role role = Role::Other;
    Call call;
    std::optional<std::int64_t> startedMs, durationMs;
    Stop stop = Stop::Aborted;
    std::vector<std::string> toolCallIds;
    std::optional<std::string> toolCallId, thinkingLevel;
    bool mirrored = false;
    bool failed() const { return stop == Stop::Aborted || stop == Stop::Error || stop == Stop::Pending || stop == Stop::Deferred; }
};

// One JSONL entry of the Pi family; nullopt with `malformed` for a line that does not parse.
std::optional<Line> readLine(std::string_view text, bool& malformed);
// `<folder>.jsonl` when it is a regular file: the session a package's sub-agent file belongs to.
std::optional<std::string> parentFileOf(const std::string& path);

}  // namespace pi

// METRICS 4.13: Pi (Pi Coding Agent) sessions, the upstream of omp.
class PiParser : public TranscriptParser {
public:
    PiParser(std::string agentId, Deduper& deduper, std::optional<std::string> parentPath);
    static std::unique_ptr<PiParser> forPath(const std::string& path, Deduper& deduper);
    void feed(std::string_view line, ResponseSink& sink) override;
    void reset() override;
    AgentLink link() const override;

    const std::optional<std::string>& parentPath() const { return parentPath_; }
    std::optional<std::int64_t> headerMs() const { return headerMs_; }
    void probeHeader(ResponseSink& sink);

private:
    void apply(const pi::Line& line, ResponseSink& sink);
    void applyAssistant(const pi::Line& line, std::int64_t ts, ResponseSink& sink);
    void applySide(const std::string& id, std::int64_t ts, const pi::Call& call, const std::optional<std::string>& model,
                   const std::optional<std::string>& provider, ResponseSink& sink);
    void selectEffort(const std::optional<std::string>& level, ModeEvidence evidence, ModeScope scope);
    void emitResponse(const std::string& key, std::optional<std::int64_t> startMs, std::int64_t endMs, const pi::Call& call,
                      const std::optional<std::string>& model, const std::optional<std::string>& provider,
                      const ExecutionModes& modes, ResponseSink& sink);

    std::optional<std::string> parentPath_;
    std::set<std::string> pendingToolCalls_;
    std::map<std::string, ParsedResponse> responses_;
    ExecutionModes context_;
    bool headerSeen_ = false;
    bool headerProbed_ = false;
    std::optional<std::int64_t> headerMs_;
};

}  // namespace agentspeed
