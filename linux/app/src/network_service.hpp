#pragma once

#include <QDateTime>
#include <QNetworkAccessManager>
#include <QObject>
#include <QPointer>
#include <QTimer>
#include <QUrl>
#include <functional>
#include <map>
#include <memory>

#include "agentspeed/money.hpp"

class Catalog;
class Preferences;
class Strings;
class QNetworkReply;
class QTemporaryDir;

namespace agentspeed::pricing {
class PriceBook;
}

// spec/NETWORK.md 1–4 and spec/UPDATES.md: the daily price-table sync, the exchange rates and the update check with
// one-click AppImage install. Every request is an HTTPS GET with only the listed headers, under the master switch.
class NetworkService : public QObject {
    Q_OBJECT
    Q_PROPERTY(QString priceLine READ priceLine NOTIFY pricesChanged)
    Q_PROPERTY(QString priceResult READ priceResult NOTIFY pricesChanged)
    Q_PROPERTY(bool priceChecking READ priceChecking NOTIFY pricesChanged)
    Q_PROPERTY(QString rateLine READ rateLine NOTIFY ratesChanged)
    Q_PROPERTY(bool rateRefreshing READ rateRefreshing NOTIFY ratesChanged)
    Q_PROPERTY(bool rateAttribution READ rateAttribution NOTIFY ratesChanged)
    Q_PROPERTY(QString updateState READ updateState NOTIFY updateChanged)
    Q_PROPERTY(QString updateLine READ updateLine NOTIFY updateChanged)
    Q_PROPERTY(QString availableVersion READ availableVersion NOTIFY updateChanged)
    Q_PROPERTY(QString releaseNotes READ releaseNotes NOTIFY updateChanged)
    Q_PROPERTY(double updateProgress READ updateProgress NOTIFY updateChanged)
    Q_PROPERTY(bool canInstall READ canInstall NOTIFY updateChanged)

public:
    NetworkService(Preferences& prefs, const Strings& strings, Catalog& catalog, std::shared_ptr<agentspeed::pricing::PriceBook> prices,
                   QString dataDir, QObject* parent = nullptr);
    ~NetworkService() override;
    // Loads the stored price table and rates, then schedules every feature as after a launch.
    void start();

    QString priceLine() const;
    QString priceResult() const { return priceResult_; }
    bool priceChecking() const { return priceChecking_; }
    QString rateLine() const;
    bool rateRefreshing() const { return rateRefreshing_; }
    bool rateAttribution() const;
    QString updateState() const { return updateState_; }
    QString updateLine() const;
    QString availableVersion() const { return release_.version; }
    QString releaseNotes() const { return release_.notes; }
    double updateProgress() const { return progress_; }
    bool canInstall() const;

    Q_INVOKABLE void checkPrices();
    Q_INVOKABLE void refreshRates();
    Q_INVOKABLE void checkUpdates();
    Q_INVOKABLE void installUpdate();
    Q_INVOKABLE void skipUpdate();
    Q_INVOKABLE void openRelease();
    Q_INVOKABLE void showUpdate() { emit updateWindowRequested(); }

signals:
    void pricesChanged();
    void ratesChanged();
    void updateChanged();
    // A newer price table became current: today's costs are priced again (NETWORK 3 Effect).
    void tableAdded();
    // An update was found by an automatic check (one notification per version).
    void updateFound(const QString& version);
    void updateWindowRequested();

private:
    struct Fetched {
        int status = 0;
        QByteArray body;
        QString etag;
        QString error;
    };
    using Done = std::function<void(const Fetched&)>;
    void fetch(const QUrl& url, const QByteArray& accept, qint64 cap, const QStringList& hosts, const QString& etag, Done done);

    // Prices (NETWORK 3).
    void loadStoredPrices();
    void syncPrices(bool manual);
    void tryPriceUrl(int index, bool manual, bool changed);
    void finishPrices(bool ok, bool manual, const QString& message);
    QJsonObject priceState() const;
    void savePriceState(const QJsonObject& state) const;

    // Rates (NETWORK 4).
    void loadStoredRates();
    void syncRates(bool manual);
    void tryProvider(int index, bool manual, std::optional<std::map<std::string, agentspeed::money::Decimal>> first, QString firstProvider,
                     std::int64_t firstDate);
    void acceptRates(const QString& provider, const QString& url, std::int64_t dateMs, const std::map<std::string, agentspeed::money::Decimal>& rates);
    void finishRates(bool ok, bool manual, const QString& message);

    // Updates (UPDATES).
    void runUpdateCheck(bool manual);
    void download(const QUrl& url, const QString& path, qint64 cap, std::function<void(bool, const QString&)> done);
    void failUpdate(const QString& reason);
    void schedule();

    Preferences& prefs_;
    const Strings& strings_;
    Catalog& catalog_;
    std::shared_ptr<agentspeed::pricing::PriceBook> prices_;
    QString dataDir_;
    QNetworkAccessManager nam_;
    QTimer hourly_;
    QDateTime launched_;
    QString priceResult_;
    bool priceChecking_ = false;
    bool rateRefreshing_ = false;
    QString rateError_;
    struct Rates {
        QString provider, url;
        std::int64_t dateMs = 0, fetchedMs = 0;
        std::map<std::string, agentspeed::money::Decimal> rates;
    };
    std::optional<Rates> rates_;
    qint64 lastRateAttemptMs_ = 0;
    qint64 lastPriceAttemptMs_ = 0;
    QString updateState_ = QStringLiteral("idle");
    QString updateError_;
    struct Release {
        QString tag, version, notes, page, assetUrl, sumsUrl, assetName;
        qint64 assetSize = 0;
    } release_;
    double progress_ = 0;
    std::unique_ptr<QTemporaryDir> updateDir_;
};
