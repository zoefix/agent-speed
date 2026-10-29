#include "orbs/orb_views.hpp"

#include <QVariantList>
#include <algorithm>

#include "agentspeed/money.hpp"
#include "agentspeed/traffic.hpp"
#include "format.hpp"

namespace {

using agentspeed::SourceKind;
using agentspeed::money::Decimal;
using agentspeed::money::Form;

bool contains(const std::vector<SourceKind>& set, SourceKind kind) { return std::find(set.begin(), set.end(), kind) != set.end(); }

QString keyOf(SourceKind kind) { return QString::fromUtf8(agentspeed::sourceId(kind)); }

}  // namespace

// DESIGN 9 Orb: the live cost rate of a set's billable sources (subscriptions excluded unless included), its known
// share, today's cost, and whether nothing is priced or every source is a subscription.
static void addCost(QVariantMap& map, const agentspeed::Snapshot& snap, const std::optional<std::vector<SourceKind>>& set, const OrbContext& c) {
    const auto& currency = agentspeed::money::CurrencyTable::bundled().currencyOrUsd(c.currency.toStdString());
    const auto rate = Decimal::parse(c.rate.toStdString()).value_or(Decimal(1));
    const auto language = c.language.toStdString();
    double liveCost = 0, weight = 0, knownWeight = 0, liveOutput = 0;
    std::int64_t todayCost = 0, unpriced = 0;
    bool anyBillable = false, anyEnabled = false;
    for (const auto& row : snap.sources) {
        if (!row.enabled || (set && !contains(*set, row.source))) continue;
        anyEnabled = true;
        if (!c.includeSubscriptions && c.subscriptions.contains(keyOf(row.source))) continue;
        anyBillable = true;
        liveCost += row.liveCost;
        liveOutput += row.liveOutput;
        const double w = std::max(row.liveOutput, 1e-9);
        weight += w;
        knownWeight += w * row.liveCostKnownShare;
        todayCost += row.todayCostMicroUsd;
        unpriced += row.unpricedRequests;
    }
    const double knownShare = weight > 0 ? knownWeight / weight : 1;
    const auto live = agentspeed::money::format(agentspeed::money::ratePerSecond(liveCost, rate), currency, language, Form::Rate);
    const auto today = agentspeed::money::format(agentspeed::money::amount(todayCost, rate), currency, language, Form::Amount);
    map.insert(QStringLiteral("costNumber"), QString::fromStdString(live.number));
    map.insert(QStringLiteral("costSymbol"), QString::fromStdString(live.symbol));
    map.insert(QStringLiteral("costPartial"), liveCost > 0 && knownShare < 0.995);
    map.insert(QStringLiteral("costUnknown"), liveOutput > 0 && knownShare <= 0);
    map.insert(QStringLiteral("costSubscriptionOnly"), anyEnabled && !anyBillable);
    map.insert(QStringLiteral("costTodayText"), todayCost == 0 && unpriced > 0 ? QStringLiteral("–")
                                                                                : QString::fromStdString((unpriced > 0 ? "≥" : "") + today.text));
}

