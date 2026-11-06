#include "agentspeed/remote_protocol.hpp"

namespace agentspeed::remote {

std::vector<std::string> LineFramer::consume(std::string_view chunk, int& oversized) {
    std::vector<std::string> lines;
    while (!chunk.empty()) {
        const auto newline = chunk.find('\n');
        const auto part = chunk.substr(0, newline);
        if (!skipping_) {
            if (partial_.size() + part.size() > max_) {
                partial_.clear();
                skipping_ = true;
                ++oversized;
            } else {
                partial_.append(part);
            }
        }
        if (newline == std::string_view::npos) break;
        if (!skipping_) {
            if (!partial_.empty() && partial_.back() == '\r') partial_.pop_back();
            if (!partial_.empty()) lines.push_back(std::move(partial_));
        }
        partial_.clear();
        skipping_ = false;
        chunk.remove_prefix(newline + 1);
    }
    return lines;
}

}  // namespace agentspeed::remote
