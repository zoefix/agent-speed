#pragma once

#include <map>
#include <set>

#include "agentspeed/transcript_parser.hpp"

namespace agentspeed {

// METRICS 4.7: omp (oh-my-pi) sessions. Every response is written once, complete, with omp's own start and duration.
class OmpParser : public TranscriptParser {
public:
    OmpParser(std::string agentId, Deduper& deduper, const std::string& root);
    void feed(std::string_view line, ResponseSink& sink) override;
    void reset() override;
    AgentLink link() const override;

    bool pathIsSubagent() const { return pathIsSubagent_; }
    std::optional<std::int64_t> headerMs() const { return headerMs_; }
    bool headerSeen() const { return headerSeen_; }
    std::optional<std::string> parentPath() const;
    // Reading from an offset > 0: applies the header among the file's first two lines (once).
    void probeHeader(ResponseSink& sink);

    struct Line;

private:
    void apply(const Line& line, ResponseSink& sink);
    void applyAssistant(const Line& line, std::int64_t ts, ResponseSink& sink);
    void emitResponse(const std::string& key, std::optional<std::int64_t> startMs, std::int64_t endMs, const Line& line,
                      ResponseSink& sink);

    bool pathIsSubagent_;
    std::set<std::string> pendingToolCalls_;
    std::map<std::string, ParsedResponse> responses_;
    std::map<std::string, ExecutionModes> modeBranches_;
    ExecutionModes branchModes_;
    bool headerSeen_ = false;
    bool headerProbed_ = false;
    std::optional<std::int64_t> headerMs_;
};

// Path components of `path` below `root` (0 when outside it).
int depthBelow(const std::string& root, const std::string& path);

}  // namespace agentspeed
