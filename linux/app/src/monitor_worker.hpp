#pragma once

#include <QObject>
#include <QVariantMap>
#include <memory>

class QTimer;
class Strings;

namespace agentspeed {
class Monitor;
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

    const Strings* strings_;
    bool demo_;
    std::unique_ptr<agentspeed::Monitor> monitor_;
    QTimer* timer_ = nullptr;
    qint64 lastPollMs_ = 0;
};
