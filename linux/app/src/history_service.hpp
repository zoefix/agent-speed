#pragma once

#include <QObject>
#include <QVariantList>
#include <QVariantMap>
#include <atomic>
#include <memory>
#include <mutex>
#include <set>

#include "agentspeed/history_analyzer.hpp"
#include "agentspeed/locations.hpp"

class QTimer;

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
public:
    explicit HistoryModel(QObject* parent = nullptr) : QObject(parent) {}
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

signals:
    void rowsChanged();
    void analyzingChanged();
    void refreshRequested();
    void clearRequested();

private:
    QVariantList rows_;
    bool analyzing_ = false;
    qint64 updatedMs_ = 0;
};
