#include <QCoreApplication>
#include <QDateTime>
#include <QUuid>

#include "agentspeed/price_book.hpp"
#include "preferences.hpp"
#include "report/report_controller.hpp"

// The live side (METRICS 11.5): every engine tick feeds the window and the scheduler; built reports are signed and sent
// one at a time; each outcome goes back to the scheduler and triggers the reaction of REPORTING 8.3.
namespace {

constexpr qint64 kHourMs = 3600LL * 1000;
constexpr int kQuitWaitMs = 1000;

std::string sv(const QString& text) { return text.toStdString(); }

}  // namespace

qint64 ReportController::correctedMs() const { return QDateTime::currentMSecsSinceEpoch() + clockCorrectionSeconds_ * 1000; }

void ReportController::onTick(const ReportTick& tick) {
    if (!tick.sample) return;
    lastSample_ = tick.sample;
    if (previewWindow_) previewWindow_->observe(*tick.sample);
    tickRun(tick.sample, tick.monotonicMs);
}

// A user event (pause, resume, network, stop) is handled at once with the latest values.
void ReportController::post(agentspeed::report::Event event) {
    pendingEvents_.push_back(event);
    if (lastSample_) tickRun(lastSample_, steadyMs());
}

// Readiness the scheduler does not track itself (METRICS 11.5, Ready).
bool ReportController::ready() const {
    return enabled() && problem_ == agentspeed::report::Problem::None && key_ && identity_ && identity_->registered && identity_->pin && discovery_ &&
           std::find(discovery_->versions.begin(), discovery_->versions.end(), 1) != discovery_->versions.end() && !needsReview() && !discoveryStale() &&
           !consent_;
}

void ReportController::startRunIfNeeded(const std::shared_ptr<const agentspeed::report::Sample>& sample) {
    if (run_ || !enabled() || !key_ || !identity_ || !identity_->registered || !discovery_) return;
    Run run;
    run.id = QUuid::createUuid().toString(QUuid::WithoutBraces).toLower();
    run.scheduler = std::make_unique<agentspeed::report::Scheduler>(discovery_->limits, paused(), !networkEnabled());
    run.window = std::make_unique<agentspeed::report::Window>(*sample);
    run_ = std::move(run);
    renamedForMismatch_ = false;
}

void ReportController::endRun() {
    if (!run_) return;
    run_.reset();
    notify();
}

void ReportController::tickRun(const std::shared_ptr<const agentspeed::report::Sample>& sample, qint64 now) {
    startRunIfNeeded(sample);
    if (!run_) {
        pendingEvents_.clear();
        return;
    }
    const auto events = std::exchange(pendingEvents_, {});
    const auto modeBefore = run_->scheduler->mode();
    const bool active = sample->working() || sample->live.output > 0 || run_->window->hasTokenIncrement(*sample);
    run_->title.update(sample->value(prefs_.metric() == QLatin1String("total")), prefs_.redline(), static_cast<double>(now) / 1000.0);
    const auto actions = run_->scheduler->tick(now, active, !ready(), events);
    apply(actions, *sample);
    if (run_ && run_->scheduler->mode() != modeBefore) notify();
    if (std::find(events.begin(), events.end(), agentspeed::report::Event::Stop) != events.end() && run_ && !run_->scheduler->busy()) endRun();
}

void ReportController::apply(const agentspeed::report::Actions& actions, const agentspeed::report::Sample& sample) {
    using Kind = agentspeed::report::Action::Kind;
    bool windowStarted = false;
    for (const auto& action : actions) {
        if (!run_) return;
        switch (action.kind) {
        case Kind::Discard:
            run_->window->discard(sample, action.keepingTick);
            windowStarted = true;
            break;
        case Kind::Build:
            build(action.ticket, sample);
            windowStarted = true;
            break;
        case Kind::Send: send(action.ticket); break;
        case Kind::Delivered:
        case Kind::Lost:
            run_->bodies.erase(action.ticket.seq);
            run_->signWithPending.erase(action.ticket.seq);
            break;
        }
    }
    if (run_) run_->window->observe(sample, !windowStarted);
}

