#pragma once

#include <map>
#include <memory>
#include <set>
#include <stdexcept>

#include "agentspeed/transcript_parser.hpp"

namespace agentspeed::sqlite {
class Connection;
}

// METRICS 4.8 and 5.4: Antigravity keeps one SQLite database per conversation, with protobuf blobs.
namespace agentspeed::antigravity {

struct FormatError : std::runtime_error {
    using std::runtime_error::runtime_error;
};

struct Usage {
    std::int64_t input = 0, output = 0, cacheRead = 0, thinking = 0, response = 0;
    std::optional<std::string> key;
    // `service_tier` (13) when non-empty.
    std::optional<std::string> tier;
    bool hasTokens() const { return input > 0 || output > 0 || cacheRead > 0 || thinking > 0 || response > 0; }
    TokenCounts tokens() const;
};

struct Step {
    std::optional<std::int64_t> createdMs, viewableMs, finishedGeneratingMs, completedMs, lastChunkMs, startedMs;
    std::optional<Usage> usage;
    std::vector<std::int64_t> times() const;
    std::optional<std::int64_t> latestTime() const;
};

struct StepRow {
    std::int64_t idx = 0, stepType = 0, status = 0;
    std::optional<Step> step;
};

struct Generation {
    // `chat_model.response_model` and `chat_model.model_display_name`.
    std::optional<std::string> responseModel, label;
    std::int64_t idx = 0;
    std::set<std::uint64_t> stepIndices;
    std::optional<std::string> model;
};

struct Executor {
    std::int64_t idx = 0, terminationReason = 0, lastStepIdx = 0;
    std::optional<std::string> model;
    // The token of `planner_config.completion_config_override.thinking_level`, the API-key path.
    std::optional<std::string> thinking;
};

struct Trajectory {
    std::optional<std::int64_t> createdMs;
    std::optional<std::string> parentConversationId, project;
    bool isSubagent = false;
};

// Decoders of the blobs (throw FormatError).
Step decodeStep(std::string_view blob);
Generation decodeGeneration(std::int64_t idx, std::string_view blob);
Executor decodeExecutor(std::int64_t idx, std::string_view blob);
Trajectory decodeTrajectory(std::string_view blob);
std::optional<std::string> uriBasename(const std::string& value);
std::string conversationId(const std::string& databasePath);
// The store folder (antigravity-cli, antigravity, antigravity-ide), shown when a conversation names no workspace.
std::optional<std::string> storeNameOf(const std::string& databasePath);

struct Batch {
    std::optional<Trajectory> trajectory;
    std::vector<Generation> generations;
    std::vector<Executor> executors;
    std::vector<StepRow> steps;
    bool unsupported = false;
    int malformed = 0;
    std::size_t count() const { return steps.size() + generations.size() + executors.size() + (trajectory ? 1 : 0); }
};

class Parser : public TranscriptParser {
public:
    Parser(std::string agentId, Deduper& deduper);
    // Databases are not read as lines.
    void feed(std::string_view, ResponseSink&) override {}
    void reset() override;
    AgentLink link() const override;
    void apply(const Batch& batch, ResponseSink& sink);

    const std::string& conversation() const { return conversationId_; }
    std::optional<std::int64_t> createdMs() const { return createdMs_; }
    const std::optional<std::string>& parentConversationId() const { return parentConversationId_; }
    bool unsupported() const { return unsupported_; }

private:
    struct Emitted {
        ParsedResponse response;
        std::int64_t idx;
        Usage usage;
    };
    void applyStep(const StepRow& row, ResponseSink& sink);
    std::optional<std::string> modelFor(std::int64_t idx) const;
    const Generation* generationOf(std::int64_t idx) const;
    const Executor* executorCovering(std::int64_t idx) const;
    ExecutionModes modesFor(std::int64_t idx, const Usage& usage) const;
    void resolveModels(ResponseSink& sink);
    void emitResponse(const ParsedResponse& response, std::int64_t idx, const Usage& usage, bool completion, ResponseSink& sink);
    void updateAgentModel();
    void updatePhase();

    std::string conversationId_;
    std::map<std::int64_t, Generation> generations_;
    std::map<std::int64_t, Executor> executors_;
    std::map<std::string, Emitted> responses_;
    std::optional<StepRow> latest_;
    std::optional<std::int64_t> createdMs_;
    std::optional<std::string> parentConversationId_;
    bool unsupported_ = false;
};

struct Cursors {
    static constexpr std::size_t kMaxOpenSteps = 256;
    std::int64_t steps = -1, generations = -1, executors = -1;
    std::set<std::int64_t> openSteps;
    std::set<std::int64_t> malformedSteps;
    bool trajectoryFound = false;
    bool trajectoryMalformed = false;
};

class Database {
public:
    static constexpr std::int64_t kMaxBlobBytes = 1024 * 1024;
    // Throws sqlite::Error when the database cannot be opened.
    static std::unique_ptr<Database> open(const std::string& path);
    ~Database();
    bool immutable() const { return immutable_; }
    bool supported() const { return supported_; }
    // One short read transaction: the rows above the cursors and the current versions of open steps.
    std::int64_t read(Cursors& cursors, Batch& batch);

private:
    Database(std::unique_ptr<sqlite::Connection> connection, bool immutable, bool supported);
    std::unique_ptr<sqlite::Connection> connection_;
    bool immutable_;
    bool supported_;
};

}  // namespace agentspeed::antigravity
