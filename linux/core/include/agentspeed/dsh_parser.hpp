#pragma once

#include "agentspeed/transcript_parser.hpp"

namespace agentspeed {

// METRICS 4.3: DeepSeek Harness sessions (a zstd frame stream of JSONL events), with fork-inherited history.
class DshParser : public TranscriptParser {
public:
    DshParser(std::string agentId, Deduper& deduper);
    void feed(std::string_view line, ResponseSink& sink) override;
    void reset() override;

    const std::optional<std::string>& sessionId() const { return sessionId_; }
    std::optional<std::int64_t> createdMs() const { return createdMs_; }
    const std::optional<std::string>& parentSession() const { return parentSession_; }
    AgentLink link() const override { return {agentId(), LinkKind::DshSession, sessionId_, parentSession_, createdMs_, 0}; }

    struct Event;

private:
    void apply(const Event& event, ResponseSink& sink);

    std::optional<std::string> sessionId_;
    std::optional<std::int64_t> createdMs_;
    std::optional<std::string> parentSession_;
    std::optional<std::int64_t> pendingStartMs_;
    std::optional<std::string> provider_;
};

}  // namespace agentspeed
