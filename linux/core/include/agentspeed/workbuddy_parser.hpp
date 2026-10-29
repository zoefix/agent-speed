#pragma once

#include <map>

#include "agentspeed/transcript_parser.hpp"

namespace agentspeed {

namespace workbuddy {
// `<slug>/<dir>/subagents/<file>` below the root.
bool isSubagentPath(const std::string& root, const std::string& path);
// providerData.model, else requestModelId (trimmed, not blank); a custom: or custom-local: prefix gives provider custom.
std::pair<std::optional<std::string>, std::optional<std::string>> resolveModel(const std::optional<std::string>& streamModel,
                                                                                const std::optional<std::string>& requestModelId);
}  // namespace workbuddy

// METRICS 4.9: WorkBuddy and WorkBuddy AI (one parser, two sources).
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
    std::optional<std::int64_t> pendingStartMs_;
};

}  // namespace agentspeed
