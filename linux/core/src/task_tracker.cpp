#include "agentspeed/task_tracker.hpp"

#include "agentspeed/rfc3339.hpp"

namespace agentspeed::tasks {

void TaskTracker::onLine(const TranscriptParser& parser, const json::Value& line, std::string_view raw) {
    composer_.onLine(parser, line, raw);
    if (!live_ || composer_.lastDuplicate()) return;
    const auto text = line["timestamp"].string();
    const auto at = text ? parseTimestamp(*text) : std::nullopt;
    if (at) actions_.feed(parser.source(), line, *at);
}

void TaskTracker::onReset(const TranscriptParser&) {
    composer_.reset();
    actions_.clear();
}

}  // namespace agentspeed::tasks
