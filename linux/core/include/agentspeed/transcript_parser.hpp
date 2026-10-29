#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

#include "agentspeed/dedupe.hpp"
#include "agentspeed/model.hpp"
#include "agentspeed/modes.hpp"

namespace agentspeed {

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
    // A reading error outside the parser (a zstd decoding error, METRICS 5.3) counts as malformed lines.
    void addMalformed(std::int64_t count) { malformedLines_ += count; }

protected:
    void countMalformed() { ++malformedLines_; }
    void touch(std::int64_t timestampMs);
    void setProjectFromCwd(const std::optional<std::string>& cwd);
    void setModel(const std::optional<std::string>& model);
    void setSubagent(bool value);
    void setSource(SourceKind source);
    void setPhase(Phase phase, std::int64_t timestampMs, std::optional<std::int64_t> inFlightStartMs);
    void markChanged() { ++version_; }
    void setModes(const ExecutionModes& modes);
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
};

}  // namespace agentspeed
