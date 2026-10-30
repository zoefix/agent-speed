#pragma once

#include <deque>
#include <map>
#include <vector>

#include "agentspeed/transcript_parser.hpp"

namespace agentspeed {

struct GrokUsage {
    std::optional<std::string> model;
    TokenCounts tokens;
    std::int64_t durationMs = 0;
    std::int64_t requests = 1;
};

struct GrokMetadata {
    std::optional<std::string> id, cwd, model, parent, kind;
    std::optional<std::int64_t> createdMs;
    // `reasoning_effort`, the session's latest value (session-current, EXECUTION-MODES).
    std::optional<std::string> effort;
    // `<session folder>/summary.json` (≤ 1 MiB).
    static std::optional<GrokMetadata> read(const std::string& folder);
};

// METRICS 4.12: Grok Build's `<workspace>/<session>/updates.jsonl` with summary.json beside it.
class GrokParser : public TranscriptParser {
public:
    GrokParser(std::string agentId, Deduper& deduper);
    // Before a read cycle's lines: summary.json as it is now, and the usage it held back until the metadata came.
    void beginBatch(ResponseSink& sink);
    void feed(std::string_view line, ResponseSink& sink) override;
    void reset() override;
    AgentLink link() const override;

    const std::string& sessionId() const { return sessionId_; }
    const std::optional<std::string>& parentSessionId() const { return parentSessionId_; }
    std::optional<std::int64_t> createdMs() const { return createdMs_; }
    const std::optional<std::string>& parentPath() const { return parentPath_; }
    std::size_t pendingUsageCount() const { return pending_.size(); }
    bool pendingOverflow() const { return pendingOverflow_; }

private:
    struct Line {
        enum class Status { Blank, Malformed, Valid } status = Status::Blank;
        std::optional<std::string> type, sessionId, promptId, model, toolStatus;
        std::optional<std::int64_t> timeMs;
        std::optional<std::vector<GrokUsage>> usage;
        static Line read(std::string_view text);
    };
    void apply(const Line& line, ResponseSink& sink);
    void emitUsage(const Line& line, std::int64_t time, ResponseSink& sink, bool updateModel = true);
    void applyMetadata(const std::optional<GrokMetadata>& metadata);

    std::map<std::string, ParsedResponse> responses_;
    std::string sessionFolder_, sessionId_;
    bool metadataSeen_ = false, suppressUsage_ = false, pendingOverflow_ = false;
    std::deque<Line> pending_;
    std::optional<std::string> parentSessionId_, parentPath_;
    std::optional<std::int64_t> createdMs_;
};

// The later of updates.jsonl's and summary.json's modification times (the reading order and the startup rule).
std::int64_t grokModifiedNs(const std::string& updatesPath, std::int64_t updatesWriteNs);

}  // namespace agentspeed
