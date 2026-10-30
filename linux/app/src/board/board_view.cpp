#include "board/board_view.hpp"

#include <algorithm>
#include <set>

#include "agentspeed/modes.hpp"
#include "agentspeed/money.hpp"
#include "format.hpp"
#include "heat.hpp"
#include "perf_card.hpp"
#include "strings.hpp"

namespace {

using agentspeed::AgentRow;
using agentspeed::SourceKind;

QString key(SourceKind kind) { return QString::fromUtf8(agentspeed::sourceId(kind)); }

double valueOf(const AgentRow& row, bool total) { return total ? row.liveTotal : row.liveOutput; }

// DESIGN 12: a 性能 value as the board draws it — the shown integer (−1 for –), its compact and full texts, the form,
// the provisional mark, the supplying agent and the words accessibility reads (the highest of N when N > 1).
QVariantMap intelMap(const agentspeed::perf::Highest& value, bool provisional, std::int64_t scored, const Strings* strings) {
    const auto shown = value.shown.value_or(-1);
    const QString words = strings && value.shown ? perf_card::scoreWords(*strings, shown, provisional, value.form == agentspeed::perf::Form::Resting, scored)
                                                 : QString();
    return {{QStringLiteral("shown"), static_cast<qlonglong>(shown)},
            {QStringLiteral("words"), words},
            {QStringLiteral("text"), value.shown ? format::perfCompact(shown) : QStringLiteral("–")},
            {QStringLiteral("fullText"), value.shown ? format::perfFull(shown) : QStringLiteral("–")},
            {QStringLiteral("scale"), value.shown ? format::perfScale(static_cast<double>(shown)) : 0.0},
            {QStringLiteral("form"), QString::fromUtf8(agentspeed::perf::formId(value.form))},
            {QStringLiteral("provisional"), value.shown.has_value() && provisional},
            {QStringLiteral("scored"), static_cast<qlonglong>(scored)},
            {QStringLiteral("agent"), value.agent ? QString::fromStdString(*value.agent) : QString()}};
}

// METRICS 6.9 Sets and groups: the highest live score, else the highest resting score, else none.
QVariantMap intelOf(const std::vector<const AgentRow*>& members, const Strings* strings) {
    std::vector<agentspeed::perf::Valued> values;
    for (const auto* m : members) {
        if (m->perf) values.push_back({m->id, m->lastLineMs, m->perf->score.form, m->perf->score.score});
    }
    const auto value = agentspeed::perf::highest(values);
    bool provisional = false;
    std::int64_t scored = 0;
    for (const auto* m : members) {
        if (!m->perf) continue;
        if (value.agent && m->id == *value.agent) provisional = m->perf->score.provisional;
        if (value.form != agentspeed::perf::Form::None && m->perf->score.form == value.form) ++scored;
    }
    return intelMap(value, provisional, scored, strings);
}

void capsule(const AgentRow& a, QVariantMap& row) {
    row.insert(QStringLiteral("effort"), a.effort ? QString::fromStdString(*a.effort) : QString());
    row.insert(QStringLiteral("speedMode"), a.speed ? QString::fromStdString(*a.speed) : QString());
    const auto level = a.effort ? agentspeed::effort_speed::level(*a.effort) : std::nullopt;
    row.insert(QStringLiteral("effortLevel"), level ? *level : -1);
    row.insert(QStringLiteral("effortTop"), a.effort && agentspeed::effort_speed::isTopRung(*a.effort));
    row.insert(QStringLiteral("bolts"), a.speed ? agentspeed::effort_speed::bolts(*a.speed) : 0);
    row.insert(QStringLiteral("effortConfigured"), a.effortConfigured);
    row.insert(QStringLiteral("speedConfigured"), a.speedConfigured);
}

}  // namespace

