#pragma once

#include "agentspeed/transcript_parser.hpp"

namespace agentspeed {

// METRICS 4.2: Codex rollouts (`.jsonl`, and `.jsonl.zst` read whole, 5.3).
class CodexParser : public TranscriptParser {
public:
    CodexParser(std::string agentId, Deduper& deduper);
    void feed(std::string_view line, ResponseSink& sink) override;
    void reset() override;

    std::optional<std::int64_t> createdMs() const { return fileCreatedMs_; }
    const std::optional<std::string>& metaThreadId() const { return metaThreadId_; }
    const std::optional<std::string>& parentThreadId() const { return parentThreadId_; }

    struct Usage {
        std::int64_t input = 0, cached = 0, cacheWrite = 0, output = 0, reasoning = 0, totalTokens = 0;
    };

private:
    void onInput(std::int64_t ts);
    void afterUsage(std::int64_t ts);
    void emit(const std::string& key, const TokenCounts& tokens, std::int64_t ts, ResponseSink& sink);
    std::string threadId() const { return metaThreadId_ ? *metaThreadId_ : agentId(); }

    bool pathIsSubagent_;
    std::optional<std::int64_t> fileCreatedMs_;
    std::optional<std::int64_t> subagentStartOrdinal_;
    std::string myAgentPath_ = "/root";
    std::optional<std::string> metaThreadId_;
    std::optional<std::string> parentThreadId_;
    std::optional<std::string> provider_;
    std::optional<std::int64_t> pendingStartMs_;
    bool streaming_ = false;
    bool sawModernUsage_ = false;
    std::optional<Usage> previousLegacyTotals_;
};

}  // namespace agentspeed