agentspeed::report::Options ReportController::options(const agentspeed::report::Discovery& discovery,
                                                      const std::set<agentspeed::report::Section>& on) const {
    agentspeed::report::Options options;
    options.sections = acceptedSections(discovery, on);
    options.maxModels = discovery.limits.maxModels;
    options.bodyLimit = std::min(16'384, discovery.limits.maxBodyBytes);
    options.totalMetric = prefs_.metric() == QLatin1String("total");
    options.redline = prefs_.redline();
    // METRICS 11.2: the table bound to today's local date, else the current one.
    if (prices_) {
        const auto today = QDate::currentDate().toString(Qt::ISODate).toStdString();
        options.priceVersion = prices_->boundVersion(today).value_or(prices_->currentVersion());
    }
    options.extensions.modelHarnessRequests = discovery.has("modelHarnessRequests");
    return options;
}

void ReportController::build(const agentspeed::report::Ticket& ticket, const agentspeed::report::Sample& sample) {
    if (!run_ || !identity_ || !discovery_) return;
    const auto content = run_->window->take(sample);
    agentspeed::report::Envelope envelope;
    envelope.client = client();
    envelope.deviceId = sv(identity_->deviceId);
    envelope.username = sv(identity_->username);
    envelope.runId = sv(run_->id);
    envelope.seq = ticket.seq;
    envelope.sentAtMs = correctedMs();
    envelope.windowMs = ticket.windowMs;
    envelope.activity = ticket.activity;
    envelope.titleLevel = run_->title.level();
    run_->bodies[ticket.seq] = agentspeed::report::fitted(envelope, content, sample, options(*discovery_, switches())).data;
}

void ReportController::send(const agentspeed::report::Ticket& ticket) {
    const auto runId = run_ ? run_->id : QString();
    const auto it = run_ ? run_->bodies.find(ticket.seq) : decltype(run_->bodies.end()){};
    if (!run_ || it == run_->bodies.end()) {
        QTimer::singleShot(0, this, [this, ticket, runId] { complete(ticket, runId, ReportResponse{}, false); });
        return;
    }
    const bool usedPending = run_->signWithPending.contains(ticket.seq);
    signedCall("POST", "/v1/reports", it->second, usedPending, false, [this, ticket, runId, usedPending](const ReportResponse& response) {
        complete(ticket, runId, response, usedPending);
    });
}

void ReportController::complete(const agentspeed::report::Ticket& ticket, const QString& runId, const ReportResponse& response, bool usedPending) {
    agentspeed::report::Outcome outcome;
    agentspeed::report::Reaction reaction;
    if (!response.reply && response.failure == ReportFailure::Key) {
        outcome.kind = agentspeed::report::Outcome::Kind::Rejected;
        reaction.kind = agentspeed::report::Reaction::Kind::NeedsUser;
        reaction.problem = agentspeed::report::Problem::KeyMismatch;
    } else {
        const auto* reply = response.reply ? &*response.reply : nullptr;
        std::tie(outcome, reaction) = agentspeed::report::classify(reply, pendingKey_.has_value() && !usedPending);
        switch (outcome.kind) {
        case agentspeed::report::Outcome::Kind::Delivered:
            lastDeliveryMs_ = QDateTime::currentMSecsSinceEpoch();
            if (reply && reply->rank) rank_ = reply->rank;
            retryReason_.reset();
            rateLimitedUntilMs_ = 0;
            strikes_ = 0;
            if (usedPending) promotePendingKey();
            rotateKeyIfDue();
            break;
        case agentspeed::report::Outcome::Kind::Transient: retryReason_ = reply ? ReportFailure::Unavailable : response.failure; break;
        case agentspeed::report::Outcome::Kind::RateLimited:
            rateLimitedUntilMs_ = QDateTime::currentMSecsSinceEpoch() + outcome.retryAfterSeconds * 1000LL;
            retryReason_.reset();
            after(outcome.retryAfterSeconds * 1000 + 1000, [this] { notify(); });
            break;
        default: break;
        }
    }
    if (!run_ || run_->id != runId) {
        if (quitDone_) std::exchange(quitDone_, {})();
        return;
    }
    if (reaction.kind == agentspeed::report::Reaction::Kind::UsePendingKey) run_->signWithPending.insert(ticket.seq);
    const auto actions = run_->scheduler->finish(ticket, outcome, steadyMs());
    if (lastSample_) apply(actions, *lastSample_);
    react(reaction);
    if (quitDone_ && (!run_ || !run_->scheduler->busy())) std::exchange(quitDone_, {})();
    if (run_ && run_->scheduler->stopped() && !run_->scheduler->busy()) endRun();
    notify();
}

void ReportController::react(const agentspeed::report::Reaction& reaction) {
    using Kind = agentspeed::report::Reaction::Kind;
    switch (reaction.kind) {
    case Kind::CorrectClock:
        if (reaction.serverTime) {
            const auto correction = *reaction.serverTime - QDateTime::currentSecsSinceEpoch();
            if (std::abs(correction) <= 86'400) clockCorrectionSeconds_ = correction;
        }
        break;
    case Kind::ReRegister: {
        const auto now = QDateTime::currentMSecsSinceEpoch();
        if (lastReRegisterMs_ > 0 && now - lastReRegisterMs_ < kHourMs) return;
        lastReRegisterMs_ = now;
        reRegister();
        break;
    }
    case Kind::RecheckRegistration: reRegister(); break;
    case Kind::Rename:
        if (!renamedForMismatch_ && identity_) {
            renamedForMismatch_ = true;
            rename(settings_.username().isEmpty() ? identity_->username : settings_.username());
        }
        break;
    case Kind::NewRun: endRun(); break;
    case Kind::RefreshDiscovery:
        if (reaction.upgrade) problem_ = agentspeed::report::Problem::Upgrade;
        fetchDiscovery(false);
        break;
    case Kind::Strike:
        if (++strikes_ >= 3) problem_ = agentspeed::report::Problem::Rejected;
        break;
    case Kind::NeedsUser:
        problem_ = reaction.problem;
        if (reaction.problem == agentspeed::report::Problem::Gone) {
            settings_.setEnabled(false);
            endRun();
        }
        break;
    case Kind::UsePendingKey:
    case Kind::None: break;
    }
}

void ReportController::quit(std::function<void()> done) {
    if (!run_ || !enabled() || !lastSample_) {
        done();
        return;
    }
    quitDone_ = std::move(done);
    post(agentspeed::report::Event::Stop);
    if (!run_ || !run_->scheduler->busy()) {
        if (quitDone_) std::exchange(quitDone_, {})();
        return;
    }
    after(kQuitWaitMs, [this] {
        if (quitDone_) std::exchange(quitDone_, {})();
    });
}

void ReportController::quitApp() {
    quit([] { QCoreApplication::quit(); });
}
