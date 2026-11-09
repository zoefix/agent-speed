#include "orbs/orb_views.hpp"

#include <QVariantList>
#include <algorithm>

#include "agentspeed/character/store.hpp"
#include "agentspeed/host_subset.hpp"
#include "agentspeed/money.hpp"
#include "agentspeed/traffic.hpp"
#include "format.hpp"

namespace {

using agentspeed::SourceKind;
using agentspeed::money::Decimal;
using agentspeed::money::Form;

bool contains(const std::vector<SourceKind>& set, SourceKind kind) { return std::find(set.begin(), set.end(), kind) != set.end(); }

QString keyOf(SourceKind kind) { return QString::fromUtf8(agentspeed::sourceId(kind)); }

// CHARACTERS 7: the live output per model vendor of the orb's generating agents and its fastest model.
void addVendors(QVariantMap& map, const agentspeed::Snapshot& snap, const std::optional<std::vector<SourceKind>>& sources,
                const std::optional<std::vector<std::string>>& hosts, const OrbContext& c) {
    std::vector<agentspeed::AgentRow> agents;
    for (const auto& a : snap.agents) {
        if (sources && !contains(*sources, a.source)) continue;
        if (hosts && std::find(hosts->begin(), hosts->end(), a.host) == hosts->end()) continue;
        agents.push_back(a);
    }
    const auto reading = agentspeed::character::VendorReading::of(agents, c.prices.get());
    QVariantMap rates;
    for (const auto& [vendor, rate] : reading.rates) rates.insert(QString::fromStdString(vendor), rate);
    QStringList order;
    for (const auto& v : reading.vendorsByRate()) order.push_back(QString::fromStdString(v));
    map.insert(QStringLiteral("vendorRates"), rates);
    map.insert(QStringLiteral("vendorOrder"), order);
    map.insert(QStringLiteral("fastestModel"), reading.fastestModel ? QString::fromStdString(*reading.fastestModel) : QString());
    map.insert(QStringLiteral("fastestVendor"), reading.fastestVendor ? QString::fromStdString(*reading.fastestVendor) : QString());
}

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
    map.insert(QStringLiteral("costInline"), QString::fromStdString(agentspeed::money::withSymbol(live.symbol, live.number)));
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
        const bool edited = !created && (state.sources != orb.sources || state.hosts != orb.hosts);
        state.sources = orb.sources;
        state.hosts = orb.hosts;

        if (!orb.sources && !orb.hosts) {
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
            addVendors(map, snap, std::nullopt, std::nullopt, c);
            out.push_back(map);
            kept[orb.id] = state;
            continue;
        }

        // DESIGN 13: an orb of some hosts reads their (host, source) cells; with every selected host offline its
        // sources are unavailable for that reason.
        const agentspeed::Snapshot restrictedSnap = orb.hosts ? agentspeed::hosts::restricted(snap, *orb.hosts) : agentspeed::Snapshot{};
        const auto& view = orb.hosts ? restrictedSnap : snap;
        const bool offline = orb.hosts && agentspeed::hosts::isOffline(snap, *orb.hosts);
        const auto inSet = [&](SourceKind kind) { return !orb.sources || contains(*orb.sources, kind); };
        const auto onHost = [&](const QString& host) {
            return !orb.hosts || std::find(orb.hosts->begin(), orb.hosts->end(), host.toStdString()) != orb.hosts->end();
        };

        // Live value, today's output and availability over the enabled sources of the set.
        double live = 0, liveOutput = 0;
        std::int64_t today = 0;
        bool anyAvailable = false, anyEnabled = false;
        QVariantList sourceStates;
        QStringList keys;
        for (const auto& row : view.sources) {
            if (!inSet(row.source)) continue;
            keys.push_back(keyOf(row.source));
            const bool detected = row.available || row.todayOutput > 0;
            QVariantMap s;
            s.insert(QStringLiteral("source"), keyOf(row.source));
            s.insert(QStringLiteral("status"), !row.enabled ? QStringLiteral("disabled")
                                               : offline    ? QStringLiteral("hostsOffline")
                                               : !detected  ? QStringLiteral("notFound")
                                                            : QString());
            sourceStates.push_back(s);
            anyEnabled = anyEnabled || row.enabled;
            if (!row.enabled) continue;
            anyAvailable = anyAvailable || detected;
            live += c.totalMetric ? row.liveTotal : row.liveOutput;
            liveOutput += row.liveOutput;
            today += row.todayOutput;
        }
        const bool unavailable = offline || !anyAvailable;
        map.insert(QStringLiteral("unavailable"), !unavailable ? QString()
                                                  : offline    ? QStringLiteral("hostsOffline")
                                                  : !anyEnabled ? QStringLiteral("disabled")
                                                                : QStringLiteral("notFound"));
        map.insert(QStringLiteral("sourceStates"), sourceStates);
        map.insert(QStringLiteral("liveOutput"), live);
        map.insert(QStringLiteral("liveText"), unavailable ? QStringLiteral("–") : format::speed(live));
        map.insert(QStringLiteral("todayOutputText"), format::tokens(today));

        QVariantList agents;
        for (const auto& item : all.value(QStringLiteral("agents")).toList()) {
            const auto agent = item.toMap();
            if (keys.contains(agent.value(QStringLiteral("source")).toString()) && onHost(agent.value(QStringLiteral("host")).toString())) {
                agents.push_back(item);
            }
        }
        map.insert(QStringLiteral("agents"), agents);
        QVariantList events;
        for (const auto& item : all.value(QStringLiteral("completions")).toList()) {
            const auto event = item.toMap();
            if (keys.contains(event.value(QStringLiteral("source")).toString()) && onHost(event.value(QStringLiteral("host")).toString())) {
                events.push_back(item);
            }
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

        // The traffic of the orb's own sources (DESIGN 10, sources mode); this computer's stays as it is. A host's agents
        // have no traffic (REMOTE 7).
        QVariantMap traffic = all.value(QStringLiteral("traffic")).toMap();
        double rx = 0, tx = 0;
        bool known = false;
        for (const auto& [kind, rates] : snap.traffic.sources) {
            if (!inSet(kind) || !onHost(QString::fromUtf8(agentspeed::hosts::kLocal))) continue;
            const auto& r = c.trafficIncludeTools ? rates.withTools : rates.selfOnly;
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
        addCost(map, view, orb.sources, c);
        addVendors(map, snap, orb.sources, orb.hosts, c);
        out.push_back(map);
        kept[orb.id] = state;
    }
    states_ = std::move(kept);
    return out;
}
