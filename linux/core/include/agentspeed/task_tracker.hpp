#pragma once

#include <string>

#include "agentspeed/task_events.hpp"
#include "agentspeed/work_signals.hpp"

namespace agentspeed::tasks {

// One agent's task state: the composed event stream (every line, seeded ones included) and the action log of the
// work signals (live lines only, METRICS 6.10 Storage).
class TaskTracker : public LineObserver {
public:
    TaskTracker(std::string path, SourceKind source) : composer_(std::move(path), source) {}

    void onLine(const TranscriptParser& parser, const json::Value& line, std::string_view raw) override;
    void onReset(const TranscriptParser&) override;
    void setLive(bool live) { live_ = live; }

    const Composer& composer() const { return composer_; }
    const work::ActionLog& actions() const { return actions_; }

private:
    Composer composer_;
    work::ActionLog actions_;
    bool live_ = true;
};

}  // namespace agentspeed::tasks
