#pragma once

#include <map>

#include "agentspeed/transcript_parser.hpp"

namespace agentspeed {

// METRICS 4.11: Kimi Code's `<session>/agents/<agent>/wire.jsonl`, with state.json for the project and parents.
class KimiParser : public TranscriptParser {
public:
    KimiParser(std::string agentId, Deduper& deduper);
    void feed(std::string_view line, ResponseSink& sink) override;
    void reset() override;
    AgentLink link() const override;

    const std::string& sessionFolder() const { return sessionFolder_; }
    std::optional<std::int64_t> createdMs() const { return createdMs_; }
    const std::optional<std::string>& parentPath() const { return parentPath_; }

private:
    struct Line;
    void applyModes(const Line& line);
    void complete(const Line& line, std::int64_t time, ResponseSink& sink);
    void restoreMetadata();

    std::map<std::string, ParsedResponse> responses_;
    ExecutionModes contextModes_;
    std::string localAgent_, sessionFolder_;
    std::optional<std::int64_t> requestStart_, createdMs_;
    std::optional<std::string> requestId_, provider_, parentPath_;
};

}  // namespace agentspeed
