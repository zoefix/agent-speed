#include "monitor_worker.hpp"

#include <QDateTime>
#include <QTimer>
#include <QVariantList>
#include <algorithm>

#include "agentspeed/demo.hpp"
#include "agentspeed/money.hpp"
#include "format.hpp"
#include "heat.hpp"
#include "strings.hpp"
#include "task_card.hpp"

namespace {

using agentspeed::money::Decimal;

qint64 localMidnightMs(qint64 nowMs) {
    const auto now = QDateTime::fromMSecsSinceEpoch(nowMs);
    return QDateTime(now.date(), QTime(0, 0)).toMSecsSinceEpoch();
}

QString sourceKey(agentspeed::SourceKind kind) { return QString::fromUtf8(agentspeed::sourceId(kind)); }

}  // namespace

MonitorWorker::MonitorWorker(const Strings* strings, bool demo, WorkerOptions options)
    : strings_(strings), demo_(demo), options_(std::move(options)) {}

MonitorWorker::~MonitorWorker() = default;

void MonitorWorker::setOptions(WorkerOptions options, bool reconfigure) {
    std::lock_guard lock(optionsMutex_);
    pending_ = std::move(options);
    restartPending_ |= reconfigure;
}

void MonitorWorker::start() {
    const qint64 now = QDateTime::currentMSecsSinceEpoch();
    startMonitor(now);
    timer_ = new QTimer(this);
    timer_->setInterval(500);
    connect(timer_, &QTimer::timeout, this, &MonitorWorker::tick);
    timer_->start();
    tick();
}

void MonitorWorker::startMonitor(qint64 now) {
    monitor_ = std::make_unique<agentspeed::Monitor>(options_.config);
    monitor_->start(now, localMidnightMs(now));
    lastPollMs_ = now;
}

void MonitorWorker::setDemo(bool demo) {
    demo_ = demo;
    peakBefore_ = -1;
    tick();
}

