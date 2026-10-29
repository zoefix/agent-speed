#include "monitor_worker.hpp"

#include <QDateTime>
#include <QTimer>
#include <QVariantList>
#include <algorithm>

#include "agentspeed/demo.hpp"
#include "agentspeed/monitor.hpp"
#include "format.hpp"
#include "heat.hpp"
#include "strings.hpp"

namespace {

qint64 localMidnightMs(qint64 nowMs) {
    const auto now = QDateTime::fromMSecsSinceEpoch(nowMs);
    return QDateTime(now.date(), QTime(0, 0)).toMSecsSinceEpoch();
}

QVariantMap toVariant(const agentspeed::Snapshot& snap, const Strings& strings) {
    QVariantMap map;
    map.insert(QStringLiteral("nowMs"), static_cast<qint64>(snap.nowMs));
    map.insert(QStringLiteral("liveOutput"), snap.liveOutput);
    map.insert(QStringLiteral("liveText"), format::speed(snap.liveOutput));
    map.insert(QStringLiteral("compactText"), format::compactSpeed(snap.liveOutput));
    map.insert(QStringLiteral("estimatedShare"), snap.estimatedShare);
    map.insert(QStringLiteral("todayOutputText"), format::tokens(snap.todayOutput));
    map.insert(QStringLiteral("todayRequestsText"), QString::number(snap.todayRequests));
    map.insert(QStringLiteral("todayAverageText"),
               snap.todayAverage ? format::speed(*snap.todayAverage) : QStringLiteral("–"));
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
        const auto source = QString::fromUtf8(agentspeed::sourceId(a.source));
        QVariantMap row;
        row.insert(QStringLiteral("id"), QString::fromStdString(a.id));
        row.insert(QStringLiteral("source"), source);
        row.insert(QStringLiteral("sourceName"), strings.t(QStringLiteral("source.") + source));
        row.insert(QStringLiteral("project"), a.project ? QString::fromStdString(*a.project) : QString());
        row.insert(QStringLiteral("model"), a.model ? QString::fromStdString(*a.model) : QString());
        row.insert(QStringLiteral("phase"), QString::fromUtf8(agentspeed::phaseId(a.phase)));
        row.insert(QStringLiteral("live"), a.liveOutput);
        row.insert(QStringLiteral("liveText"), format::speed(a.liveOutput));
        row.insert(QStringLiteral("depth"), a.depth);
        row.insert(QStringLiteral("subagent"), a.subagent);
        agents.push_back(row);
    }
    map.insert(QStringLiteral("agents"), agents);
    QVariantList sources;
    for (const auto& s : snap.sources) {
        const auto source = QString::fromUtf8(agentspeed::sourceId(s.source));
        QVariantMap row;
        row.insert(QStringLiteral("source"), source);
        row.insert(QStringLiteral("name"), strings.t(QStringLiteral("source.") + source));
        row.insert(QStringLiteral("live"), s.liveOutput);
        row.insert(QStringLiteral("todayText"), format::tokens(s.todayOutput));
        sources.push_back(row);
    }
    map.insert(QStringLiteral("sources"), sources);
    return map;
}

}  // namespace

MonitorWorker::MonitorWorker(const Strings* strings, bool demo) : strings_(strings), demo_(demo) {}
MonitorWorker::~MonitorWorker() = default;

void MonitorWorker::start() {
    const qint64 now = QDateTime::currentMSecsSinceEpoch();
    monitor_ = std::make_unique<agentspeed::Monitor>(agentspeed::MonitorConfig::fromEnvironment());
    monitor_->start(now, localMidnightMs(now));
    lastPollMs_ = now;
    timer_ = new QTimer(this);
    timer_->setInterval(500);
    connect(timer_, &QTimer::timeout, this, &MonitorWorker::tick);
    timer_->start();
    tick();
}

void MonitorWorker::setDemo(bool demo) {
    demo_ = demo;
    peakBefore_ = -1;
    tick();
}

void MonitorWorker::publish(const agentspeed::Snapshot& snap, const std::vector<agentspeed::Completion>& completions,
                            qint64 now, qint64 todayStart) {
    QVariantMap map = toVariant(snap, *strings_);
    QVariantList events;
    for (const auto& c : completions) {
        QVariantMap event;
        event.insert(QStringLiteral("source"), QString::fromUtf8(agentspeed::sourceId(c.source)));
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