QVariantMap BoardViews::compute(const agentspeed::Snapshot& snap, const std::vector<BoardSpec>& boards, const BoardContext& c, std::size_t setOffset) {
    using agentspeed::money::Decimal;
    using agentspeed::money::Form;
    const auto& currencies = agentspeed::money::CurrencyTable::bundled();
    const auto& currency = currencies.currencyOrUsd(c.currency.toStdString());
    const auto rate = Decimal::parse(c.rate.toStdString()).value_or(Decimal(1));
    const auto language = c.language.toStdString();
    QVariantMap out;
    std::set<QString> alive;
    for (std::size_t index = 0; index < boards.size(); ++index) {
        const auto& spec = boards[index];
        alive.insert(spec.id);
        auto& state = states_[spec.id];
        const auto inSet = [&](SourceKind kind) {
            if (spec.sources) return std::find(spec.sources->begin(), spec.sources->end(), kind) != spec.sources->end();
            return true;
        };
        // Hero: the board's live value, cost and today's numbers over its enabled sources (METRICS 6.8).
        double live = 0, liveCost = 0, weight = 0, knownWeight = 0;
        std::int64_t today = 0, todayCost = 0, unpriced = 0;
        bool billableAny = false, anySource = false;
        for (const auto& s : snap.sources) {
            if (!s.enabled || !inSet(s.source)) continue;
            anySource = true;
            live += c.totalMetric ? s.liveTotal : s.liveOutput;
            today += s.todayOutput;
            const bool billable = c.includeSubscriptions || !c.subscriptions.contains(key(s.source));
            if (!billable) continue;
            billableAny = true;
            liveCost += s.liveCost;
            const double w = std::max(s.liveOutput, 1e-9);
            weight += w;
            knownWeight += w * s.liveCostKnownShare;
            todayCost += s.todayCostMicroUsd;
            unpriced += s.unpricedRequests;
        }
        state.title.update(live, c.redline, static_cast<double>(c.steadyMs) / 1000.0);
        const auto second = c.steadyMs / 1000;
        if (state.seconds.empty() || state.seconds.back().first != second) state.seconds.emplace_back(second, live);
        else state.seconds.back().second = std::max(state.seconds.back().second, live);
        while (state.seconds.size() > 60) state.seconds.pop_front();
        if (state.peakDay != c.day) {
            state.peakDay = c.day;
            state.peak = 0;
        }
        state.peak = std::max(state.peak, live);
        const double peak = spec.sources ? state.peak : std::max(snap.peakToday, live);
        // newPeak_A (METRICS 6.8): 1 tok/s above the peak before this tick, at least 20, 60 s apart, never in the
        // board's first 60 s.
        if (state.firstMs == 0) state.firstMs = c.steadyMs;
        bool newPeak = false;
        if (state.peakBefore >= 0 && live >= 20 && live >= state.peakBefore + 1 && c.steadyMs - state.lastPeakMs >= 60'000 &&
            c.steadyMs - state.firstMs >= 60'000) {
            newPeak = true;
            state.lastPeakMs = c.steadyMs;
        }
        state.peakBefore = peak;

        QVariantMap view;
        view.insert(QStringLiteral("live"), live);
        view.insert(QStringLiteral("liveText"), format::speed(live));
        view.insert(QStringLiteral("heat"), heat::of(live, c.redline));
        view.insert(QStringLiteral("titleLevel"), state.title.level());
        QVariantList history;
        for (const auto& [_, v] : state.seconds) history.append(v);
        view.insert(QStringLiteral("history"), history);
        view.insert(QStringLiteral("todayText"), format::tokens(today));
        view.insert(QStringLiteral("peak"), peak);
        view.insert(QStringLiteral("peakText"), format::speed(peak));
        view.insert(QStringLiteral("newPeak"), newPeak);
        const auto todayMoney = agentspeed::money::format(agentspeed::money::amount(todayCost, rate), currency, language, Form::Amount);
        const auto liveMoney = agentspeed::money::format(agentspeed::money::ratePerSecond(liveCost, rate), currency, language, Form::Rate);
        const double knownShare = weight > 0 ? knownWeight / weight : 1;
        const bool subscriptionOnly = anySource && !billableAny;
        view.insert(QStringLiteral("costSubscription"), subscriptionOnly);
        view.insert(QStringLiteral("costTodayText"), todayCost == 0 && unpriced > 0 ? QStringLiteral("–") : QString::fromStdString(todayMoney.text));
        view.insert(QStringLiteral("costPartial"), liveCost > 0 && knownShare < 0.995);
        view.insert(QStringLiteral("costUnknown"), live > 0 && liveCost == 0 && knownShare == 0);
        view.insert(QStringLiteral("costNumber"), QString::fromStdString(liveMoney.number));
        view.insert(QStringLiteral("costUnit"), c.strings ? c.strings->fmt(QStringLiteral("cost.unitPerSecond"), {QString::fromStdString(liveMoney.symbol)})
                                                          : QString());
        if (setOffset + index < snap.scoreSets.size()) {
            const auto& set = snap.scoreSets[setOffset + index];
            view.insert(QStringLiteral("intel"), intelMap(set.value, set.provisional, set.scored, c.strings));
            view.insert(QStringLiteral("intelScored"), static_cast<int>(set.scored));
            view.insert(QStringLiteral("thinking"), set.liveThinking);
            view.insert(QStringLiteral("thinkingText"), format::speed(set.liveThinking));
        }

        // Without its own score set (demo snapshots), the board's 性能 is the set rule over its agents.
        if (setOffset + index >= snap.scoreSets.size()) {
            std::vector<const AgentRow*> all;
            for (const auto& a : snap.agents)
                if (inSet(a.source)) all.push_back(&a);
            view.insert(QStringLiteral("intel"), intelOf(all, c.strings));
            double thinking = 0;
            for (const auto* a : all) thinking += a->liveThinking;
            view.insert(QStringLiteral("thinking"), thinking);
            view.insert(QStringLiteral("thinkingText"), format::speed(thinking));
        }
        // The hero's card: the supplying agent's, with 「N 個 agent 中最高」 when N ≥ 2.
        if (c.strings) {
            const auto intel = view.value(QStringLiteral("intel")).toMap();
            const auto supplier = intel.value(QStringLiteral("agent")).toString().toStdString();
            for (const auto& a : snap.agents) {
                if (a.id != supplier) continue;
                auto card = perf_card::card(a, *c.strings, c.strings->t(QStringLiteral("source.") + key(a.source)));
                if (const auto n = intel.value(QStringLiteral("scored")).toLongLong(); n >= 2) {
                    const auto top = c.strings->fmt(QStringLiteral("perf.card.top"), {n});
                    const auto eyebrow = card.value(QStringLiteral("eyebrow")).toString();
                    card.insert(QStringLiteral("eyebrow"), eyebrow.isEmpty() ? top : eyebrow + QStringLiteral(" · ") + top);
                }
                view.insert(QStringLiteral("heroCard"), card);
            }
        }
        // Ranking: groups of a root and its listed descendants; group speed = the sum of its members' live values.
        std::vector<const AgentRow*> agents;
        std::set<std::string> listed;
        for (const auto& a : snap.agents) {
            if (!inSet(a.source)) continue;
            agents.push_back(&a);
            listed.insert(a.id);
        }
        std::map<std::string, std::vector<const AgentRow*>> members;
        std::map<std::string, const AgentRow*> byId;
        std::vector<std::string> roots;
        for (const auto* a : agents) byId[a->id] = a;
        std::map<std::string, std::string> rootOf;
        int generating = 0, tools = 0;
        for (const auto* a : agents) {
            generating += a->phase == agentspeed::Phase::Generating;
            tools += a->phase == agentspeed::Phase::Tools;
            std::string root = a->id;
            for (auto parent = a->parentId; parent && listed.contains(*parent);) {
                root = *parent;
                parent = byId[*parent]->parentId;
            }
            if (root == a->id) roots.push_back(root);
            members[root].push_back(a);
        }
        view.insert(QStringLiteral("generating"), generating);
        view.insert(QStringLiteral("tools"), tools);
        std::vector<agentspeed::board::Group> groups;
        std::map<std::string, double> groupValue;
        for (const auto& root : roots) {
            double sum = 0;
            std::int64_t last = 0;
            for (const auto* m : members[root]) {
                sum += valueOf(*m, c.totalMetric);
                last = std::max(last, m->lastLineMs.value_or(0));
            }
            groupValue[root] = sum;
            groups.push_back({root, sum, last});
        }
        const auto [order, events] = state.ranking.tick(groups);
        QVariantList rows;
        double restSum = 0;
        int rank = 0;
        for (const auto& id : order) {
            ++rank;
            const auto* root = byId[id];
            const auto& team = members[id];
            QVariantMap row;
            row.insert(QStringLiteral("id"), QString::fromStdString(id));
            row.insert(QStringLiteral("rank"), rank);
            row.insert(QStringLiteral("source"), key(root->source));
            row.insert(QStringLiteral("phase"), QString::fromUtf8(agentspeed::phaseId(root->phase)));
            row.insert(QStringLiteral("project"), root->project ? QString::fromStdString(*root->project) : QString::fromStdString(id).section(QLatin1Char('/'), -1));
            row.insert(QStringLiteral("model"), root->model ? QString::fromStdString(*root->model) : QString());
            row.insert(QStringLiteral("subCount"), static_cast<int>(team.size()) - 1);
            row.insert(QStringLiteral("value"), groupValue[id]);
            row.insert(QStringLiteral("valueText"), format::speed(groupValue[id]));
            row.insert(QStringLiteral("share"), live > 0 ? std::min(1.0, groupValue[id] / live) : 0.0);
            const auto intel = intelOf(team, c.strings);
            row.insert(QStringLiteral("intel"), intel);
            if (c.strings) {
                // DESIGN 12 Score card: a group row's card is its supplying member's (none: the root's).
                const AgentRow* supplier = root;
                for (const auto* m : team)
                    if (m->id == intel.value(QStringLiteral("agent")).toString().toStdString()) supplier = m;
                auto card = perf_card::card(*supplier, *c.strings, c.strings->t(QStringLiteral("source.") + key(supplier->source)));
                if (team.size() > 1) {
                    card.insert(QStringLiteral("meta"), card.value(QStringLiteral("meta")).toString() + QStringLiteral(" · ") +
                                                            c.strings->fmt(QStringLiteral("boards.subCount"), {static_cast<qlonglong>(team.size() - 1)}));
                }
                auto facts = card.value(QStringLiteral("facts")).toStringList();
                facts << format::speed(groupValue[id]) + QLatin1Char(' ') + c.strings->t(QStringLiteral("unit.tokS"));
                card.insert(QStringLiteral("facts"), facts);
                row.insert(QStringLiteral("card"), card);
            }
            capsule(*root, row);
            // Sub rows: the fastest sub-agents first, then the rest summed.
            std::vector<const AgentRow*> subs(team.begin() + 1, team.end());
            std::sort(subs.begin(), subs.end(), [&](const AgentRow* a, const AgentRow* b) {
                const auto va = valueOf(*a, c.totalMetric), vb = valueOf(*b, c.totalMetric);
                if (va != vb) return va > vb;
                if (a->lastLineMs != b->lastLineMs) return a->lastLineMs.value_or(0) > b->lastLineMs.value_or(0);
                return a->id < b->id;
            });
            QVariantList subRows;
            for (const auto* s : subs) {
                QVariantMap sub;
                sub.insert(QStringLiteral("id"), QString::fromStdString(s->id));
                sub.insert(QStringLiteral("source"), key(s->source));
                sub.insert(QStringLiteral("phase"), QString::fromUtf8(agentspeed::phaseId(s->phase)));
                sub.insert(QStringLiteral("name"), s->model ? QString::fromStdString(*s->model) : s->project ? QString::fromStdString(*s->project) : QString());
                sub.insert(QStringLiteral("value"), valueOf(*s, c.totalMetric));
                sub.insert(QStringLiteral("valueText"), format::speed(valueOf(*s, c.totalMetric)));
                sub.insert(QStringLiteral("intel"), intelOf({s}, c.strings));
                if (c.strings) sub.insert(QStringLiteral("card"), perf_card::card(*s, *c.strings, c.strings->t(QStringLiteral("source.") + key(s->source))));
                capsule(*s, sub);
                subRows.append(sub);
            }
            row.insert(QStringLiteral("subs"), subRows);
            rows.append(row);
            restSum += groupValue[id];
        }
        Q_UNUSED(restSum);
        view.insert(QStringLiteral("groups"), rows);
        QVariantList eventList;
        for (const auto& e : events) {
            eventList.append(QVariantMap{{QStringLiteral("type"), e.type == agentspeed::board::RankEvent::Type::NewLeader ? QStringLiteral("newLeader") : QStringLiteral("overtake")},
                                         {QStringLiteral("id"), QString::fromStdString(e.id)},
                                         {QStringLiteral("from"), e.from},
                                         {QStringLiteral("to"), e.to}});
        }
        view.insert(QStringLiteral("events"), eventList);
        out.insert(spec.id, view);
    }
    for (auto it = states_.begin(); it != states_.end();) it = alive.contains(it->first) ? std::next(it) : states_.erase(it);
    return out;
}
