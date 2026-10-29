#include "agentspeed/zstd_frames.hpp"

#include <zstd.h>
#include <zstd_errors.h>

#include <cstring>

namespace agentspeed {

namespace {

constexpr std::uint32_t kFrameMagic = 0xFD2FB528u;
constexpr std::uint32_t kSkippableMask = 0xFFFFFFF0u;
constexpr std::uint32_t kSkippableMagic = 0x184D2A50u;
constexpr std::size_t kChunk = 128 * 1024;

std::uint32_t readU32(const char* p) {
    const auto* b = reinterpret_cast<const unsigned char*>(p);
    return static_cast<std::uint32_t>(b[0]) | (static_cast<std::uint32_t>(b[1]) << 8) |
           (static_cast<std::uint32_t>(b[2]) << 16) | (static_cast<std::uint32_t>(b[3]) << 24);
}

std::ptrdiff_t indexOfMagic(std::string_view data) {
    for (std::size_t i = 0; i + 4 <= data.size(); ++i) {
        const std::uint32_t value = readU32(data.data() + i);
        if (value == kFrameMagic || (value & kSkippableMask) == kSkippableMagic) return static_cast<std::ptrdiff_t>(i);
    }
    return -1;
}

}  // namespace

ZstdFrameScanner::ZstdFrameScanner() = default;

ZstdFrameScanner::~ZstdFrameScanner() {
    if (context_) ZSTD_freeDCtx(context_);
}

void ZstdFrameScanner::reset() {
    lines_.reset();
    resyncing_ = false;
}

ZstdFrameScanner::Status ZstdFrameScanner::frameSize(std::string_view data, std::size_t& size) {
    size = 0;
    const std::size_t result = ZSTD_findFrameCompressedSize(data.data(), data.size());
    if (ZSTD_isError(result)) {
        return ZSTD_getErrorCode(result) == ZSTD_error_srcSize_wrong ? Status::Incomplete : Status::Invalid;
    }
    if (result == 0 || result > data.size()) return Status::Invalid;
    size = result;
    return Status::Complete;
}

bool ZstdFrameScanner::decode(std::string_view frame) {
    if (!context_) {
        context_ = ZSTD_createDCtx();
    } else {
        ZSTD_DCtx_reset(context_, ZSTD_reset_session_only);
    }
    output_.clear();
    std::vector<char> chunk(kChunk);
    ZSTD_inBuffer input{frame.data(), frame.size(), 0};
    while (true) {
        ZSTD_outBuffer out{chunk.data(), chunk.size(), 0};
        const std::size_t hint = ZSTD_decompressStream(context_, &out, &input);
        if (ZSTD_isError(hint)) return false;
        if (out.pos > kMaxFrameOutput - output_.size()) return false;
        output_.insert(output_.end(), chunk.data(), chunk.data() + out.pos);
        if (hint == 0) return true;
        if (input.pos == input.size && out.pos < out.size) return false;
    }
}

std::size_t ZstdFrameScanner::scan(std::string_view data, const std::function<void(std::string_view)>& onLine,
                                   const std::function<void()>& onMalformed) {
    std::size_t position = 0;
    while (position < data.size()) {
        const std::string_view rest = data.substr(position);
        if (resyncing_) {
            const auto next = indexOfMagic(rest);
            if (next < 0) return std::max(position, data.size() >= 3 ? data.size() - 3 : std::size_t{0});
            resyncing_ = false;
            position += static_cast<std::size_t>(next);
            continue;
        }
        std::size_t size = 0;
        const Status status = frameSize(rest, size);
        if (status == Status::Incomplete) return position;
        if (status == Status::Invalid) {
            resyncing_ = true;
            onMalformed();
            ++position;
            continue;
        }
        const std::string_view frame = rest.substr(0, size);
        position += size;
        if ((readU32(frame.data()) & kSkippableMask) == kSkippableMagic) continue;
        if (decode(frame)) {
            lines_.feed(std::string_view(output_.data(), output_.size()), onLine);
        } else {
            onMalformed();
        }
    }
    return position;
}

}  // namespace agentspeed
