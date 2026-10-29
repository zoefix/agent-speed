#pragma once

#include <QObject>
#include <QStringList>
#include <QVariantMap>
#include <chrono>
#include <memory>
#include <mutex>
#include <optional>
#include <vector>

#include "agentspeed/monitor.hpp"
#include "agentspeed/report_body.hpp"
#include "board/board_view.hpp"
#include "orbs/orb_views.hpp"

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
    bool trafficEnabled = true;
    bool trafficIncludeTools = true;
    bool trafficBits = false;
    int redline = 2000;
    bool totalMetric = false;
    std::vector<BoardSpec> boards;  // DESIGN 2c: every board's sources
    std::vector<OrbSpec> orbs;      // DESIGN 2b: every floating orb's sources
};

using TrafficRows = std::vector<agentspeed::traffic::HistoryRow>;
Q_DECLARE_METATYPE(TrafficRows)

// One engine tick for live reporting (METRICS 11.1): the instantaneous values and the ledger, on the monotonic clock.
struct ReportTick {
    std::shared_ptr<const agentspeed::report::Sample> sample;
    qint64 monotonicMs = 0;
    // METRICS 11.9 Observations: when the read that changed the engine before this tick began.
    std::optional<qint64> observedMs;
};
Q_DECLARE_METATYPE(ReportTick)

// The monotonic clock live reporting runs on, shared by the worker's ticks and the report controller.
inline qint64 steadyMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

// Runs the monitor off the UI thread and publishes one snapshot per engine tick (500 ms, METRICS 6).
class MonitorWorker : public QObject {
    Q_OBJECT
public:
    MonitorWorker(const Strings* strings, bool demo, WorkerOptions options);
    ~MonitorWorker() override;

    // Thread-safe. A changed monitor configuration restarts the monitor.
    void setOptions(WorkerOptions options, bool reconfigure);
    // Thread-safe: the prediction models calibrated on the history (METRICS 6.11), applied on the next tick.
    void setTaskModels(std::map<agentspeed::SourceKind, agentspeed::tasks::Models> models);
    // Thread-safe: the source sets whose 智力 and 思考 each snapshot carries (orbs, boards); the tray's set comes first.
    void setScoreSets(std::vector<std::optional<std::vector<agentspeed::SourceKind>>> sets);
    // Thread-safe: today's traffic saved before this launch (METRICS 10.8), added on the next tick.
    void restoreTraffic(TrafficRows rows);
    // Thread-safe: the task watch of live reporting (METRICS 11.10), applied on the next tick and to a new monitor.
    void setReportWatch(bool on, qint64 taken);
    // Thread-safe: a newer price table arrived; today's unpriced responses are priced again on the next tick.
    void requestReprice();
    // Worker thread: today's traffic rows now.
    TrafficRows trafficToday();

public slots:
    void start();
    void setDemo(bool demo);

signals:
    void snapshotReady(const QVariantMap& snapshot);
    // Every 60 s: today's traffic for the history store (METRICS 10.9).
    void trafficSave(TrafficRows rows);
    // Every live tick (never in demo mode): what live reporting reads (METRICS 11.2).
    void reportTick(ReportTick tick);

private:
    void tick();
    void startMonitor(qint64 now);
    void publish(const agentspeed::Snapshot& snap, const std::vector<agentspeed::Completion>& completions, qint64 now,
                 qint64 todayStart);
    QVariantMap toVariant(const agentspeed::Snapshot& snap) const;
    QVariantMap trafficVariant(const agentspeed::Snapshot& snap) const;

    const Strings* strings_;
    bool demo_;
    WorkerOptions options_;
    std::mutex optionsMutex_;
    std::optional<WorkerOptions> pending_;
    std::optional<std::map<agentspeed::SourceKind, agentspeed::tasks::Models>> pendingModels_;
    std::map<agentspeed::SourceKind, agentspeed::tasks::Models> models_;
    std::optional<std::vector<std::optional<std::vector<agentspeed::SourceKind>>>> pendingSets_;
    std::vector<std::optional<std::vector<agentspeed::SourceKind>>> sets_{std::nullopt};
    std::optional<TrafficRows> pendingTraffic_;
    bool pendingReprice_ = false;
    bool watchOn_ = false;
    qint64 watchTaken_ = 0;
    qint64 lastTrafficSaveMs_ = 0;
    bool restartPending_ = false;
    std::unique_ptr<agentspeed::Monitor> monitor_;
    QTimer* timer_ = nullptr;
    qint64 lastPollMs_ = 0;
    qint64 lastTickMs_ = 0;
    // newPeak (METRICS 6.4): the peak before this tick, the time of the last event, and the day they belong to.
    double peakBefore_ = -1;
    qint64 lastPeakEventMs_ = 0;
    qint64 peakDay_ = 0;
    // DESIGN 1: the dashboard's speed title level (the orb keeps its own tracker).
    agentspeed::report::TitleTracker title_;
    BoardViews boards_;
    OrbViews orbs_;
    // The requested score sets, then one per board, then one per orb.
    std::vector<std::optional<std::vector<agentspeed::SourceKind>>> allSets() const;
    std::vector<std::optional<std::vector<agentspeed::SourceKind>>> orbSets() const;
    std::vector<BoardSpec> appliedBoards_;
    std::vector<OrbSpec> appliedOrbs_;
};
