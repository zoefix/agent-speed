#pragma once

#include <QObject>
#include <QStringList>
#include <QVariantList>
#include <QVariantMap>
#include <atomic>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <set>

#include "agentspeed/history_analyzer.hpp"
#include "agentspeed/locations.hpp"
#include "agentspeed/perf_history.hpp"
#include "agentspeed/remote_history.hpp"
#include "monitor_worker.hpp"

class QTimer;
class Strings;

// The performance records of the history (METRICS 6.11), summed over files per day, source and raw model, and the
// 14-day baselines of every (source, raw model) as of today.
using PerfRecords = std::shared_ptr<const std::vector<agentspeed::perf::DayRecord>>;
using PerfBaselines = std::map<std::pair<agentspeed::SourceKind, std::string>, double>;
Q_DECLARE_METATYPE(PerfRecords)
Q_DECLARE_METATYPE(PerfBaselines)

// The stored aggregates behind the day rows: their mode breakdowns (EXECUTION-MODES) and unpriced facts (METRICS 9.10).
// `days` holds every host's rows (REMOTE 8.3), `unpriced` every host's facts merged; `hostUnpriced` each host's own
// (`local` included) while host history exists.
struct HistoryDetail {
    std::vector<agentspeed::history::DailyUsage> days;
    std::vector<agentspeed::unpriced::Part> unpriced;
    std::map<std::string, std::vector<agentspeed::unpriced::Part>> hostUnpriced;
};
using HistoryDetails = std::shared_ptr<const HistoryDetail>;
Q_DECLARE_METATYPE(HistoryDetails)

namespace agentspeed::pricing {
class PriceBook;
}

// Sends a `history` request to a host's relay (REMOTE 8.2): "<day>/<source>" → digest of every day held for it.
using RemoteHistoryRequester = std::function<void(const std::string& host, const std::map<std::string, std::string>& digests)>;

// Runs the daily history analysis (METRICS 8) on its own thread: at launch, every 10 minutes and at midnight, and on
// request; records live peaks; keeps the remote hosts' days (REMOTE 8.2, 8.3); publishes the day rows for the history
// window.
class HistoryWorker : public QObject {
    Q_OBJECT
public:
    HistoryWorker(std::shared_ptr<agentspeed::pricing::PriceBook> prices, QString storePath);
    ~HistoryWorker() override;

    // Thread-safe: the locations and sources the next analysis reads.
    void setScope(agentspeed::SourceLocations locations, std::set<agentspeed::SourceKind> enabled);
    void cancel() { cancelled_ = true; }

    // Thread-safe, in order: what the history sync needs of a host's events (status changes, historyDue, the history
    // lines and a historyFailed error). Events that arrive before `start` wait for it.
    void receiveRemote(agentspeed::remote::HostEvent event);
    // Thread-safe: the settings' hosts with their display names; stored hosts take the new names, removed ones keep
    // their rows and last name (REMOTE 2.2).
    void setRemoteHosts(std::map<std::string, std::string> names);
    // Thread-safe: deletes a host's daily history (REMOTE 2.4 Also delete its usage history).
    void deleteRemoteHost(std::string id);
    // Thread-safe: how `historyDue` is answered, and how connected hosts are asked again after Clear history.
    void setRemoteRequester(RemoteHistoryRequester requester);

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
    // Files analysed so far of the files planned, while an analysis runs.
    void progress(int done, int total);
    void detailsReady(HistoryDetails details);
    void perfReady(PerfRecords records, PerfBaselines baselines);
    // Today's traffic saved by an earlier launch (METRICS 10.8), and every day's rows for the window.
    void trafficRestored(TrafficRows rows);
    void trafficReady(const QVariantList& rows, const QString& since);
    // The hosts with history ({id, name}; removed hosts under their last name), by name.
    void hostsReady(const QVariantList& hosts);
    // REMOTE 8.3 Bounds: the hosts whose last history batch was discarded (`historyTooLarge`) until a sync succeeds.
    void remoteTooLarge(const QStringList& hosts);

private:
    void publish();
    void publishTraffic();
    void applyRemote(const agentspeed::remote::HostEvent& event);
    void applyEffect(const agentspeed::remote::HistoryEffect& effect);
    void requestRemote(const std::string& host, const std::map<std::string, std::string>& digests);
    void publishTooLarge();

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
    agentspeed::remote::HistoryLedger ledger_;
    bool remoteReady_ = false;
    std::mutex requesterMutex_;
    RemoteHistoryRequester requester_;
};

// The history for QML: day rows, the analysis state and formatting helpers.
class HistoryModel : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantList rows READ rows NOTIFY rowsChanged)
    Q_PROPERTY(bool analyzing READ analyzing NOTIFY analyzingChanged)
    Q_PROPERTY(qint64 updatedMs READ updatedMs NOTIFY rowsChanged)
    Q_PROPERTY(int perfRevision READ perfRevision NOTIFY perfChanged)
    Q_PROPERTY(QVariantList trafficRows READ trafficRows NOTIFY trafficChanged)
    Q_PROPERTY(QString trafficSince READ trafficSince NOTIFY trafficChanged)
    Q_PROPERTY(int filesDone READ filesDone NOTIFY progressChanged)
    Q_PROPERTY(int filesTotal READ filesTotal NOTIFY progressChanged)
    Q_PROPERTY(int detailRevision READ detailRevision NOTIFY detailsChanged)
    // DESIGN 13: the hosts with history ({id, name}, by name; removed hosts under their last name) and whether any
    // exists (the host picker and the CSV `host` column appear only then). Every row carries `host` and `hostName`.
    Q_PROPERTY(QVariantList hosts READ hosts NOTIFY hostsChanged)
    Q_PROPERTY(bool hasHostHistory READ hasHostHistory NOTIFY hostsChanged)
    Q_PROPERTY(QStringList tooLargeHosts READ tooLargeHosts NOTIFY tooLargeHostsChanged)
