#pragma once

#include <cstdint>
#include <memory>
#include <optional>
#include <string>

#include "agentspeed/antigravity.hpp"
#include "agentspeed/line_splitter.hpp"
#include "agentspeed/locations.hpp"
#include "agentspeed/transcript_parser.hpp"
#include "agentspeed/zstd_frames.hpp"

// METRICS 5: reading what changed in one tracked transcript and handing it to its parser.
namespace agentspeed {

// How a tailer that starts inside a file treats the bytes before the first newline.
enum class Alignment { None, SkipPartialLine, SkipToNextLine };

struct TailResult {
    bool wasReset = false;
    std::int64_t length = 0;
    std::int64_t writeNs = 0;
    std::int64_t bytesRead = 0;
};

// The file disappeared between discovery and the read.
struct FileGone {};

class Tailer {
public:
    explicit Tailer(std::string path) : path_(std::move(path)) {}
    virtual ~Tailer() = default;

    const std::string& path() const { return path_; }
    std::int64_t offset() const { return offset_; }
    virtual std::int64_t oversizedLines() const { return 0; }
    virtual void startAt(std::int64_t offset, Alignment alignment = Alignment::None) = 0;
    // An older file, observed at this size and time: nothing is read until either changes.
    virtual void startAtEnd(std::int64_t length, std::int64_t writeNs);
    // Reads what changed since the last read; a replaced or shrunk file resets the parser first. Throws FileGone.
    virtual TailResult read(TranscriptParser& parser, ResponseSink& sink) = 0;

protected:
    struct Identity {
        std::int64_t device = 0, inode = 0;
        bool operator==(const Identity&) const = default;
    };

    std::string path_;
    std::int64_t offset_ = 0;
    std::optional<Identity> identity_;
};

// Plain JSONL (also Factory Droid with its sidecar, Grok Build with summary.json).
class LineTailer : public Tailer {
public:
    using Tailer::Tailer;
    std::int64_t oversizedLines() const override { return lines_.oversizedLines(); }
    void startAt(std::int64_t offset, Alignment alignment) override;
    void startAtEnd(std::int64_t length, std::int64_t writeNs) override;
    TailResult read(TranscriptParser& parser, ResponseSink& sink) override;

private:
    LineSplitter lines_;
    Alignment alignment_ = Alignment::None;
};

// DeepSeek Harness `.zstd`: independent frames appended over time (METRICS 5.1).
class FrameTailer : public Tailer {
public:
    using Tailer::Tailer;
    std::int64_t oversizedLines() const override;
    void startAt(std::int64_t offset, Alignment alignment) override;
    TailResult read(TranscriptParser& parser, ResponseSink& sink) override;

private:
    ZstdFrameScanner scanner_;
};

// Read again from the start whenever size, time or identity changes: Codex `.jsonl.zst` (5.3), Gemini `.json` (4.6).
class WholeFileTailer : public Tailer {
public:
    WholeFileTailer(std::string path, bool document) : Tailer(std::move(path)), document_(document) {}
    void startAt(std::int64_t offset, Alignment alignment) override;
    void startAtEnd(std::int64_t length, std::int64_t writeNs) override;
    TailResult read(TranscriptParser& parser, ResponseSink& sink) override;
    bool oversized() const { return oversized_; }

private:
    bool document_;
    bool oversized_ = false;
    std::optional<std::pair<std::int64_t, std::int64_t>> read_;  // length, write time (0: any)
};

// One Antigravity conversation database (METRICS 5.4): a complete first read, then rows above the cursors.
class DatabaseTailer : public Tailer {
public:
    using Tailer::Tailer;
    void startAt(std::int64_t offset, Alignment alignment) override;
    void startAtEnd(std::int64_t length, std::int64_t writeNs) override;
    TailResult read(TranscriptParser& parser, ResponseSink& sink) override;
    bool needsCompleteRead() const { return needsCompleteRead_; }
    std::int64_t lastReadMs() const { return lastReadMs_; }

private:
    antigravity::Cursors cursors_;
    std::optional<std::pair<std::int64_t, std::int64_t>> skipUntilChanged_;
    bool needsCompleteRead_ = true;
    std::int64_t lastReadMs_ = 0;
};

std::unique_ptr<Tailer> makeTailer(RootFamily family, const std::string& path);
std::unique_ptr<TranscriptParser> makeParser(RootFamily family, const std::string& path, const std::string& root, Deduper& deduper);

// Monotonic milliseconds.
std::int64_t steadyMs();

}  // namespace agentspeed
