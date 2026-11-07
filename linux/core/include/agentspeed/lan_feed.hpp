#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <string>

#include "agentspeed/lan_frame.hpp"

// The feed of LAN 3.3: while it runs, a frame per engine tick with `seq` + 1, the history and the changed flag.
namespace agentspeed::lan {

struct Tick {
    std::int64_t monotonicMs = 0, wallMs = 0;
    LiveValues live;
    std::map<SourceKind, LiveValues> sources;
};

class Feed {
public:
    // `run`: 16 random hex digits, new at every launch.
    explicit Feed(std::string run) : run_(std::move(run)) {}

    struct Built {
        std::shared_ptr<const Frame> frame;
        bool changed = true;
    };
    Built tick(const Tick& tick, const std::string& instance, const std::string& name);
    // Neither the server nor a target is active: the history and the last frame are dropped; `seq` continues.
    void stop();
    // One frame outside the feed (Send Test while it is stopped): its own one-sample history, `seq` + 1.
    std::shared_ptr<const Frame> single(const Tick& tick, const std::string& instance, const std::string& name);

    bool running() const { return latest_ != nullptr; }
    const std::shared_ptr<const Frame>& latest() const { return latest_; }
    std::int64_t seq() const { return seq_; }
    const std::string& run() const { return run_; }

private:
    std::shared_ptr<const Frame> build(const Tick& tick, const std::string& instance, const std::string& name, std::vector<double> history);

    std::string run_;
    std::int64_t seq_ = 0;
    History history_;
    std::shared_ptr<const Frame> latest_;
};

}  // namespace agentspeed::lan
