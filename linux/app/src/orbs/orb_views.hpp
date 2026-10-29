#pragma once

#include <QString>
#include <QStringList>
#include <QVariantMap>
#include <map>
#include <optional>
#include <vector>

#include "agentspeed/snapshot.hpp"

// One floating orb as the worker sees it (DESIGN 2b): its id and sources (nullopt: every enabled source).
struct OrbSpec {
    QString id;
    std::optional<std::vector<agentspeed::SourceKind>> sources;
    bool operator==(const OrbSpec&) const = default;
};

// What an orb's values depend on besides the snapshot.
struct OrbContext {
    QString currency = QStringLiteral("USD"), rate = QStringLiteral("1"), language = QStringLiteral("en");
    QStringList subscriptions;
    bool includeSubscriptions = false;
    bool totalMetric = false;
    bool trafficIncludeTools = true;
    bool trafficBits = false;
    qint64 nowMs = 0;
    qint64 todayStartMs = 0;
};

// METRICS 6.8 Orb subsets: per orb, on the engine tick, the live value of its sources, its agents, events, today's
// output, its own peakToday_A and newPeak_A, its traffic and whether its sources are unavailable. Each map holds the keys
// OrbWindow::applySnapshot reads, so a subset orb behaves exactly like the single all-sources orb.
class OrbViews {
public:
    // `all`: the all-sources values of this tick (its newPeak, scoreSets, traffic); `setOffset`: the index of the first
    // orb's score set in the snapshot.
    QVariantList compute(const agentspeed::Snapshot& snap, const std::vector<OrbSpec>& orbs, const OrbContext& context,
                         const QVariantMap& all, std::size_t setOffset);

private:
    struct State {
        std::optional<std::vector<agentspeed::SourceKind>> sources;
        double peak = -1;
        qint64 day = 0;
        qint64 lastEventMs = 0;
        qint64 quietUntilMs = 0;
    };
    std::map<QString, State> states_;
};
