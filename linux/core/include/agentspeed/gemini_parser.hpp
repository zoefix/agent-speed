#pragma once

#include <map>
#include <set>

#include "agentspeed/transcript_parser.hpp"

namespace agentspeed {

namespace gemini {

std::string sanitize(std::string_view id);
bool isMainSessionPath(const std::string& path);
std::optional<std::string> parentDirectoryOf(const std::string& path);
std::optional<std::string> projectDirectoryOf(const std::string& path);
// From `<projectDir>/.project_root`, else `<root>/../projects.json` (cached per project folder).
std::optional<std::string> projectOf(const std::string& sessionPath);

}  // namespace gemini

// METRICS 4.6: Gemini CLI sessions — append-only `.jsonl` records and legacy whole-document `.json` snapshots.
class GeminiParser : public TranscriptParser {
public:
    static constexpr std::size_t kMaxDocumentBytes = 64u << 20;

    GeminiParser(std::string agentId, Deduper& deduper);
    // A `.json` parser takes the whole document as one line.
    void feed(std::string_view line, ResponseSink& sink) override;
    void reset() override;
    AgentLink link() const override;

    bool isDocument() const { return isDocument_; }
    bool isMainSession() const { return isMainSession_; }
    const std::optional<std::string>& parentDirectory() const { return parentDirectory_; }
    const std::optional<std::string>& sessionId() const { return sessionId_; }
    std::optional<std::int64_t> startTimeMs() const { return startTimeMs_; }

    struct ToolCall {
        std::string id;
        std::optional<std::int64_t> timestampMs;
        bool completesTask = false;
        bool cancelled = false;
    };
    enum class MessageKind { Other, User, Gemini, Error, Cancelled };
    struct Message {
        std::string id;
        MessageKind kind = MessageKind::Other;
        std::optional<std::int64_t> timestampMs, detailMs, firstCallMs;
        bool hasTokens = false;
        TokenCounts tokens;
        std::optional<std::string> model;
        bool hasVisibleText = false, hasFunctionCall = false;
        std::optional<std::vector<ToolCall>> toolCalls;
    };
    struct CheckpointResponse {
        std::string id;
        std::int64_t timestampMs;
        TokenCounts tokens;
        std::optional<std::string> model;
    };
    struct Header {
        std::string sessionId;
        bool subagentKind = false;
        std::optional<std::int64_t> startMs, lastUpdatedMs;
        std::vector<Message> messages;
    };

private:
    enum class Ownership { New, ThisFile, OtherFile };
    void applyHeader(const Header& header, ResponseSink& sink);
    void applyMessage(const Message& message, ResponseSink& sink);
    void applyToolCalls(const Message& message, std::int64_t ts);
    void applyCheckpoint(const std::vector<std::string>& ids, const std::vector<CheckpointResponse>& responses,
                         ResponseSink& sink);
    void emitResponse(const std::string& key, std::optional<std::int64_t> startMs, std::int64_t endMs,
                      const TokenCounts& tokens, const std::optional<std::string>& model, bool completion,
                      ResponseSink& sink);
    void update(const ParsedResponse& existing, std::int64_t endMs, const TokenCounts& tokens,
                const std::optional<std::string>& model, ResponseSink& sink);
    Ownership claim(const std::string& key);
    void generating(std::int64_t startMs, const std::string& marker);
    void idle(std::int64_t timestampMs);
    void setSessionId(const std::string& id);
    void restorePathState();

    bool isDocument_;
    bool isMainSession_;
    std::optional<std::string> parentDirectory_;
    bool pathIsSubagent_;
    std::optional<std::string> pathProject_;
    std::map<std::string, ParsedResponse> responses_;
    std::set<std::string> seenToolCalls_;
    std::optional<std::int64_t> pendingStartMs_;
    std::optional<std::string> marker_;
    std::optional<std::string> sessionId_;
    std::optional<std::int64_t> startTimeMs_;
};

}  // namespace agentspeed
