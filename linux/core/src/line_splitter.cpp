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

void LineSplitter::reset() {
    partial_.clear();
    skipping_ = false;
}

}  // namespace agentspeed
