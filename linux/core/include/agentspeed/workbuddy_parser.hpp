#pragma once

#include <map>
#include <set>

#include "agentspeed/json.hpp"
#include "agentspeed/transcript_parser.hpp"

namespace agentspeed {

namespace workbuddy {
// `<slug>/<dir>/subagents/<file>` below the root.
bool isSubagentPath(const std::string& root, const std::string& path);
// providerData.model, else requestModelId (trimmed, not blank); a custom: or custom-local: prefix gives provider custom.
std::pair<std::optional<std::string>, std::optional<std::string>> resolveModel(const std::optional<std::string>& streamModel,
                                                                                const std::optional<std::string>& requestModelId);

// The measured attempt that answered a call (METRICS 4.9 Timing model), in truncated milliseconds.
struct Attempt {
    std::int64_t durationMs = 0;  // > 0
    std::optional<std::int64_t> firstTokenMs;  // > 0 and <= durationMs
    // The call's start for a usage line written at `ts`, saturating.
    std::int64_t startMs(std::int64_t ts) const;
};
// The last element of `rawUsage.model_calls` when it is an object with a finite `duration_ms` that truncates above 0.
std::optional<Attempt> lastAttempt(const json::Value& rawUsage);
}  // namespace workbuddy

// METRICS 4.9: the WorkBuddy format, one parser for three sources: WorkBuddy, WorkBuddy AI and CodeBuddy Code (4.20).
class WorkBuddyParser : public TranscriptParser {
public:
    WorkBuddyParser(std::string agentId, Deduper& deduper, SourceKind source, const std::string& root);
    void feed(std::string_view line, ResponseSink& sink) override;
    void reset() override;
    AgentLink link() const override;

    struct Line;

private:
    void applyToolResult(const Line& line, std::int64_t ts);
    void applyOutput(const Line& line, std::int64_t ts, ResponseSink& sink);
    void upsert(const std::string& key, const Line& line, std::int64_t ts, ResponseSink& sink);
    void idle(std::int64_t ts);

    SourceKind edition_;
    bool pathIsSubagent_;
    std::map<std::string, std::string> openCalls_;
    std::map<std::string, ParsedResponse> responses_;
    // Keys whose start came from a measured attempt: the first measured start of a call is kept.
    std::set<std::string> measured_;
    std::optional<std::int64_t> pendingStartMs_;
};

}  // namespace agentspeed
