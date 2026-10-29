#include <algorithm>
#include <cmath>
#include <random>

#include "agentspeed/report_scheduler.hpp"

namespace agentspeed::report {

const char* activityName(Activity activity) {
    switch (activity) {
    case Activity::Active: return "active";
    case Activity::Idle: return "idle";
    case Activity::Leaving: return "leaving";
    }
    return "";
}

Scheduler::Scheduler(const Limits& limits, bool paused, bool networkOff, std::function<double()> jitter)
    : jitter_(std::move(jitter)), paused_(paused), networkOff_(networkOff) {
    update(limits);
    if (!jitter_) {
        jitter_ = [] {
            static thread_local std::mt19937_64 engine{std::random_device{}()};
            return std::uniform_real_distribution<double>(0.8, 1.2)(engine);
        };
    }
}

std::optional<std::int64_t> Scheduler::nextDueAt() const {
    if (!windowStart_ || mode_ == Mode::Quiet) return std::nullopt;
    return *windowStart_ + (mode_ == Mode::Active ? intervalMs() : heartbeatMs());
}

void Scheduler::update(const Limits& limits) {
    limitsIntervalMs_ = limits.intervalSeconds * std::int64_t{1000};
    limitsHeartbeatMs_ = limits.heartbeatSeconds * std::int64_t{1000};
    maxAgeMs_ = limits.maxAgeSeconds * std::int64_t{1000};
}

Actions Scheduler::startRun() {
    Actions actions;
    dropOutbox(actions);
    seq_ = 0;
    windowStart_.reset();
    lastActive_.reset();
    mode_ = Mode::Quiet;
    stopped_ = false;
    return actions;
}

Actions Scheduler::tick(std::int64_t t, bool active, bool blocked, const std::vector<Event>& events) {
    Actions actions;
    bool leaving = false;
    blocked_ = blocked;
    for (const auto event : events) {
        switch (event) {
        case Event::Pause:
            leaving |= !paused_ && !stopped_;
            paused_ = true;
            break;
        case Event::Resume: paused_ = false; break;
        case Event::NetworkOff:
            networkOff_ = true;
            dropOutbox(actions);
            break;
        case Event::NetworkOn: networkOff_ = false; break;
        case Event::Stop:
            leaving |= !paused_ && !stopped_;
            stopped_ = true;
            break;
        }
    }

    if (previousTick_ && t - *previousTick_ > kGapMs) {
        windowStart_ = t;
        actions.push_back({Action::Kind::Discard, Ticket{}, true});
    }
    if (!windowStart_) windowStart_ = t;
    if (active) lastActive_ = t;

    const auto since = lastActive_ ? std::optional<std::int64_t>(t - *lastActive_) : std::nullopt;
    const auto current = since && *since < kHoldMs ? Mode::Active : since && *since < kQuietMs ? Mode::Idle : Mode::Quiet;
    const auto previous = mode_;
    mode_ = current;
    previousTick_ = t;

    if (leaving && previous != Mode::Quiet && !networkOff_ && !blocked && t >= rateLimitedUntil_) {
        if (inflight_ && !inflight_->sending) {
            fail(inflight_->ticket, t, actions);
            inflight_.reset();
        }
        if (waiting_) {
            actions.push_back({Action::Kind::Lost, *waiting_, false});
            waiting_.reset();
        }
        const auto ticket = buildTicket(t, Activity::Leaving, true, actions);
        if (!inflight_) send(Slot{ticket, t}, actions);
        else waiting_ = ticket;
    }

    if (paused_ || networkOff_ || blocked || stopped_ || t < backoffUntil_ || t < rateLimitedUntil_) {
        windowStart_ = t;
        actions.push_back({Action::Kind::Discard, Ticket{}, false});
        return actions;
    }

    if (current == Mode::Quiet) {
        if (previous == Mode::Active || previous == Mode::Idle) {
            const auto ticket = buildTicket(t, Activity::Leaving, false, actions);
            if (waiting_) actions.push_back({Action::Kind::Lost, *waiting_, false});
            waiting_ = ticket;
        } else {
            windowStart_ = t;
            actions.push_back({Action::Kind::Discard, Ticket{}, false});
        }
    } else if (t - *windowStart_ >= (current == Mode::Active ? intervalMs() : heartbeatMs())) {
        const auto ticket = buildTicket(t, current == Mode::Active ? Activity::Active : Activity::Idle, false, actions);
        if (inflight_ && !inflight_->sending && inflight_->nextAt > t) {
            fail(inflight_->ticket, t, actions);
            inflight_.reset();
        }
        if (waiting_) actions.push_back({Action::Kind::Lost, *waiting_, false});
        waiting_ = ticket;
    }

    deliver(t, actions);
    return actions;
}

Actions Scheduler::finish(const Ticket& ticket, const Outcome& outcome, std::int64_t t) {
    Actions actions;
    if (!inflight_ || !(inflight_->ticket == ticket) || !inflight_->sending) return actions;
    auto slot = *inflight_;
    slot.sending = false;
    ++slot.attempts;
    inflight_.reset();
    switch (outcome.kind) {
    case Outcome::Kind::Delivered:
        actions.push_back({Action::Kind::Delivered, ticket, false});
        failures_ = 0;
        if (outcome.intervalSeconds) adoptedIntervalMs_ = std::clamp(*outcome.intervalSeconds, 5, 300) * std::int64_t{1000};
        break;
    case Outcome::Kind::RateLimited:
        actions.push_back({Action::Kind::Lost, ticket, false});
        rateLimitedUntil_ = std::max(rateLimitedUntil_, t + outcome.retryAfterSeconds * std::int64_t{1000});
        break;
    case Outcome::Kind::Again:
        if (slot.againUsed) {
            fail(ticket, t, actions);
        } else {
            slot.againUsed = true;
            --slot.attempts;
            send(slot, actions);
            return actions;
        }
        break;
    case Outcome::Kind::Transient:
        if (!ticket.single && slot.attempts < 2 && t + kRetryMs - ticket.builtAt <= maxAgeMs_) {
            slot.nextAt = t + kRetryMs;
            inflight_ = slot;
        } else {
            fail(ticket, t, actions);
        }
        break;
    case Outcome::Kind::Rejected: fail(ticket, t, actions); break;
    }
    if (!inflight_ && waiting_ && !networkOff_ && !blocked_ && t >= backoffUntil_ && t >= rateLimitedUntil_) deliver(t, actions);
    return actions;
}

Ticket Scheduler::buildTicket(std::int64_t t, Activity activity, bool single, Actions& actions) {
    ++seq_;
    const Ticket ticket{seq_, activity, t, t - windowStart_.value_or(t), single};
    windowStart_ = t;
    actions.push_back({Action::Kind::Build, ticket, false});
    return ticket;
}

void Scheduler::deliver(std::int64_t t, Actions& actions) {
    if (!inflight_ && waiting_) {
        inflight_ = Slot{*waiting_, t};
        waiting_.reset();
    }
    if (!inflight_ || inflight_->sending || inflight_->nextAt > t) return;
    if (t - inflight_->ticket.builtAt > maxAgeMs_) {
        fail(inflight_->ticket, t, actions);
        inflight_.reset();
    } else {
        send(*inflight_, actions);
    }
}

void Scheduler::send(Slot slot, Actions& actions) {
    slot.sending = true;
    inflight_ = slot;
    actions.push_back({Action::Kind::Send, slot.ticket, false});
}

void Scheduler::fail(const Ticket& ticket, std::int64_t t, Actions& actions) {
    actions.push_back({Action::Kind::Lost, ticket, false});
    if (++failures_ < 2) return;
    const auto base = std::min(kMaxBackoffMs, std::int64_t{5000} << std::min(failures_ - 2, 20));
    backoffUntil_ = std::max(backoffUntil_, t + static_cast<std::int64_t>(std::llround(static_cast<double>(base) * jitter_())));
}

void Scheduler::dropOutbox(Actions& actions) {
    if (inflight_) actions.push_back({Action::Kind::Lost, inflight_->ticket, false});
    if (waiting_) actions.push_back({Action::Kind::Lost, *waiting_, false});
    inflight_.reset();
    waiting_.reset();
}

}  // namespace agentspeed::report
