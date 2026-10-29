#pragma once

#include <QObject>
#include <QVariantList>
#include <QVariantMap>
#include <atomic>
#include <map>
#include <memory>
#include <mutex>
#include <set>

#include "agentspeed/history_analyzer.hpp"
#include "agentspeed/locations.hpp"
#include "agentspeed/task_score.hpp"
#include "monitor_worker.hpp"

class QTimer;
class Strings;

// The task records of the history (METRICS 6.11) and the prediction models calibrated on them, per source.
using TaskRecords = std::shared_ptr<const std::vector<agentspeed::tasks::TaskRecord>>;
using TaskModels = std::map<agentspeed::SourceKind, agentspeed::tasks::Models>;
Q_DECLARE_METATYPE(TaskRecords)
Q_DECLARE_METATYPE(TaskModels)

namespace agentspeed::pricing {
class PriceBook;
}

// Runs the daily history analysis (METRICS 8) on its own thread: at launch, every 10 minutes and at midnight, and on
// request; records live peaks; publishes the day rows for the history window.
class HistoryWorker : public QObject {
    Q_OBJECT
public:
    HistoryWorker(std::shared_ptr<agentspeed::pricing::PriceBook> prices, QString storePath);
    ~HistoryWorker() override;

    // Thread-safe: the locations and sources the next analysis reads.
    void setScope(agentspeed::SourceLocations locations, std::set<agentspeed::SourceKind> enabled);
    void cancel() { cancelled_ = true; }

public slots:
    void start();
    void analyze();
    void clear();
    void recordPeak(const QString& day, double peak);
    void saveTraffic(TrafficRows rows);

signals:
    // Rows of every day and source; `progress` < 0 when idle.
    void daysReady(const QVariantList& rows, qint64 updatedMs);
    void analyzing(bool running);
    void tasksReady(TaskRecords records, TaskModels models);
    // Today's traffic saved by an earlier launch (METRICS 10.8), and every day's rows for the window.
    void trafficRestored(TrafficRows rows);
    void trafficReady(const QVariantList& rows, const QString& since);

private:
    void publish();
    void publishTraffic();

    std::shared_ptr<agentspeed::pricing::PriceBook> prices_;
    QString storePath_;
    std::unique_ptr<agentspeed::history::Store> store_;
    std::unique_ptr<agentspeed::history::Analyzer> analyzer_;
    std::mutex scopeMutex_;
    agentspeed::SourceLocations locations_;
    std::set<agentspeed::SourceKind> enabled_;
    std::atomic<bool> cancelled_{false};
    QTimer* timer_ = nullptr;
    qint64 lastDayStart_ = 0;
    qint64 lastRunMs_ = 0;
};

// The history for QML: day rows, the analysis state and formatting helpers.
class HistoryModel : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantList rows READ rows NOTIFY rowsChanged)
    Q_PROPERTY(bool analyzing READ analyzing NOTIFY analyzingChanged)
    Q_PROPERTY(qint64 updatedMs READ updatedMs NOTIFY rowsChanged)
    Q_PROPERTY(int tasksRevision READ tasksRevision NOTIFY tasksChanged)
    Q_PROPERTY(QVariantList trafficRows READ trafficRows NOTIFY trafficChanged)
    Q_PROPERTY(QString trafficSince READ trafficSince NOTIFY trafficChanged)
public:
    explicit HistoryModel(const Strings* strings = nullptr, QObject* parent = nullptr) : QObject(parent), strings_(strings) {}
    int tasksRevision() const { return tasksRevision_; }
    QVariantList trafficRows() const { return trafficRows_; }
    QString trafficSince() const { return trafficSince_; }
    // DESIGN 3c task performance over [first, last] (days `YYYY-MM-DD`), every source or `source` alone.
    Q_INVOKABLE QVariantMap taskComparison(const QString& first, const QString& last, const QString& source) const;
    // DESIGN 10: a day's traffic of a source ("all": the enabled sources), tools per the setting.
    Q_INVOKABLE QVariantMap trafficOf(const QString& date, const QString& source, bool includeTools) const;
    void setDisabledSources(const QStringList& sources) { disabled_ = sources; }
    QVariantList rows() const { return rows_; }
    bool analyzing() const { return analyzing_; }
    qint64 updatedMs() const { return updatedMs_; }

    // DESIGN 4b CSV: the rows shown, locale-independent; returns false when the file cannot be written.
    Q_INVOKABLE bool exportCsv(const QString& path, const QVariantList& rows, const QString& sourceCode, const QString& currency,
                               const QString& rate) const;
    Q_INVOKABLE QString chooseCsvPath(const QString& suggestedName) const;
    Q_INVOKABLE QString money(qint64 microUsd, const QString& currency, const QString& rate, const QString& language) const;
    // A byte total as METRICS 10.11 formats it.
    Q_INVOKABLE QString bytes(double value) const;

public slots:
    void applyRows(const QVariantList& rows, qint64 updatedMs);
    void setAnalyzing(bool running);
    void applyTasks(TaskRecords records);
    void applyTraffic(const QVariantList& rows, const QString& since);

signals:
    void rowsChanged();
    void analyzingChanged();
    void tasksChanged();
    void trafficChanged();
    void refreshRequested();
    void clearRequested();

private:
    QVariantList rows_;
    bool analyzing_ = false;
    qint64 updatedMs_ = 0;
    const Strings* strings_;
    TaskRecords tasks_;
    QVariantList trafficRows_;
    QString trafficSince_;
    QStringList disabled_;
    struct TrafficSums {
        qint64 rx = 0, tx = 0, rxTools = 0, txTools = 0;
    };
    TrafficSums trafficSums(const QString& date, const QString& source) const;
    int tasksRevision_ = 0;
};
