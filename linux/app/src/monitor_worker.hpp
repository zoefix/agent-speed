#pragma once

#include <QObject>
#include <QStringList>
#include <QVariantMap>
#include <memory>
#include <mutex>
#include <optional>
#include <vector>

#include "agentspeed/monitor.hpp"

class QTimer;
class Strings;

// What the worker needs from the settings; set from the UI thread, applied on the next tick.
struct WorkerOptions {
    agentspeed::MonitorConfig config;
    QString currency = QStringLiteral("USD");
    QString rate = QStringLiteral("1");  // units per US dollar, decimal text
    QStringList subscriptions;
    bool includeSubscriptions = false;
    QString language = QStringLiteral("en");
};

// Runs the monitor off the UI thread and publishes one snapshot per engine tick (500 ms, METRICS 6).
class MonitorWorker : public QObject {
    Q_OBJECT
public:
    MonitorWorker(const Strings* strings, bool demo, WorkerOptions options);
    ~MonitorWorker() override;

    // Thread-safe. A changed monitor configuration restarts the monitor.
    void setOptions(WorkerOptions options, bool reconfigure);

public slots:
    void start();
    void setDemo(bool demo);

signals:
    void snapshotReady(const QVariantMap& snapshot);

private:
    void tick();
    void startMonitor(qint64 now);
    void publish(const agentspeed::Snapshot& snap, const std::vector<agentspeed::Completion>& completions, qint64 now,
                 qint64 todayStart);
    QVariantMap toVariant(const agentspeed::Snapshot& snap) const;

    const Strings* strings_;
    bool demo_;
    WorkerOptions options_;
    std::mutex optionsMutex_;
    std::optional<WorkerOptions> pending_;
    bool restartPending_ = false;
    std::unique_ptr<agentspeed::Monitor> monitor_;
    QTimer* timer_ = nullptr;
    qint64 lastPollMs_ = 0;
    qint64 lastTickMs_ = 0;
    // newPeak (METRICS 6.4): the peak before this tick, the time of the last event, and the day they belong to.
    double peakBefore_ = -1;
    qint64 lastPeakEventMs_ = 0;
    qint64 peakDay_ = 0;
};
