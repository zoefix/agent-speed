#pragma once

#include <cstdint>
#include <functional>
#include <string_view>
#include <vector>

#include "agentspeed/line_splitter.hpp"

struct ZSTD_DCtx_s;

namespace agentspeed {

// METRICS 5.1: a stream of independent zstd frames, decoded frame by frame. Feed it the bytes from the current offset;
// it consumes the complete frames (and skippable ones) and stops before an incomplete one. A frame that fails to decode
// counts as one malformed line; bytes that are not a frame count one and resynchronize at the next frame magic.
class ZstdFrameScanner {
public:
    static constexpr std::size_t kMaxFrameOutput = 64u << 20;

    ZstdFrameScanner();
    ~ZstdFrameScanner();
    ZstdFrameScanner(const ZstdFrameScanner&) = delete;
    ZstdFrameScanner& operator=(const ZstdFrameScanner&) = delete;

    // Returns the bytes consumed from the start of `data`.
    std::size_t scan(std::string_view data, const std::function<void(std::string_view)>& onLine,
                     const std::function<void()>& onMalformed);
    void reset();
    LineSplitter& lines() { return lines_; }

private:
    enum class Status { Complete, Incomplete, Invalid };
    static Status frameSize(std::string_view data, std::size_t& size);
    bool decode(std::string_view frame);

    ZSTD_DCtx_s* context_ = nullptr;
    std::vector<char> output_;
    LineSplitter lines_;
    bool resyncing_ = false;
};

}  // namespace agentspeed
