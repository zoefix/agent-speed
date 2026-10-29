#include "agentspeed/tailer.hpp"

#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <chrono>
#include <vector>

#include "agentspeed/claude_parser.hpp"
#include "agentspeed/codex_parser.hpp"
#include "agentspeed/droid.hpp"
#include "agentspeed/dsh_parser.hpp"
#include "agentspeed/file_reader.hpp"
#include "agentspeed/fs_util.hpp"
#include "agentspeed/gemini_parser.hpp"
#include "agentspeed/grok_parser.hpp"
#include "agentspeed/kimi_parser.hpp"
#include "agentspeed/omp_parser.hpp"
#include "agentspeed/pi_parser.hpp"
#include "agentspeed/sqlite.hpp"
#include "agentspeed/workbuddy_parser.hpp"

namespace agentspeed {

namespace {

constexpr std::size_t kChunkBytes = 1 << 20;

// An open file descriptor with its fstat, closed on scope exit.
class OpenFile {
public:
    explicit OpenFile(const std::string& path) {
        fd_ = ::open(path.c_str(), O_RDONLY | O_CLOEXEC);
        if (fd_ < 0) {
            if (errno == ENOENT || errno == ENOTDIR) throw FileGone{};
            throw std::system_error(errno, std::generic_category(), "open");
        }
        struct stat st {};
        if (::fstat(fd_, &st) != 0) throw std::system_error(errno, std::generic_category(), "fstat");
        length = static_cast<std::int64_t>(st.st_size);
        writeNs = static_cast<std::int64_t>(st.st_mtim.tv_sec) * 1'000'000'000 + st.st_mtim.tv_nsec;
        device = static_cast<std::int64_t>(st.st_dev);
        inode = static_cast<std::int64_t>(st.st_ino);
    }
    ~OpenFile() {
        if (fd_ >= 0) ::close(fd_);
    }
    OpenFile(const OpenFile&) = delete;
    OpenFile& operator=(const OpenFile&) = delete;

    std::int64_t readAt(char* buffer, std::size_t size, std::int64_t offset) const {
        const auto n = ::pread(fd_, buffer, size, static_cast<off_t>(offset));
        if (n < 0) throw std::system_error(errno, std::generic_category(), "pread");
        return n;
    }

