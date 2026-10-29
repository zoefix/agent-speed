#pragma once

#include <deque>
#include <unordered_map>

#include "agentspeed/transcript_parser.hpp"

namespace agentspeed {

// METRICS 4.1: Claude Code and Claude Desktop transcripts.
class ClaudeParser : public TranscriptParser {
public:
    ClaudeParser(std::string agentId, Deduper& deduper);
    void feed(std::string_view line, ResponseSink& sink) override;
    void reset() override;

private:
    static constexpr std::size_t kMergeCacheCapacity = 64;
    void remember(const ParsedResponse& response);

    bool pathIsSubagent_;
    std::optional<std::int64_t> pendingStartMs_;
    bool preciseStart_ = false;
    std::unordered_map<std::string, ParsedResponse> recent_;
    std::deque<std::string> recentOrder_;
};

}  // namespace agentspeed
