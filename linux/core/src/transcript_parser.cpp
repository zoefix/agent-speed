#include "agentspeed/transcript_parser.hpp"

#include "agentspeed/path_text.hpp"

namespace agentspeed {

TranscriptParser::TranscriptParser(std::string agentId, Deduper& deduper, SourceKind initialSource)
    : agentId_(std::move(agentId)), deduper_(deduper), initialSource_(initialSource), source_(initialSource) {}

void TranscriptParser::observeText(std::string_view raw) {
    if (!observer_ || json::isBlank(raw)) return;
    json::Document doc;
    if (doc.parse(raw) && doc.root().isObject()) observer_->onLine(*this, doc.root(), raw);
}

void TranscriptParser::reset() {
    if (observer_) observer_->onReset(*this);
    source_ = initialSource_;
    sourceDecided_ = false;
    project_.reset();
    cwdPath_.reset();
    model_.reset();
    modes_ = ExecutionModes();
    isSubagent_ = false;
    phase_ = Phase::Idle;
    phaseSinceMs_.reset();
    inFlightStartMs_.reset();
    lastLineMs_.reset();
    ++version_;
}

void TranscriptParser::touch(std::int64_t timestampMs) {
    if (!lastLineMs_ || timestampMs > *lastLineMs_) {
        lastLineMs_ = timestampMs;
        ++version_;
    }
}

void TranscriptParser::setProjectFromCwd(const std::optional<std::string>& cwd) {
    if (!cwd) return;
    auto project = path_text::basename(*cwd);
    if (!project.empty() && project != project_) {
        project_ = std::move(project);
        ++version_;
    }
    if (cwdPath_ != cwd) {
        cwdPath_ = cwd;
        ++version_;
    }
}

void TranscriptParser::setModel(const std::optional<std::string>& model) {
    if (model && model != model_) {
        model_ = model;
        ++version_;
    }
}

void TranscriptParser::setProject(const std::optional<std::string>& project) {
    if (project != project_) {
        project_ = project;
        ++version_;
    }
}

void TranscriptParser::setModes(const ExecutionModes& modes) {
    if (!(modes == modes_)) {
        modes_ = modes;
        ++version_;
    }
}

void TranscriptParser::setSubagent(bool value) {
    if (isSubagent_ != value) {
        isSubagent_ = value;
        ++version_;
    }
}

void TranscriptParser::setSource(SourceKind source) {
    sourceDecided_ = true;
    if (source_ != source) {
        source_ = source;
        ++version_;
    }
}

void TranscriptParser::setPhase(Phase phase, std::int64_t timestampMs, std::optional<std::int64_t> inFlightStartMs) {
    if (phase_ != phase) {
        phase_ = phase;
        phaseSinceMs_ = timestampMs;
        ++version_;
    }
    const auto inFlight = phase == Phase::Generating ? inFlightStartMs : std::nullopt;
    if (inFlightStartMs_ != inFlight) {
        inFlightStartMs_ = inFlight;
        ++version_;
    }
}

}  // namespace agentspeed
