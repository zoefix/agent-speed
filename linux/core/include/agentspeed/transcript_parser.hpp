#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

#include "agentspeed/dedupe.hpp"
#include "agentspeed/json.hpp"
#include "agentspeed/model.hpp"
#include "agentspeed/modes.hpp"
#include "agentspeed/parents.hpp"

namespace agentspeed {

class TranscriptParser;

// Sees every line that parsed as a JSON object, right after its parser handled it (METRICS 6.9 Composing the stream).
class LineObserver {
public:
    virtual ~LineObserver() = default;
    virtual void onLine(const TranscriptParser& parser, const json::Value& line, std::string_view raw) = 0;
};

// What the task composer needs to skip copied history like the parser does.
struct CopyFacts {
    std::optional<std::int64_t> createdMs;     // Codex: the session_meta time; omp, Pi, OpenClaw: the header time
    std::optional<std::int64_t> startOrdinal;  // Codex: subagent_history_start_ordinal
    bool mirrorWindow = false;                 // OpenClaw: a window that only mirrors delegated turns
};

// A per-file state machine fed complete lines in order (METRICS 4).
class TranscriptParser {
public:
    TranscriptParser(std::string agentId, Deduper& deduper, SourceKind initialSource);
    virtual ~TranscriptParser() = default;

    virtual void feed(std::string_view line, ResponseSink& sink) = 0;
    virtual void reset();

    const std::string& agentId() const { return agentId_; }
    SourceKind source() const { return source_; }
    const std::optional<std::string>& project() const { return project_; }
    const std::optional<std::string>& cwdPath() const { return cwdPath_; }
    const std::optional<std::string>& model() const { return model_; }
    const ExecutionModes& modes() const { return modes_; }
    bool isSubagent() const { return isSubagent_; }
    Phase phase() const { return phase_; }
    std::optional<std::int64_t> phaseSinceMs() const { return phaseSinceMs_; }
    std::optional<std::int64_t> inFlightStartMs() const { return inFlightStartMs_; }
    std::optional<std::int64_t> lastLineMs() const { return lastLineMs_; }
    std::int64_t malformedLines() const { return malformedLines_; }
    std::uint64_t version() const { return version_; }
    // What METRICS 6.7 needs to find this agent's parent.
    virtual AgentLink link() const { return {agentId_, LinkKind::Path, std::nullopt, std::nullopt, std::nullopt, 0}; }
    // A reading error outside the parser (a zstd decoding error, METRICS 5.3) counts as malformed lines.
    void addMalformed(std::int64_t count) { malformedLines_ += count; }
    virtual CopyFacts copyFacts() const { return {}; }
    void setLineObserver(LineObserver* observer) { observer_ = observer; }
    LineObserver* lineObserver() const { return observer_; }
    const Deduper& dedupeSet() const { return deduper_; }

protected:
    void observe(const json::Value& line, std::string_view raw) {
        if (observer_) observer_->onLine(*this, line, raw);
    }
    // Parses the line again for the observer (parsers that keep no document).
    void observeText(std::string_view raw);
    void countMalformed() { ++malformedLines_; }
    void touch(std::int64_t timestampMs);
    void setProjectFromCwd(const std::optional<std::string>& cwd);
    void setModel(const std::optional<std::string>& model);
    void setSubagent(bool value);
    void setSource(SourceKind source);
    void setPhase(Phase phase, std::int64_t timestampMs, std::optional<std::int64_t> inFlightStartMs);
    void markChanged() { ++version_; }
    void setModes(const ExecutionModes& modes);
    void setProject(const std::optional<std::string>& project);
    void clearModel() {
        if (model_) {
            model_.reset();
            ++version_;
        }
    }
    bool sourceDecided() const { return sourceDecided_; }
    Deduper& deduper() { return deduper_; }

private:
    std::string agentId_;
    Deduper& deduper_;
    SourceKind initialSource_;
    SourceKind source_;
    bool sourceDecided_ = false;
    std::optional<std::string> project_;
    std::optional<std::string> cwdPath_;
    std::optional<std::string> model_;
    ExecutionModes modes_;
    bool isSubagent_ = false;
    Phase phase_ = Phase::Idle;
    std::optional<std::int64_t> phaseSinceMs_;
    std::optional<std::int64_t> inFlightStartMs_;
    std::optional<std::int64_t> lastLineMs_;
    std::int64_t malformedLines_ = 0;
    std::uint64_t version_ = 0;
    LineObserver* observer_ = nullptr;
};

}  // namespace agentspeed