QVariantMap MonitorWorker::toVariant(const agentspeed::Snapshot& snap) const {
    using agentspeed::money::Form;
    const auto& currencies = agentspeed::money::CurrencyTable::bundled();
    const auto& currency = currencies.currencyOrUsd(options_.currency.toStdString());
    const auto rate = Decimal::parse(options_.rate.toStdString()).value_or(Decimal(1));
    const auto language = options_.language.toStdString();
    const auto billable = [&](agentspeed::SourceKind kind) {
        return options_.includeSubscriptions || !options_.subscriptions.contains(sourceKey(kind));
    };

    QVariantMap map;
    map.insert(QStringLiteral("nowMs"), static_cast<qint64>(snap.nowMs));
    map.insert(QStringLiteral("liveOutput"), snap.liveOutput);
    map.insert(QStringLiteral("liveText"), format::speed(snap.liveOutput));
    map.insert(QStringLiteral("compactText"), format::compactSpeed(snap.liveOutput));
    map.insert(QStringLiteral("estimatedShare"), snap.estimatedShare);
    map.insert(QStringLiteral("todayOutputText"), format::tokens(snap.todayOutput));
    map.insert(QStringLiteral("todayRequestsText"), QString::number(snap.todayRequests));
    map.insert(QStringLiteral("todayAverageText"), snap.todayAverage ? format::speed(*snap.todayAverage) : QStringLiteral("–"));
    map.insert(QStringLiteral("peakText"), format::speed(snap.peakToday));
    map.insert(QStringLiteral("peak"), snap.peakToday);
    map.insert(QStringLiteral("todayInputText"), format::tokens(snap.todayInput));
    map.insert(QStringLiteral("heat"), heat::of(snap.liveOutput));
    QVariantList history;
    history.reserve(static_cast<qsizetype>(snap.history.size()));
    for (double v : snap.history) history.push_back(v);
    map.insert(QStringLiteral("history"), history);
    map.insert(QStringLiteral("generating"), snap.generating);
    map.insert(QStringLiteral("tools"), snap.tools);
    map.insert(QStringLiteral("demo"), snap.demo);

    QVariantList agents;
    for (const auto& a : snap.agents) {
        const auto source = sourceKey(a.source);
        QVariantMap row;
        row.insert(QStringLiteral("id"), QString::fromStdString(a.id));
        row.insert(QStringLiteral("source"), source);
        row.insert(QStringLiteral("sourceName"), strings_->t(QStringLiteral("source.") + source));
        row.insert(QStringLiteral("project"), a.project ? QString::fromStdString(*a.project) : QString());
        row.insert(QStringLiteral("model"), a.model ? QString::fromStdString(*a.model) : QString());
        row.insert(QStringLiteral("phase"), QString::fromUtf8(agentspeed::phaseId(a.phase)));
        row.insert(QStringLiteral("live"), a.liveOutput);
        row.insert(QStringLiteral("liveText"), format::speed(a.liveOutput));
        row.insert(QStringLiteral("depth"), a.depth);
        row.insert(QStringLiteral("subagent"), a.subagent);
        row.insert(QStringLiteral("effort"), a.effort ? QString::fromStdString(*a.effort) : QString());
        row.insert(QStringLiteral("speed"), a.speed ? QString::fromStdString(*a.speed) : QString());
        const auto level = a.effort ? agentspeed::effort_speed::level(*a.effort) : std::nullopt;
        row.insert(QStringLiteral("effortLevel"), level ? *level : -1);
        row.insert(QStringLiteral("effortTop"), a.effort && agentspeed::effort_speed::isTopRung(*a.effort));
        row.insert(QStringLiteral("bolts"), a.speed ? agentspeed::effort_speed::bolts(*a.speed) : 0);
        row.insert(QStringLiteral("looping"), a.task && a.task->workSignals.looping.has_value());
        row.insert(QStringLiteral("liveThinking"), a.liveThinking);
        task_card::cell(a, row);
        row.insert(QStringLiteral("card"), task_card::card(a, *strings_, strings_->t(QStringLiteral("source.") + source)));
        agents.push_back(row);
    }
    map.insert(QStringLiteral("agents"), agents);

    // 智力 and 思考 of every requested source set (DESIGN 10, 12); the first is every enabled source.
    QVariantList sets;
    for (const auto& set : snap.scoreSets) {
        QVariantMap value;
        value.insert(QStringLiteral("form"), QString::fromStdString(set.value.form));
        value.insert(QStringLiteral("shown"), set.value.shown ? static_cast<int>(*set.value.shown) : -1);
        value.insert(QStringLiteral("agent"), set.value.agent ? QString::fromStdString(*set.value.agent) : QString());
        value.insert(QStringLiteral("scored"), static_cast<int>(set.scored));
        const auto state = set.state.value_or("");
        value.insert(QStringLiteral("proof"), set.value.shown && (state == "unverified" || state == "pending"));
        value.insert(QStringLiteral("liveThinking"), set.liveThinking);
        value.insert(QStringLiteral("thinkingText"), format::speed(set.liveThinking));
        value.insert(QStringLiteral("generating"), set.generating);
        QString project;
        for (const auto& a : snap.agents) {
            if (set.value.agent && a.id == *set.value.agent) project = a.project ? QString::fromStdString(*a.project) : QString();
        }
        value.insert(QStringLiteral("project"), project);
        sets.push_back(value);
    }
    map.insert(QStringLiteral("scoreSets"), sets);

    // DESIGN 9: money values sum the billable sources (9.4); a partly unpriced value carries `≥`.
    double liveCost = 0, knownWeight = 0, weight = 0;
    std::int64_t todayCost = 0, unpriced = 0;
    QVariantList sources;
    for (const auto& s : snap.sources) {
        const auto source = sourceKey(s.source);
        QVariantMap row;
        row.insert(QStringLiteral("source"), source);
        row.insert(QStringLiteral("name"), strings_->t(QStringLiteral("source.") + source));
        row.insert(QStringLiteral("enabled"), s.enabled);
        row.insert(QStringLiteral("available"), s.available);
        row.insert(QStringLiteral("path"), QString::fromStdString(s.path));
        row.insert(QStringLiteral("sessions"), s.sessions);
        row.insert(QStringLiteral("live"), s.liveOutput);
        row.insert(QStringLiteral("today"), static_cast<qint64>(s.todayOutput));
        row.insert(QStringLiteral("todayText"), format::tokens(s.todayOutput));
        const auto cost = agentspeed::money::format(agentspeed::money::amount(s.todayCostMicroUsd, rate), currency, language, Form::Amount);
        row.insert(QStringLiteral("todayCostText"), QString::fromStdString(cost.text));
        row.insert(QStringLiteral("unpriced"), static_cast<qint64>(s.unpricedRequests));
        row.insert(QStringLiteral("subscribed"), options_.subscriptions.contains(source));
        sources.push_back(row);
        if (!s.enabled || !billable(s.source)) continue;
        liveCost += s.liveCost;
        const double w = std::max(s.liveOutput, 1e-9);
        weight += w;
        knownWeight += w * s.liveCostKnownShare;
        todayCost += s.todayCostMicroUsd;
        unpriced += s.unpricedRequests;
    }
    map.insert(QStringLiteral("sources"), sources);
    const double knownShare = weight > 0 ? knownWeight / weight : 1;
    const auto liveMoney = agentspeed::money::format(agentspeed::money::ratePerSecond(liveCost, rate), currency, language, Form::Rate);
    const auto todayMoney = agentspeed::money::format(agentspeed::money::amount(todayCost, rate), currency, language, Form::Amount);
    const bool livePartial = knownShare < 0.999;
    map.insert(QStringLiteral("costLiveNumber"), QString::fromStdString((livePartial ? "≥" : "") + liveMoney.number));
    map.insert(QStringLiteral("costLiveText"), QString::fromStdString((livePartial ? "≥" : "") + liveMoney.text));
    map.insert(QStringLiteral("costSymbol"), QString::fromStdString(liveMoney.symbol));
    map.insert(QStringLiteral("costTodayText"),
               todayCost == 0 && unpriced > 0 ? QStringLiteral("–") : QString::fromStdString((unpriced > 0 ? "≥" : "") + todayMoney.text));
    map.insert(QStringLiteral("costUnpriced"), static_cast<qint64>(unpriced));
    map.insert(QStringLiteral("liveCostMicroUsd"), liveCost);
    map.insert(QStringLiteral("priceVersion"), snap.priceVersion);
    QVariantMap warnings;
    warnings.insert(QStringLiteral("partialHistory"), snap.warnings.partialHistory);
    warnings.insert(QStringLiteral("unreadableFiles"), snap.warnings.unreadableFiles);
    warnings.insert(QStringLiteral("malformedLines"), static_cast<qint64>(snap.warnings.malformedLines));
    QStringList unsupported;
    for (const auto kind : snap.warnings.unsupported) unsupported.push_back(sourceKey(kind));
    warnings.insert(QStringLiteral("unsupported"), unsupported);
    map.insert(QStringLiteral("warnings"), warnings);
    return map;
}

