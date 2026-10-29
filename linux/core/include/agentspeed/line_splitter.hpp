#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <string_view>

namespace agentspeed {

// METRICS 5: splits bytes into lines at '\n', drops one trailing '\r', buffers an incomplete last line, and skips lines
// longer than 32 MiB.
class LineSplitter {
public:
    static constexpr std::size_t kMaxLineBytes = 32u * 1024u * 1024u;
    using LineHandler = std::function<void(std::string_view)>;

    void feed(std::string_view data, const LineHandler& onLine);
    using PositionedHandler = std::function<void(std::string_view line, std::int64_t offset, std::int64_t length)>;
    // Like feed, for bytes that start at file offset `offset`; lines are delivered with their offset and raw length.
    void feedPositioned(std::string_view data, std::int64_t offset, const PositionedHandler& onLine);
    void reset();
    bool hasPartial() const { return !partial_.empty() || skipping_; }
    std::int64_t oversizedLines() const { return oversized_; }
    // Drops the bytes up to and including the next '\n' (reading started inside a line).
    void alignToNextLine() { align_ = true; }

private:
    void deliver(std::string_view line, const LineHandler& onLine);

    std::string partial_;
    std::int64_t partialOffset_ = 0;
    bool skipping_ = false;
    bool align_ = false;
    std::int64_t oversized_ = 0;
};

}  // namespace agentspeed
