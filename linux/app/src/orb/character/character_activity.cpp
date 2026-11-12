#include "orb/character/character_activity.hpp"

#include <QVariantMap>
#include <algorithm>
#include <cmath>
#include <set>

namespace orb {

namespace {

std::optional<qint64> optionalMs(const QVariantMap& row, const QString& key) {
    const auto v = row.value(key);
    if (!v.isValid() || v.isNull()) return std::nullopt;
    return v.toLongLong();
}

}  // namespace

std::vector<AgentFacts> AgentFacts::fromAgents(const QVariantList& agents) {
    std::vector<AgentFacts> out;
    out.reserve(static_cast<std::size_t>(agents.size()));
    for (const auto& item : agents) {
        const auto row = item.toMap();
        AgentFacts a;
        a.id = row.value(QStringLiteral("id")).toString().toStdString();
        const auto phase = row.value(QStringLiteral("phase")).toString();
        a.phase = phase == QLatin1String("generating") ? agentspeed::Phase::Generating
                  : phase == QLatin1String("tools")    ? agentspeed::Phase::Tools
                                                        : agentspeed::Phase::Idle;
        a.inFlightSinceMs = optionalMs(row, QStringLiteral("inFlightSinceMs"));
        a.outputSinceInFlight = row.value(QStringLiteral("outputSinceInFlight")).toBool();
        a.lastResponseEndMs = optionalMs(row, QStringLiteral("lastResponseEndMs"));
        a.repeatFailureMs = optionalMs(row, QStringLiteral("repeatFailureMs"));
        a.looping = row.value(QStringLiteral("looping")).toBool();
        out.push_back(std::move(a));
    }
    return out;
}

bool CharacterActivity::update(const std::vector<AgentFacts>& agents) {
    using agentspeed::Phase;
    const auto any = [&](Phase p) { return std::any_of(agents.begin(), agents.end(), [p](const AgentFacts& a) { return a.phase == p; }); };
    phase_ = any(Phase::Generating) ? Phase::Generating : any(Phase::Tools) ? Phase::Tools : Phase::Idle;
    waitingSinceMs_.reset();
    if (phase_ == Phase::Generating) {
        bool output = false;
        for (const auto& a : agents) {
            if (a.phase != Phase::Generating) continue;
            output = output || a.outputSinceInFlight;
            if (a.inFlightSinceMs && (!waitingSinceMs_ || *a.inFlightSinceMs < *waitingSinceMs_)) waitingSinceMs_ = a.inFlightSinceMs;
        }
        if (output) waitingSinceMs_.reset();
    }
    for (const auto& a : agents) {
        if (a.lastResponseEndMs) noteResponseEnd(*a.lastResponseEndMs);
    }

    bool fired = false;
    std::map<std::string, bool> looping;
    for (const auto& a : agents) {
        const auto was = looping_.find(a.id);
        if (primed_ && a.looping && (was == looping_.end() || !was->second)) fired = true;
        looping[a.id] = a.looping;
        // The failure already met stays remembered, so an agent that leaves the list and returns never repeats it.
        if (a.repeatFailureMs) {
            const auto known = failures_.find(a.id);
            if (known == failures_.end() || known->second != *a.repeatFailureMs) {
                fired = fired || primed_;
                failures_[a.id] = *a.repeatFailureMs;
            }
        }
    }
    looping_ = std::move(looping);
    if (failures_.size() > kRemembered) {
        std::set<std::string> present;
        for (const auto& a : agents) present.insert(a.id);
        for (auto it = failures_.begin(); it != failures_.end();) it = present.count(it->first) ? std::next(it) : failures_.erase(it);
    }
    primed_ = true;
    if (fired) ++troubles_;
    return fired;
}

void CharacterClickRule::down(double time, double x, double y, int clickCount, bool armed) {
    if (clickCount >= 2) {
        cancel();
        return;
    }
    if (armed) {
        press_ = Press{time, x, y};
    } else {
        press_.reset();
    }
}

std::optional<double> CharacterClickRule::up(double time, double x, double y, bool moved) {
    if (!press_) return std::nullopt;
    const Press press = *press_;
    press_.reset();
    const double held = time - press.time;
    if (moved || std::hypot(x - press.x, y - press.y) > kMaxMovement || held > kMaxPress) return std::nullopt;
    const double delay = std::max(0.0, doubleClickInterval - held);
    pending_ = Pending{time + delay, press.x, press.y};
    return delay;
}

std::optional<std::pair<double, double>> CharacterClickRule::take(double now) {
    if (!pending_ || !(now >= pending_->fireAt - 1e-6)) return std::nullopt;
    const auto p = *pending_;
    pending_.reset();
    return std::pair{p.x, p.y};
}

}  // namespace orb
