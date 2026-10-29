#include "board/board_view.hpp"

#include <algorithm>
#include <set>

#include "agentspeed/modes.hpp"
#include "agentspeed/money.hpp"
#include "format.hpp"
#include "heat.hpp"
#include "strings.hpp"
#include "task_card.hpp"

namespace {

using agentspeed::AgentRow;
using agentspeed::SourceKind;

QString key(SourceKind kind) { return QString::fromUtf8(agentspeed::sourceId(kind)); }

double valueOf(const AgentRow& row, bool total) { return total ? row.liveTotal : row.liveOutput; }

struct Intel {
    int shown = -1;
    QString form = QStringLiteral("none");
    bool proof = false;
    QString agent;
};

// METRICS 6.9 Sets and groups: the highest live score, else the highest resting score, else none.
Intel intelOf(const std::vector<const AgentRow*>& members) {
    Intel out;
    for (const char* form : {"live", "resting"}) {
        const AgentRow* best = nullptr;
        for (const auto* m : members) {
            if (!m->task || m->task->value.form != form || !m->task->value.score) continue;
            if (!best || *m->task->value.score > *best->task->value.score) best = m;
        }
        if (best) {
            out.shown = best->task->value.shown ? static_cast<int>(*best->task->value.shown) : static_cast<int>(*best->task->value.score + 0.5);
            out.form = QString::fromLatin1(form);
            const auto state = best->task->value.state.value_or("");
            out.proof = state == "unverified" || state == "pending";
            out.agent = QString::fromStdString(best->id);
            return out;
        }
    }
    return out;
}

QVariantMap intelMap(const Intel& intel) {
    return {{QStringLiteral("shown"), intel.shown}, {QStringLiteral("form"), intel.form}, {QStringLiteral("proof"), intel.proof}, {QStringLiteral("agent"), intel.agent}};
}

void capsule(const AgentRow& a, QVariantMap& row) {
    row.insert(QStringLiteral("effort"), a.effort ? QString::fromStdString(*a.effort) : QString());
    row.insert(QStringLiteral("speedMode"), a.speed ? QString::fromStdString(*a.speed) : QString());
    const auto level = a.effort ? agentspeed::effort_speed::level(*a.effort) : std::nullopt;
    row.insert(QStringLiteral("effortLevel"), level ? *level : -1);
    row.insert(QStringLiteral("effortTop"), a.effort && agentspeed::effort_speed::isTopRung(*a.effort));
    row.insert(QStringLiteral("bolts"), a.speed ? agentspeed::effort_speed::bolts(*a.speed) : 0);
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
        view.insert(QStringLiteral("costUnit"), c.strings ? task_card::fmt(*c.strings, QStringLiteral("cost.unitPerSecond"), {QString::fromStdString(liveMoney.symbol)})
                                                          : QString());
        if (setOffset + index < snap.scoreSets.size()) {
            const auto& set = snap.scoreSets[setOffset + index];
            Intel intel;
            intel.form = QString::fromStdString(set.value.form);
            intel.shown = set.value.shown ? static_cast<int>(*set.value.shown) : -1;
            const auto st = set.state.value_or("");
            intel.proof = set.value.shown && (st == "unverified" || st == "pending");
            intel.agent = set.value.agent ? QString::fromStdString(*set.value.agent) : QString();
            view.insert(QStringLiteral("intel"), intelMap(intel));
            view.insert(QStringLiteral("intelScored"), static_cast<int>(set.scored));
            view.insert(QStringLiteral("thinking"), set.liveThinking);
            view.insert(QStringLiteral("thinkingText"), format::speed(set.liveThinking));
        }

        // Without its own score set (demo snapshots), the board's 智力 is the set rule over its agents.
        if (setOffset + index >= snap.scoreSets.size()) {
            std::vector<const AgentRow*> all;
            for (const auto& a : snap.agents)
                if (inSet(a.source)) all.push_back(&a);
            view.insert(QStringLiteral("intel"), intelMap(intelOf(all)));
            double thinking = 0;
            for (const auto* a : all) thinking += a->liveThinking;
            view.insert(QStringLiteral("thinking"), thinking);
            view.insert(QStringLiteral("thinkingText"), format::speed(thinking));
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
            row.insert(QStringLiteral("intel"), intelMap(intelOf(team)));
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
                sub.insert(QStringLiteral("intel"), intelMap(intelOf({s})));
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
