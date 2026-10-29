#pragma once

#include <map>
#include <set>
#include <string>
#include <utility>
#include <vector>

#include "agentspeed/engine.hpp"
#include "agentspeed/monitor.hpp"

namespace agentspeed {

// Keeps every response of today and of the retention window by key (METRICS 4.4 upserts, 6.1) and raises
// responseCompleted for first emissions while running (6.4).
class Monitor::Store : public ResponseSink {
public:
    static constexpr std::int64_t kRetentionMs = 30 * 60'000;
    static constexpr std::int64_t kCompletionWindowMs = 120'000;

    explicit Store(const std::set<SourceKind>* enabled = nullptr) : enabled_(enabled) {}
    void setEnabled(const std::set<SourceKind>* enabled) { enabled_ = enabled; }

    void onResponse(const ParsedResponse& r, bool completion) override {
        if (enabled_ && !enabled_->contains(r.source)) return;
        if (completion && live_ && !seeding_ && r.endMs >= nowMs_ - kCompletionWindowMs) {
            Completion c;
            c.source = r.source;
            c.output = r.tokens.output;
            if (r.startMs && r.endMs > *r.startMs) {
                c.speed = static_cast<double>(r.tokens.output) * 1000.0 / static_cast<double>(r.endMs - *r.startMs);
            }
            completions_.push_back(c);
        }
        auto& slot = byKey_[r.key];
        slot.parsed = r;
        engine::Response& e = slot.engine;
        e.key = r.key;
        e.agentId = r.agentId;
        e.source = r.source;
        e.model = r.model;
        e.startMs = r.startMs;
        e.endMs = r.endMs;
        e.input = r.tokens.input;
        e.output = r.tokens.output;
        e.final = r.final;
        e.requestCount = r.requestCount;
    }

    void goLive() { live_ = true; }
    void setSeeding(bool seeding) { seeding_ = seeding; }
    void setClock(std::int64_t nowMs) { nowMs_ = nowMs; }
    std::vector<Completion> takeCompletions() { return std::exchange(completions_, {}); }

    void prune(std::int64_t nowMs, std::int64_t todayStartMs) {
        const auto keepFrom = std::min(todayStartMs, nowMs - kRetentionMs);
        for (auto it = byKey_.begin(); it != byKey_.end();) {
            it = it->second.engine.endMs < keepFrom ? byKey_.erase(it) : std::next(it);
        }
    }

    std::vector<engine::Response> all() const {
        std::vector<engine::Response> out;
        out.reserve(byKey_.size());
        for (const auto& [_, s] : byKey_) out.push_back(s.engine);
        return out;
    }

    std::vector<ParsedResponse> responses() const {
        std::vector<ParsedResponse> out;
        out.reserve(byKey_.size());
        for (const auto& [_, s] : byKey_) out.push_back(s.parsed);
        return out;
    }

private:
    struct Slot {
        ParsedResponse parsed;
        engine::Response engine;
    };
    const std::set<SourceKind>* enabled_;
    std::map<std::string, Slot> byKey_;
    std::vector<Completion> completions_;
    bool live_ = false;
    bool seeding_ = false;
    std::int64_t nowMs_ = 0;
};

}  // namespace agentspeed