void MonitorWorker::publish(const agentspeed::Snapshot& snap, const std::vector<agentspeed::Completion>& completions, qint64 now,
                            qint64 todayStart) {
    QVariantMap map = toVariant(snap);
    QVariantList events;
    for (const auto& c : completions) {
        QVariantMap event;
        event.insert(QStringLiteral("source"), sourceKey(c.source));
        event.insert(QStringLiteral("output"), static_cast<qint64>(c.output));
        event.insert(QStringLiteral("speed"), c.speed ? *c.speed : -1.0);
        events.push_back(event);
    }
    map.insert(QStringLiteral("completions"), events);
    bool newPeak = false;
    if (peakBefore_ < 0 || peakDay_ != todayStart) {
        peakBefore_ = snap.peakToday;
        peakDay_ = todayStart;
    } else if (snap.liveOutput >= 20 && snap.liveOutput >= peakBefore_ + 1 && now - lastPeakEventMs_ >= 60'000) {
        newPeak = true;
        lastPeakEventMs_ = now;
    }
    peakBefore_ = std::max({peakBefore_, snap.peakToday, snap.liveOutput});
    map.insert(QStringLiteral("newPeak"), newPeak);
    emit snapshotReady(map);
}

void MonitorWorker::tick() {
    const qint64 now = QDateTime::currentMSecsSinceEpoch();
    const qint64 todayStart = localMidnightMs(now);
    bool restart = false;
    {
        std::lock_guard lock(optionsMutex_);
        if (pending_) {
            options_ = std::move(*pending_);
            pending_.reset();
            restart = restartPending_;
            restartPending_ = false;
        }
    }
    if (restart || (monitor_ && monitor_->restartRequested())) {
        startMonitor(now);
        peakBefore_ = -1;
    }
    const qint64 previous = lastTickMs_;
    lastTickMs_ = now;
    if (demo_) {
        publish(agentspeed::demoSnapshot(now, todayStart), agentspeed::demoCompletions(previous, now), now, todayStart);
        return;
    }
    std::vector<agentspeed::Completion> completions;
    if (now - lastPollMs_ >= 1000) {
        monitor_->poll(now, todayStart);
        lastPollMs_ = now;
        completions = monitor_->takeCompletions();
    }
    publish(monitor_->snapshot(now, todayStart), completions, now, todayStart);
}
