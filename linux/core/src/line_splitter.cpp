#include "agentspeed/line_splitter.hpp"

namespace agentspeed {

void LineSplitter::deliver(std::string_view line, const LineHandler& onLine) {
    if (line.size() > kMaxLineBytes) {
        ++oversized_;
        return;
    }
    if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
    onLine(line);
}

void LineSplitter::feed(std::string_view data, const LineHandler& onLine) {
    if (align_) {
        const auto newline = data.find('\n');
        if (newline == std::string_view::npos) return;
        data.remove_prefix(newline + 1);
        align_ = false;
    }
    while (!data.empty()) {
        const auto newline = data.find('\n');
        if (newline == std::string_view::npos) {
            if (skipping_) return;
            if (partial_.size() + data.size() > kMaxLineBytes) {
                partial_.clear();
                skipping_ = true;
                return;
            }
            partial_.append(data);
            return;
        }
        const auto segment = data.substr(0, newline);
        data.remove_prefix(newline + 1);
        if (skipping_) {
            skipping_ = false;
            ++oversized_;
            continue;
        }
        if (partial_.empty()) {
            deliver(segment, onLine);
        } else if (partial_.size() + segment.size() > kMaxLineBytes) {
            partial_.clear();
            ++oversized_;
        } else {
            partial_.append(segment);
            deliver(partial_, onLine);
            partial_.clear();
        }
    }
}

void LineSplitter::feedPositioned(std::string_view data, std::int64_t offset, const PositionedHandler& onLine) {
    if (align_) {
        const auto newline = data.find('\n');
        if (newline == std::string_view::npos) return;
        data.remove_prefix(newline + 1);
        offset += static_cast<std::int64_t>(newline + 1);
        align_ = false;
    }
    while (!data.empty()) {
        const auto newline = data.find('\n');
        if (newline == std::string_view::npos) {
            if (skipping_) return;
            if (partial_.size() + data.size() > kMaxLineBytes) {
                partial_.clear();
                skipping_ = true;
                return;
            }
            if (partial_.empty()) partialOffset_ = offset;
            partial_.append(data);
            return;
        }
        const auto segment = data.substr(0, newline);
        const std::int64_t segmentOffset = offset;
        data.remove_prefix(newline + 1);
        offset += static_cast<std::int64_t>(newline + 1);
        if (skipping_) {
            skipping_ = false;
            ++oversized_;
            continue;
        }
        std::string_view line = segment;
        std::int64_t lineOffset = segmentOffset;
        if (!partial_.empty()) {
            if (partial_.size() + segment.size() > kMaxLineBytes) {
                partial_.clear();
                ++oversized_;
                continue;
            }
            partial_.append(segment);
            line = partial_;
            lineOffset = partialOffset_;
        }
        if (line.size() > kMaxLineBytes) {
            ++oversized_;
        } else {
            const auto length = static_cast<std::int64_t>(line.size());
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            onLine(line, lineOffset, length);
        }
        partial_.clear();
    }
}

void LineSplitter::reset() {
    partial_.clear();
    skipping_ = false;
    align_ = false;
}

}  // namespace agentspeed
