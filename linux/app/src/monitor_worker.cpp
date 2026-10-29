#include "monitor_worker.hpp"

#include <QDateTime>
#include <QTimer>
#include <QVariantList>

#include "agentspeed/demo.hpp"
#include "agentspeed/monitor.hpp"
#include "format.hpp"
#include "strings.hpp"

namespace {

qint64 localMidnightMs(qint64 nowMs) {
    const auto now = QDateTime::fromMSecsSinceEpoch(nowMs);
    return QDateTime(now.date(), QTime(0, 0)).toMSecsSinceEpoch();
}

QVariantMap toVariant(const agentspeed::Snapshot& snap, const Strings& strings) {
    QVariantMap map;
    map.insert(QStringLiteral("liveOutput"), snap.liveOutput);
    map.insert(QStringLiteral("liveText"), format::speed(snap.liveOutput));
    map.insert(QStringLiteral("compactText"), format::compactSpeed(snap.liveOutput));
    map.insert(QStringLiteral("estimatedShare"), snap.estimatedShare);
    map.insert(QStringLiteral("todayOutputText"), format::tokens(snap.todayOutput));
    map.insert(QStringLiteral("todayRequestsText"), QString::number(snap.todayRequests));
    map.insert(QStringLiteral("todayAverageText"),
               snap.todayAverage ? format::speed(*snap.todayAverage) : QStringLiteral("–"));
    map.insert(QStringLiteral("peakText"), format::speed(snap.peakToday));
    map.insert(QStringLiteral("generating"), snap.generating);
    map.insert(QStringLiteral("tools"), snap.tools);
    map.insert(QStringLiteral("demo"), snap.demo);
    QVariantList agents;
    for (const auto& a : snap.agents) {
        const auto source = QString::fromUtf8(agentspeed::sourceId(a.source));
        QVariantMap row;
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
        row.insert(QStringLiteral("liveText"), format::speed(s.liveOutput));
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
    tick();
}

void MonitorWorker::tick() {
    const qint64 now = QDateTime::currentMSecsSinceEpoch();
    const qint64 todayStart = localMidnightMs(now);
    if (demo_) {
        emit snapshotReady(toVariant(agentspeed::demoSnapshot(now, todayStart), *strings_));
        return;
    }
    if (now - lastPollMs_ >= 1000) {
        monitor_->poll(now, todayStart);
        lastPollMs_ = now;
    }
    emit snapshotReady(toVariant(monitor_->snapshot(now, todayStart), *strings_));
}
