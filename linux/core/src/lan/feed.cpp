#include "agentspeed/lan_feed.hpp"

namespace agentspeed::lan {

std::shared_ptr<const Frame> Feed::build(const Tick& tick, const std::string& instance, const std::string& name, std::vector<double> history) {
    FrameInput input;
    input.instance = instance;
    input.name = name;
    input.run = run_;
    input.seq = ++seq_;
    input.atMs = tick.wallMs;
    input.live = tick.live;
    input.sources = tick.sources;
    input.history = std::move(history);
    return std::make_shared<const Frame>(buildFrame(input));
}

Feed::Built Feed::tick(const Tick& tick, const std::string& instance, const std::string& name) {
    const auto& samples = history_.tick(tick.monotonicMs, tick.live.outputTokS);
    auto frame = build(tick, instance, name, samples);
    const bool changed = !latest_ || !latest_->sameContent(*frame);
    latest_ = frame;
    return {std::move(frame), changed};
}

void Feed::stop() {
    history_.reset();
    latest_.reset();
}

std::shared_ptr<const Frame> Feed::single(const Tick& tick, const std::string& instance, const std::string& name) {
    return build(tick, instance, name, {roundTokS(tick.live.outputTokS)});
}

}  // namespace agentspeed::lan
