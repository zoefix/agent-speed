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

signals:
    // Rows of every day and source; `progress` < 0 when idle.
    void daysReady(const QVariantList& rows, qint64 updatedMs);
    void analyzing(bool running);
    void tasksReady(TaskRecords records, TaskModels models);

private:
    void publish();

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
public:
    explicit HistoryModel(const Strings* strings = nullptr, QObject* parent = nullptr) : QObject(parent), strings_(strings) {}
    int tasksRevision() const { return tasksRevision_; }
    // DESIGN 3c task performance over [first, last] (days `YYYY-MM-DD`), every source or `source` alone.
    Q_INVOKABLE QVariantMap taskComparison(const QString& first, const QString& last, const QString& source) const;
    QVariantList rows() const { return rows_; }
    bool analyzing() const { return analyzing_; }
    qint64 updatedMs() const { return updatedMs_; }

    // DESIGN 4b CSV: the rows shown, locale-independent; returns false when the file cannot be written.
    Q_INVOKABLE bool exportCsv(const QString& path, const QVariantList& rows, const QString& sourceCode, const QString& currency,
                               const QString& rate) const;
    Q_INVOKABLE QString chooseCsvPath(const QString& suggestedName) const;
    Q_INVOKABLE QString money(qint64 microUsd, const QString& currency, const QString& rate, const QString& language) const;

public slots:
    void applyRows(const QVariantList& rows, qint64 updatedMs);
    void setAnalyzing(bool running);
    void applyTasks(TaskRecords records);

signals:
    void rowsChanged();
    void analyzingChanged();
    void tasksChanged();
    void refreshRequested();
    void clearRequested();

private:
    QVariantList rows_;
    bool analyzing_ = false;
    qint64 updatedMs_ = 0;
    const Strings* strings_;
    TaskRecords tasks_;
    int tasksRevision_ = 0;
};
