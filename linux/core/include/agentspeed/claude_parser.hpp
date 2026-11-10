#pragma once

#include <deque>
#include <unordered_map>
#include <unordered_set>

#include "agentspeed/transcript_parser.hpp"

namespace agentspeed {

// METRICS 4.1: Claude Code and Claude Desktop transcripts, and Qoder CLI's with the differences of METRICS 4.19.
class ClaudeParser : public TranscriptParser {
public:
    enum class Variant {
        Claude,
        // Qoder CLI: source `qoderCli`, its own keys, no response without usage, no `api-request`, execution modes or
        // actions.
        QoderCli,
    };

    ClaudeParser(std::string agentId, Deduper& deduper, Variant variant = Variant::Claude);
    void feed(std::string_view line, ResponseSink& sink) override;
    void reset() override;

private:
    static constexpr std::size_t kMergeCacheCapacity = 64;
    void handle(const json::Value& root, std::string_view text, ResponseSink& sink);
    void handleQoder(const json::Value& root, std::string_view text, ResponseSink& sink);
    // Keeps a key in the merge memory: with its response, or claimed by a Qoder CLI line without usage.
    void remember(const ParsedResponse& response);
    void rememberSilent(const std::string& key);
    void makeRoom();
    bool ultracode() const { return ultraSession_ || ultraTurn_; }
    void refreshModes();

    Variant variant_;
    bool pathIsSubagent_;
    std::optional<std::int64_t> pendingStartMs_;
    bool preciseStart_ = false;
    std::unordered_map<std::string, ParsedResponse> recent_;
    // Qoder CLI keys this file claimed with a line without usage (no response yet).
    std::unordered_set<std::string> silent_;
    std::deque<std::string> recentOrder_;
    // Reasoning effort: the ultracode switches, the latest response's facts and the latest line effort.
    bool ultraSession_ = false;
    bool ultraTurn_ = false;
    ExecutionModes latestModes_;
    std::optional<ModeFact> lineEffort_;
};

}  // namespace agentspeed