    std::int64_t length = 0, writeNs = 0, device = 0, inode = 0;

private:
    int fd_ = -1;
};

}  // namespace

std::int64_t steadyMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

void Tailer::startAtEnd(std::int64_t length, std::int64_t) { startAt(length, Alignment::SkipPartialLine); }

void LineTailer::startAt(std::int64_t offset, Alignment alignment) {
    offset_ = std::max<std::int64_t>(0, offset);
    alignment_ = offset_ > 0 ? alignment : Alignment::None;
    lines_.reset();
    if (alignment_ == Alignment::SkipToNextLine) lines_.alignToNextLine();
}

void LineTailer::startAtEnd(std::int64_t length, std::int64_t) { startAt(length, Alignment::SkipPartialLine); }

TailResult LineTailer::read(TranscriptParser& parser, ResponseSink& sink) {
    OpenFile file(path_);
    TailResult result;
    result.length = file.length;
    result.writeNs = file.writeNs;
    const Identity identity{file.device, file.inode};
    if (file.length < offset_ || (identity_ && *identity_ != identity)) {
        offset_ = 0;
        alignment_ = Alignment::None;
        lines_.reset();
        parser.reset();
        result.wasReset = true;
    }
    identity_ = identity;
    if (alignment_ == Alignment::SkipPartialLine && offset_ <= file.length) {
        char previous = 0;
        if (file.readAt(&previous, 1, offset_ - 1) != 1 || previous != '\n') lines_.alignToNextLine();
    }
    alignment_ = Alignment::None;

    auto* droidParser = dynamic_cast<droid::Parser*>(&parser);
    if (auto* grok = dynamic_cast<GrokParser*>(&parser)) grok->beginBatch(sink);
    if (droidParser) droidParser->beginRead(offset_);
    if (file.length > offset_) {
        std::vector<char> buffer(static_cast<std::size_t>(std::min<std::int64_t>(kChunkBytes, file.length - offset_)));
        while (offset_ < file.length) {
            const auto want = static_cast<std::size_t>(std::min<std::int64_t>(static_cast<std::int64_t>(buffer.size()), file.length - offset_));
            const auto got = file.readAt(buffer.data(), want, offset_);
            if (got <= 0) break;
            const std::string_view chunk(buffer.data(), static_cast<std::size_t>(got));
            lines_.feedPositioned(chunk, offset_, [&](std::string_view line, std::int64_t at, std::int64_t length) {
                if (droidParser) {
                    droidParser->feedPositioned(line, at, length);
                } else {
                    parser.feed(line, sink);
                }
            });
            offset_ += got;
            result.bytesRead += got;
        }
    }
    if (droidParser) {
        const auto sidecar = droid::readSidecar(droid::sidecarOf(path_), result.wasReset ? std::nullopt : droidParser->knownSidecar());
        droidParser->applyCycle(sidecar, sink);
        if (const auto s = fsx::statOf(droid::sidecarOf(path_)); s && s->regular && s->writeNs > result.writeNs) result.writeNs = s->writeNs;
    }
    if (dynamic_cast<GrokParser*>(&parser)) result.writeNs = grokModifiedNs(path_, result.writeNs);
    return result;
}

std::int64_t FrameTailer::oversizedLines() const { return const_cast<ZstdFrameScanner&>(scanner_).lines().oversizedLines(); }

void FrameTailer::startAt(std::int64_t offset, Alignment) {
    offset_ = std::max<std::int64_t>(0, offset);
    scanner_.reset();
}

TailResult FrameTailer::read(TranscriptParser& parser, ResponseSink& sink) {
    OpenFile file(path_);
    TailResult result{false, file.length, file.writeNs, 0};
    const Identity identity{file.device, file.inode};
    if (file.length < offset_ || (identity_ && *identity_ != identity)) {
        startAt(0, Alignment::None);
        parser.reset();
        result.wasReset = true;
    }
    identity_ = identity;
    if (file.length > offset_) {
        std::string data(static_cast<std::size_t>(file.length - offset_), '\0');
        std::size_t filled = 0;
        while (filled < data.size()) {
            const auto got = file.readAt(data.data() + filled, data.size() - filled, offset_ + static_cast<std::int64_t>(filled));
            if (got <= 0) break;
            filled += static_cast<std::size_t>(got);
        }
        data.resize(filled);
        const auto used = scanner_.scan(data, [&](std::string_view line) { parser.feed(line, sink); }, [&] { parser.addMalformed(1); });
        offset_ += static_cast<std::int64_t>(used);
        result.bytesRead = static_cast<std::int64_t>(used);
    }
    return result;
}

void WholeFileTailer::startAt(std::int64_t offset, Alignment) {
    offset_ = std::max<std::int64_t>(0, offset);
    read_ = offset_ > 0 ? std::optional<std::pair<std::int64_t, std::int64_t>>({offset_, 0}) : std::nullopt;
}

void WholeFileTailer::startAtEnd(std::int64_t length, std::int64_t writeNs) {
    startAt(length, Alignment::None);
    read_ = std::make_pair(offset_, writeNs);
}

TailResult WholeFileTailer::read(TranscriptParser& parser, ResponseSink& sink) {
    TailResult result;
    {
        OpenFile file(path_);
        result.length = file.length;
        result.writeNs = file.writeNs;
        const Identity identity{file.device, file.inode};
        const bool replaced = identity_ && *identity_ != identity;
        identity_ = identity;
        if (!replaced && read_ && read_->first == file.length && (read_->second == 0 || read_->second == file.writeNs)) {
            read_ = std::make_pair(file.length, file.writeNs);
            return result;
        }
        const bool wasRead = read_.has_value();
        read_ = std::make_pair(file.length, file.writeNs);
        offset_ = 0;
        // A document parser resets itself once a new version parses; a stream is replayed from the start.
        result.wasReset = wasRead && !document_;
        if (result.wasReset) parser.reset();
        if (document_) {
            oversized_ = file.length > static_cast<std::int64_t>(GeminiParser::kMaxDocumentBytes);
            if (!oversized_) {
                std::string text(static_cast<std::size_t>(file.length), '\0');
                std::size_t filled = 0;
                while (filled < text.size()) {
                    const auto got = file.readAt(text.data() + filled, text.size() - filled, static_cast<std::int64_t>(filled));
                    if (got <= 0) break;
                    filled += static_cast<std::size_t>(got);
                }
                text.resize(filled);
                parser.feed(text, sink);
                result.bytesRead = static_cast<std::int64_t>(filled);
            }
            offset_ = file.length;
            return result;
        }
    }
    const auto read = readWholeFile(path_, [&](std::string_view line) { parser.feed(line, sink); });
    if (!read.ok) throw FileGone{};
    parser.addMalformed(read.decodeErrors);
    offset_ = result.length;
    result.bytesRead = result.length;
    return result;
}

void DatabaseTailer::startAt(std::int64_t offset, Alignment) {
    offset_ = std::max<std::int64_t>(0, offset);
    if (offset_ == 0) skipUntilChanged_.reset();
}

void DatabaseTailer::startAtEnd(std::int64_t length, std::int64_t writeNs) {
    offset_ = length;
    skipUntilChanged_ = std::make_pair(length, writeNs);
}

TailResult DatabaseTailer::read(TranscriptParser& parser, ResponseSink& sink) {
    const auto stamp = sqlite::stampOf(path_);
    if (!stamp) throw FileGone{};
    TailResult result{false, stamp->length(), stamp->lastWriteNs(), 0};
    if (skipUntilChanged_) {
        if (*skipUntilChanged_ == std::make_pair(stamp->length(), stamp->lastWriteNs())) return result;
        skipUntilChanged_.reset();
    }
    if (const auto s = fsx::statOf(path_, true)) {
        const Identity identity{s->device, s->inode};
        if (identity_ && *identity_ != identity) {
            cursors_ = antigravity::Cursors();
            parser.reset();
            result.wasReset = true;
        }
        identity_ = identity;
    }
    lastReadMs_ = steadyMs();
    auto* conversation = dynamic_cast<antigravity::Parser*>(&parser);
    const auto database = antigravity::Database::open(path_);
    antigravity::Batch batch;
    if (!database->supported()) {
        batch.unsupported = true;
    } else {
        result.bytesRead = database->read(cursors_, batch) + static_cast<std::int64_t>(batch.count());
        if (needsCompleteRead_) result.bytesRead = std::max<std::int64_t>(1, stamp->databaseLength);
    }
    needsCompleteRead_ = false;
    offset_ = stamp->length();
    if (conversation) conversation->apply(batch, sink);
    return result;
}

std::unique_ptr<Tailer> makeTailer(RootFamily family, const std::string& path) {
    if (family == RootFamily::Antigravity) return std::make_unique<DatabaseTailer>(path);
    if (family != RootFamily::GrokBuild && family != RootFamily::Droid) {
        if (fsx::endsWithNoCase(path, ".zstd")) return std::make_unique<FrameTailer>(path);
        if (fsx::endsWithNoCase(path, ".jsonl.zst")) return std::make_unique<WholeFileTailer>(path, false);
        if (fsx::endsWithNoCase(path, ".json")) return std::make_unique<WholeFileTailer>(path, true);
    }
    return std::make_unique<LineTailer>(path);
}

std::unique_ptr<TranscriptParser> makeParser(RootFamily family, const std::string& path, const std::string& root, Deduper& deduper) {
    switch (family) {
        case RootFamily::Codex: return std::make_unique<CodexParser>(path, deduper);
        case RootFamily::DeepSeek: return std::make_unique<DshParser>(path, deduper);
        case RootFamily::Gemini: return std::make_unique<GeminiParser>(path, deduper);
        case RootFamily::Omp: return std::make_unique<OmpParser>(path, deduper, root);
        case RootFamily::Antigravity: return std::make_unique<antigravity::Parser>(path, deduper);
        case RootFamily::WorkBuddy: return std::make_unique<WorkBuddyParser>(path, deduper, SourceKind::WorkBuddy, root);
        case RootFamily::WorkBuddyAi: return std::make_unique<WorkBuddyParser>(path, deduper, SourceKind::WorkBuddyAi, root);
        case RootFamily::Kimi: return std::make_unique<KimiParser>(path, deduper);
        case RootFamily::GrokBuild: return std::make_unique<GrokParser>(path, deduper);
        case RootFamily::Pi: return PiParser::forPath(path, deduper);
        case RootFamily::Droid: return std::make_unique<droid::Parser>(path, deduper);
        default: return std::make_unique<ClaudeParser>(path, deduper);
    }
}

}  // namespace agentspeed