QVariantList OrbViews::compute(const agentspeed::Snapshot& snap, const std::vector<OrbSpec>& orbs, const OrbContext& c,
                               const QVariantMap& all, std::size_t setOffset) {
    const auto sets = all.value(QStringLiteral("scoreSets")).toList();
    QVariantList out;
    std::map<QString, State> kept;
    for (std::size_t i = 0; i < orbs.size(); ++i) {
        const auto& orb = orbs[i];
        QVariantMap map;
        map.insert(QStringLiteral("id"), orb.id);
        const QVariant scoreSet = setOffset + i < static_cast<std::size_t>(sets.size()) ? sets[static_cast<qsizetype>(setOffset + i)] : QVariant();
        map.insert(QStringLiteral("scoreSets"), scoreSet.isValid() ? QVariantList{scoreSet} : QVariantList{});
        State state = states_.contains(orb.id) ? states_[orb.id] : State{};
        const bool created = !states_.contains(orb.id);
        const bool edited = !created && state.sources != orb.sources;
        state.sources = orb.sources;

        if (!orb.sources) {
            // An all-sources orb gets exactly the all-sources values.
            for (const char* key : {"liveOutput", "liveText", "agents", "completions", "todayOutputText", "traffic", "newPeak"}) {
                map.insert(QString::fromLatin1(key), all.value(QString::fromLatin1(key)));
            }
            if (c.totalMetric) {
                map.insert(QStringLiteral("liveOutput"), snap.liveTotal);
                map.insert(QStringLiteral("liveText"), format::speed(snap.liveTotal));
            }
            map.insert(QStringLiteral("unavailable"), QString());
            addCost(map, snap, std::nullopt, c);
            out.push_back(map);
            kept[orb.id] = state;
            continue;
        }

        // Live value, today's output and availability over the enabled sources of the set.
        const auto& set = *orb.sources;
        double live = 0, liveOutput = 0;
        std::int64_t today = 0;
        bool anyAvailable = false, anyEnabled = false;
        QVariantList sourceStates;
        for (const auto& row : snap.sources) {
            if (!contains(set, row.source)) continue;
            const bool detected = row.available || row.todayOutput > 0;
            QVariantMap s;
            s.insert(QStringLiteral("source"), keyOf(row.source));
            s.insert(QStringLiteral("status"), !row.enabled ? QStringLiteral("disabled") : !detected ? QStringLiteral("notFound") : QString());
            sourceStates.push_back(s);
            anyEnabled = anyEnabled || row.enabled;
            if (!row.enabled) continue;
            anyAvailable = anyAvailable || detected;
            live += c.totalMetric ? row.liveTotal : row.liveOutput;
            liveOutput += row.liveOutput;
            today += row.todayOutput;
        }
        const bool unavailable = !anyAvailable;
        map.insert(QStringLiteral("unavailable"), !unavailable ? QString() : !anyEnabled ? QStringLiteral("disabled") : QStringLiteral("notFound"));
        map.insert(QStringLiteral("sourceStates"), sourceStates);
        map.insert(QStringLiteral("liveOutput"), live);
        map.insert(QStringLiteral("liveText"), unavailable ? QStringLiteral("–") : format::speed(live));
        map.insert(QStringLiteral("todayOutputText"), format::tokens(today));

        QStringList keys;
        for (const auto kind : set) keys.push_back(keyOf(kind));
        QVariantList agents;
        for (const auto& item : all.value(QStringLiteral("agents")).toList()) {
            if (keys.contains(item.toMap().value(QStringLiteral("source")).toString())) agents.push_back(item);
        }
        map.insert(QStringLiteral("agents"), agents);
        QVariantList events;
        for (const auto& item : all.value(QStringLiteral("completions")).toList()) {
            if (keys.contains(item.toMap().value(QStringLiteral("source")).toString())) events.push_back(item);
        }
        map.insert(QStringLiteral("completions"), events);

        // peakToday_A and newPeak_A (METRICS 6.8): rebuilt from the engine's arrays at a create, a set change or a new
        // day, and no event for 60 s after a create or a set change.
        const double enginePeak = i < snap.peakSets.size() ? snap.peakSets[i] : liveOutput;
        if (created || edited) state.quietUntilMs = c.nowMs + 60'000;
        if (created || edited || state.day != c.todayStartMs || state.peak < 0) {
            state.peak = enginePeak;
            state.day = c.todayStartMs;
        }
        bool newPeak = false;
        if (liveOutput >= 20 && liveOutput >= state.peak + 1 && c.nowMs - state.lastEventMs >= 60'000 && c.nowMs >= state.quietUntilMs) {
            newPeak = true;
            state.lastEventMs = c.nowMs;
        }
        state.peak = std::max({state.peak, enginePeak, liveOutput});
        map.insert(QStringLiteral("newPeak"), newPeak);

        // The traffic of the orb's own sources (DESIGN 10, sources mode); this computer's stays as it is.
        QVariantMap traffic = all.value(QStringLiteral("traffic")).toMap();
        double rx = 0, tx = 0;
        bool known = false;
        for (const auto kind : set) {
            const auto it = snap.traffic.sources.find(kind);
            if (it == snap.traffic.sources.end()) continue;
            const auto& r = c.trafficIncludeTools ? it->second.withTools : it->second.selfOnly;
            if (!r) continue;
            rx += r->rx;
            tx += r->tx;
            known = true;
        }
        traffic.insert(QStringLiteral("agentsKnown"), known && snap.traffic.available);
        traffic.insert(QStringLiteral("agentsRx"), rx);
        traffic.insert(QStringLiteral("agentsTx"), tx);
        traffic.insert(QStringLiteral("agentsRxText"), QString::fromStdString(agentspeed::traffic::formatRate(rx, c.trafficBits)));
        traffic.insert(QStringLiteral("agentsTxText"), QString::fromStdString(agentspeed::traffic::formatRate(tx, c.trafficBits)));
        map.insert(QStringLiteral("traffic"), traffic);
        addCost(map, snap, orb.sources, c);
        out.push_back(map);
        kept[orb.id] = state;
    }
    states_ = std::move(kept);
    return out;
}
