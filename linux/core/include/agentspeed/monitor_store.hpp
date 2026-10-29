#pragma once

#include <map>
#include <set>
#include <string>
#include <utility>
#include <vector>

#include "agentspeed/engine.hpp"
#include "agentspeed/monitor.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/pricing.hpp"
#include "agentspeed/report_values.hpp"
#include "agentspeed/tz.hpp"

namespace agentspeed {

// Keeps every response of today and of the retention window by key (METRICS 4.4 upserts, 6.1), raises
// responseCompleted for first emissions while running (6.4) and keeps the report ledger (11.4).
class Monitor::Store : public ResponseSink {
public:
    static constexpr std::int64_t kRetentionMs = 30 * 60'000;
    static constexpr std::int64_t kCompletionWindowMs = 120'000;

    explicit Store(const std::set<SourceKind>* enabled = nullptr) : enabled_(enabled) {}
    void setEnabled(const std::set<SourceKind>* enabled) { enabled_ = enabled; }
    // METRICS 9.3, 9.7: every response is priced with the table bound to its local date.
    void setPricing(std::shared_ptr<pricing::PriceBook> book, std::shared_ptr<const TimeZone> zone) {
        book_ = std::move(book);
        zone_ = std::move(zone);
    }
    std::string localDate(std::int64_t ms) const {
        return pricing::UtcDay(ms + (zone_ ? zone_->offsetMs(ms) : 0)).label();
    }

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
        if (r.provider) providers_[r.agentId] = *r.provider;
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
        e.reasoning = r.tokens.reasoning;
        e.final = r.final;
        e.requestCount = r.requestCount;
        e.costMicroUsd = book_ ? book_->cost(r, localDate(r.endMs)) : std::nullopt;
        if (completion && live_ && !seeding_ && r.endMs >= nowMs_ - kCompletionWindowMs) {
            ledger_.count(r, e.costMicroUsd, reportModelId(r.model, r.provider));
        } else if (ledger_.counted(r.key)) {
            ledger_.grow(r, e.costMicroUsd, reportModelId(r.model, r.provider));
        }
    }

    // The id a model is reported as (METRICS 11.3), by the current table.
    std::string reportModelId(const std::optional<std::string>& model, const std::optional<std::string>& provider) const {
        return (book_ ? book_->current() : pricing::PriceBook::bundled())->reportModelId(model, provider);
    }
    std::optional<std::string> providerOf(const std::string& agentId) const {
        const auto it = providers_.find(agentId);
        return it == providers_.end() ? std::nullopt : std::optional<std::string>(it->second);
    }
    const report::Ledger& ledger() const { return ledger_; }

    // A new current table prices today's unpriced responses again (METRICS 9.7).
    void reprice() {
        if (!book_) return;
        for (auto& [_, slot] : byKey_) {
            if (slot.engine.costMicroUsd) continue;
            slot.engine.costMicroUsd = book_->cost(slot.parsed, localDate(slot.parsed.endMs));
            if (slot.engine.costMicroUsd && ledger_.counted(slot.parsed.key)) {
                ledger_.grow(slot.parsed, slot.engine.costMicroUsd, reportModelId(slot.parsed.model, slot.parsed.provider));
            }
        }
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
        ledger_.prune(keepFrom);
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
    std::shared_ptr<pricing::PriceBook> book_;
    std::shared_ptr<const TimeZone> zone_;
    std::map<std::string, Slot> byKey_;
    std::vector<Completion> completions_;
    std::map<std::string, std::string> providers_;
    report::Ledger ledger_;
    bool live_ = false;
    bool seeding_ = false;
    std::int64_t nowMs_ = 0;
};

}  // namespace agentspeed
