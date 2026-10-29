#pragma once

#include <QObject>
#include <QVariantMap>
#include <memory>
#include <vector>

class QTimer;
class Strings;

namespace agentspeed {
class Monitor;
struct Snapshot;
struct Completion;
}

// Runs the monitor off the UI thread and publishes one snapshot per engine tick (500 ms, METRICS 6).
class MonitorWorker : public QObject {
    Q_OBJECT
public:
    MonitorWorker(const Strings* strings, bool demo);
    ~MonitorWorker() override;

public slots:
    void start();
    void setDemo(bool demo);

signals:
    void snapshotReady(const QVariantMap& snapshot);

private:
    void tick();
    void publish(const agentspeed::Snapshot& snap, const std::vector<agentspeed::Completion>& completions, qint64 now,
                 qint64 todayStart);

    const Strings* strings_;
    bool demo_;
    std::unique_ptr<agentspeed::Monitor> monitor_;
    QTimer* timer_ = nullptr;
    qint64 lastPollMs_ = 0;
    qint64 lastTickMs_ = 0;
    // newPeak (METRICS 6.4): the peak before this tick, the time of the last event, and the day they belong to.
    double peakBefore_ = -1;
    qint64 lastPeakEventMs_ = 0;
    qint64 peakDay_ = 0;
};