public:
    explicit HistoryModel(const Strings* strings = nullptr, QObject* parent = nullptr) : QObject(parent), strings_(strings) {}
    void setPrices(std::shared_ptr<agentspeed::pricing::PriceBook> prices) { prices_ = std::move(prices); }
    int filesDone() const { return filesDone_; }
    int filesTotal() const { return filesTotal_; }
    int detailRevision() const { return detailRevision_; }
    // DESIGN 4b 3b: one row per value of `kind` (effort | speed) over [first, last] for a source or all, in display
    // order with the unknown bucket last; each row carries its top three models. `host` (empty: every host) combines
    // with the source (DESIGN 13).
    Q_INVOKABLE QVariantMap modeSection(const QString& first, const QString& last, const QString& source, const QString& kind,
                                        const QString& host = QString()) const;
    // The mode CSV of EXECUTION-MODES for the same days (one row per day and source, every host summed unless `host`);
    // returns false when the file cannot be written.
    Q_INVOKABLE bool exportModeCsv(const QString& path, const QString& first, const QString& last, const QString& source,
                                   const QString& host = QString()) const;
    // DESIGN 9 Unknown prices: the groups of METRICS 9.10 under the current price table, and the text to copy.
    Q_INVOKABLE QVariantMap unpricedView(const QString& first, const QString& last, const QString& source, const QStringList& excluded,
                                         const QString& view, const QString& host = QString()) const;
    Q_INVOKABLE bool copyText(const QString& text) const;
    int perfRevision() const { return perfRevision_; }
    QVariantList trafficRows() const { return trafficRows_; }
    QString trafficSince() const { return trafficSince_; }
    // DESIGN 12 性能表現 over [first, last] (days `YYYY-MM-DD`), every source or `source` alone: one row per (source,
    // model) with its pooled values, baseline and daily trend.
    Q_INVOKABLE QVariantMap perfSection(const QString& first, const QString& last, const QString& source) const;
    // DESIGN 10: a day's traffic of a source ("all": the enabled sources), tools per the setting.
    Q_INVOKABLE QVariantMap trafficOf(const QString& date, const QString& source, bool includeTools) const;
    void setDisabledSources(const QStringList& sources) { disabled_ = sources; }
    QVariantList rows() const { return rows_; }
    bool analyzing() const { return analyzing_; }
    qint64 updatedMs() const { return updatedMs_; }
    QVariantList hosts() const { return hosts_; }
    bool hasHostHistory() const { return !hosts_.isEmpty(); }
    QStringList tooLargeHosts() const { return tooLargeHosts_; }

    // DESIGN 4b CSV: the rows shown, locale-independent; returns false when the file cannot be written. A non-empty
    // `host` (the selected host's name, `local` for this computer) adds the trailing `host` column (DESIGN 13);
    // without `localTraffic` (a remote host) the traffic columns stay empty.
    Q_INVOKABLE bool exportCsv(const QString& path, const QVariantList& rows, const QString& sourceCode, const QString& currency,
                               const QString& rate, const QString& host = QString(), bool localTraffic = true) const;
    Q_INVOKABLE QString chooseCsvPath(const QString& suggestedName) const;
    Q_INVOKABLE QString money(qint64 microUsd, const QString& currency, const QString& rate, const QString& language) const;
    // A byte total as METRICS 10.11 formats it.
    Q_INVOKABLE QString bytes(double value) const;

public slots:
    void applyRows(const QVariantList& rows, qint64 updatedMs);
    void setAnalyzing(bool running);
    void applyPerf(PerfRecords records, PerfBaselines baselines);
    void applyTraffic(const QVariantList& rows, const QString& since);
    void applyProgress(int done, int total);
    void applyDetails(HistoryDetails details);
    void applyHosts(const QVariantList& hosts);
    void applyTooLarge(const QStringList& hosts);

signals:
    void rowsChanged();
    void analyzingChanged();
    void perfChanged();
    void trafficChanged();
    void progressChanged();
    void detailsChanged();
    void hostsChanged();
    void tooLargeHostsChanged();
    void refreshRequested();
    void clearRequested();

private:
    QVariantList rows_;
    bool analyzing_ = false;
    qint64 updatedMs_ = 0;
    const Strings* strings_;
    PerfRecords perf_;
    PerfBaselines baselines_;
    QVariantList trafficRows_;
    QString trafficSince_;
    QStringList disabled_;
    struct TrafficSums {
        qint64 rx = 0, tx = 0, rxTools = 0, txTools = 0;
    };
    TrafficSums trafficSums(const QString& date, const QString& source) const;
    int perfRevision_ = 0;
    std::shared_ptr<agentspeed::pricing::PriceBook> prices_;
    HistoryDetails details_;
    int detailRevision_ = 0;
    QVariantList hosts_;
    QStringList tooLargeHosts_;
    int filesDone_ = 0, filesTotal_ = 0;
};
