#include "network_service.hpp"

#include <QCoreApplication>
#include <QCryptographicHash>
#include <QDesktopServices>
#include <QDir>
#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QNetworkCookie>
#include <QNetworkCookieJar>
#include <QNetworkProxyFactory>
#include <QNetworkReply>
#include <QProcess>
#include <QRegularExpression>
#include <QSaveFile>
#include <QSettings>
#include <QSysInfo>
#include <QTemporaryDir>
#include <set>
#include <sys/stat.h>
#include <unistd.h>

#include "agentspeed/price_book.hpp"
#include "agentspeed/pricing.hpp"
#include "catalog.hpp"
#include "preferences.hpp"
#include "strings.hpp"
#include "task_card.hpp"

namespace {

constexpr qint64 kDayMs = 24 * 3600 * 1000;
constexpr qint64 kHourMs = 3600 * 1000;
constexpr qint64 kUpdateIntervalMs = 6 * kHourMs;
constexpr qint64 kPriceCap = 1 << 20;
constexpr qint64 kRateCap = 256 * 1024;
constexpr qint64 kReleaseCap = 1 << 20;
constexpr qint64 kAssetCap = 300LL * 1024 * 1024;

const QStringList kPriceHosts = {QStringLiteral("raw.githubusercontent.com"), QStringLiteral("cdn.jsdelivr.net")};
const QStringList kPriceUrls = {QStringLiteral("https://raw.githubusercontent.com/zoefix/agent-speed/main/spec/assets/prices.json"),
                                QStringLiteral("https://cdn.jsdelivr.net/gh/zoefix/agent-speed@main/spec/assets/prices.json")};
const QStringList kUpdateHosts = {QStringLiteral("api.github.com"), QStringLiteral("github.com"), QStringLiteral("objects.githubusercontent.com"),
                                  QStringLiteral("release-assets.githubusercontent.com")};

struct Provider {
    const char* url;
    agentspeed::money::rates::Shape shape;
    const char* host;
};
const Provider kProviders[] = {
    {"https://open.er-api.com/v6/latest/USD", agentspeed::money::rates::Shape::ErApi, "open.er-api.com"},
    {"https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@latest/v1/currencies/usd.min.json", agentspeed::money::rates::Shape::Fawaz,
     "cdn.jsdelivr.net"},
    {"https://latest.currency-api.pages.dev/v1/currencies/usd.min.json", agentspeed::money::rates::Shape::Fawaz, "latest.currency-api.pages.dev"},
    {"https://api.frankfurter.dev/v1/latest?base=USD", agentspeed::money::rates::Shape::Frankfurter, "api.frankfurter.dev"},
};

// No cookies are ever kept or sent (NETWORK 1).
class NoCookies : public QNetworkCookieJar {
public:
    using QNetworkCookieJar::QNetworkCookieJar;
    bool setCookiesFromUrl(const QList<QNetworkCookie>&, const QUrl&) override { return false; }
    QList<QNetworkCookie> cookiesForUrl(const QUrl&) const override { return {}; }
};

QByteArray userAgent() { return "AgentSpeed/" + QCoreApplication::applicationVersion().toUtf8() + " (Linux)"; }

struct Version {
    int major = 0, minor = 0, patch = 0;
    bool prerelease = false;
    bool operator<(const Version& o) const {
        if (major != o.major) return major < o.major;
        if (minor != o.minor) return minor < o.minor;
        if (patch != o.patch) return patch < o.patch;
        return prerelease && !o.prerelease;
    }
};

std::optional<Version> parseVersion(const QString& text) {
    static const QRegularExpression pattern(QStringLiteral("^v?(\\d+)\\.(\\d+)\\.(\\d+)(-[0-9A-Za-z.-]+)?$"));
    const auto m = pattern.match(text.trimmed());
    if (!m.hasMatch()) return std::nullopt;
    return Version{m.captured(1).toInt(), m.captured(2).toInt(), m.captured(3).toInt(), !m.captured(4).isEmpty()};
}

QString hostOf(const QString& url) { return QUrl(url).host(); }

QString shortDate(qint64 ms) { return QDateTime::fromMSecsSinceEpoch(ms).toString(QStringLiteral("yyyy/MM/dd HH:mm")); }

}  // namespace

NetworkService::NetworkService(Preferences& prefs, const Strings& strings, Catalog& catalog, std::shared_ptr<agentspeed::pricing::PriceBook> prices,
                               QString dataDir, QObject* parent)
    : QObject(parent), prefs_(prefs), strings_(strings), catalog_(catalog), prices_(std::move(prices)), dataDir_(std::move(dataDir)) {
    QNetworkProxyFactory::setUseSystemConfiguration(true);
    nam_.setCookieJar(new NoCookies(&nam_));
    nam_.setRedirectPolicy(QNetworkRequest::UserVerifiedRedirectPolicy);
    hourly_.setInterval(static_cast<int>(kHourMs));
    connect(&hourly_, &QTimer::timeout, this, &NetworkService::schedule);
    connect(&prefs_, &Preferences::networkEnabledChanged, this, [this] {
        // Turning the switch back on schedules each feature as after a launch.
        if (prefs_.networkEnabled()) launched_ = QDateTime::currentDateTime();
        emit pricesChanged();
        emit ratesChanged();
        emit updateChanged();
    });
    connect(&prefs_, &Preferences::currencyChanged, this, [this] {
        if (prefs_.currency() == QLatin1String("USD")) return;
        if (!rates_ || QDateTime::currentMSecsSinceEpoch() - rates_->fetchedMs >= kDayMs) syncRates(false);
        emit ratesChanged();
    });
}

NetworkService::~NetworkService() = default;

void NetworkService::start() {
    launched_ = QDateTime::currentDateTime();
    loadStoredPrices();
    loadStoredRates();
    hourly_.start();
    QTimer::singleShot(30'000, this, [this] {
        if (prefs_.currency() != QLatin1String("USD")) syncRates(false);
        const auto last = QSettings().value(QStringLiteral("update/lastCheckMs"), 0).toLongLong();
        if (QDateTime::currentMSecsSinceEpoch() - last >= kUpdateIntervalMs) runUpdateCheck(false);
    });
    QTimer::singleShot(60'000, this, [this] { syncPrices(false); });
}

// Hourly: each feature runs when its interval has passed since its last success (NETWORK 1 Clock).
void NetworkService::schedule() {
    if (!prefs_.networkEnabled()) return;
    const auto now = QDateTime::currentMSecsSinceEpoch();
    const auto state = priceState();
    const auto lastPrice = static_cast<qint64>(state.value(QStringLiteral("lastSuccessMs")).toDouble());
    if (now - lastPrice >= kDayMs && now - lastPriceAttemptMs_ >= kHourMs) syncPrices(false);
    if (prefs_.currency() != QLatin1String("USD") && (!rates_ || now - rates_->fetchedMs >= kDayMs) && now - lastRateAttemptMs_ >= kHourMs) {
        syncRates(false);
    }
    const auto lastUpdate = QSettings().value(QStringLiteral("update/lastCheckMs"), 0).toLongLong();
    if (now - lastUpdate >= kUpdateIntervalMs) runUpdateCheck(false);
}

void NetworkService::fetch(const QUrl& url, const QByteArray& accept, qint64 cap, const QStringList& hosts, const QString& etag, Done done) {
    QNetworkRequest request(url);
    request.setTransferTimeout(15'000);
    request.setAttribute(QNetworkRequest::CacheLoadControlAttribute, QNetworkRequest::AlwaysNetwork);
    request.setAttribute(QNetworkRequest::CacheSaveControlAttribute, false);
    request.setRawHeader("User-Agent", userAgent());
    request.setRawHeader("Accept", accept);
    if (url.host() == QLatin1String("api.github.com")) request.setRawHeader("X-GitHub-Api-Version", "2022-11-28");
    if (!etag.isEmpty()) request.setRawHeader("If-None-Match", etag.toUtf8());
    auto* reply = nam_.get(request);
    connect(reply, &QNetworkReply::redirected, reply, [reply, hosts](const QUrl& to) {
        if (to.scheme() == QLatin1String("https") && hosts.contains(to.host())) emit reply->redirectAllowed();
        else reply->abort();
    });
    connect(reply, &QNetworkReply::downloadProgress, reply, [reply, cap](qint64 received, qint64 total) {
        if (received > cap || total > cap) {
            reply->setProperty("tooBig", true);
            reply->abort();
        }
    });
    connect(reply, &QNetworkReply::finished, this, [reply, done, url] {
        Fetched f;
        f.status = reply->attribute(QNetworkRequest::HttpStatusCodeAttribute).toInt();
        f.body = reply->readAll();
        f.etag = QString::fromUtf8(reply->rawHeader("ETag"));
        if (reply->property("tooBig").toBool()) f.error = QStringLiteral("too large");
        else if (reply->error() != QNetworkReply::NoError && f.status != 304) f.error = reply->errorString();
        else if (f.status != 304 && (f.status < 200 || f.status >= 300)) f.error = QStringLiteral("HTTP %1").arg(f.status);
        if (!f.error.isEmpty()) qInfo("network: %s %d %lld bytes: %s", qPrintable(url.host()), f.status, static_cast<long long>(f.body.size()), qPrintable(f.error));
        reply->deleteLater();
        done(f);
    });
}

// ---- Prices (NETWORK 3)

QJsonObject NetworkService::priceState() const {
    QFile file(dataDir_ + QStringLiteral("/prices/sync.json"));
    if (!file.open(QIODevice::ReadOnly)) return {};
    return QJsonDocument::fromJson(file.readAll()).object();
}

void NetworkService::savePriceState(const QJsonObject& state) const {
    QDir().mkpath(dataDir_ + QStringLiteral("/prices"));
    QSaveFile file(dataDir_ + QStringLiteral("/prices/sync.json"));
    if (!file.open(QIODevice::WriteOnly)) return;
    file.write(QJsonDocument(state).toJson(QJsonDocument::Compact));
    file.commit();
}

void NetworkService::loadStoredPrices() {
    QFile file(dataDir_ + QStringLiteral("/prices/prices.json"));
    if (!file.open(QIODevice::ReadOnly)) return;
    const auto bytes = file.readAll();
    file.close();
    agentspeed::pricing::TableError error;
    if (auto table = agentspeed::pricing::PriceTable::load(std::string_view(bytes.constData(), static_cast<std::size_t>(bytes.size())), &error)) {
        if (prices_->add(table)) emit tableAdded();
    } else {
        qInfo("network: stored price table invalid, removed: %s", error.describe().c_str());
        QFile::remove(file.fileName());
    }
}

void NetworkService::checkPrices() { syncPrices(true); }

void NetworkService::syncPrices(bool manual) {
    if (!prefs_.networkEnabled() || priceChecking_ || (!manual && !prefs_.syncPrices())) return;
    priceChecking_ = true;
    priceResult_.clear();
    lastPriceAttemptMs_ = QDateTime::currentMSecsSinceEpoch();
    emit pricesChanged();
    tryPriceUrl(0, manual, false);
}

void NetworkService::tryPriceUrl(int index, bool manual, bool changed) {
    if (index >= kPriceUrls.size()) {
        finishPrices(false, manual, priceResult_.isEmpty() ? strings_.t(QStringLiteral("update.failed.network")) : priceResult_);
        return;
    }
    const QString url = kPriceUrls[index];
    const auto known = priceState().value(QStringLiteral("etags")).toObject();
    fetch(QUrl(url), "application/json", kPriceCap, kPriceHosts, known.value(url).toString(), [this, index, manual, url, changed](const Fetched& f) {
        if (!f.error.isEmpty()) {
            priceResult_ = f.error;
            tryPriceUrl(index + 1, manual, changed);
            return;
        }
        auto state = priceState();
        auto etags = state.value(QStringLiteral("etags")).toObject();
        if (f.status == 304) {
            state.insert(QStringLiteral("lastSuccessMs"), static_cast<double>(QDateTime::currentMSecsSinceEpoch()));
            savePriceState(state);
            finishPrices(true, manual, strings_.t(QStringLiteral("prices.upToDate")));
            return;
        }
        agentspeed::pricing::TableError error;
        const auto table = agentspeed::pricing::PriceTable::load(std::string_view(f.body.constData(), static_cast<std::size_t>(f.body.size())), &error);
        if (!table) {
            if (error.kind == agentspeed::pricing::TableErrorKind::UnsupportedSchema && error.schema &&
                *error.schema > agentspeed::pricing::PriceTable::kSupportedSchema) {
                finishPrices(true, manual, strings_.t(QStringLiteral("prices.newerSchema")));
                return;
            }
            priceResult_ = QString::fromStdString(error.describe());
            tryPriceUrl(index + 1, manual, changed);
            return;
        }
        // Never a downgrade, and never a table dated more than 2 days ahead.
        const auto latest = QDate::currentDate().addDays(2).toString(Qt::ISODate).toStdString();
        if (table->updated() > latest) {
            priceResult_ = QStringLiteral("updated in the future");
            tryPriceUrl(index + 1, manual, changed);
            return;
        }
        etags.insert(url, f.etag);
        state.insert(QStringLiteral("etags"), etags);
        state.insert(QStringLiteral("lastSuccessMs"), static_cast<double>(QDateTime::currentMSecsSinceEpoch()));
        state.insert(QStringLiteral("url"), url);
        if (table->version() <= prices_->currentVersion()) {
            savePriceState(state);
            finishPrices(true, manual, strings_.t(QStringLiteral("prices.upToDate")));
            return;
        }
        QDir().mkpath(dataDir_ + QStringLiteral("/prices"));
        QSaveFile file(dataDir_ + QStringLiteral("/prices/prices.json"));
        if (file.open(QIODevice::WriteOnly)) {
            file.write(f.body);
            file.commit();
        }
        state.insert(QStringLiteral("version"), table->version());
        state.insert(QStringLiteral("updated"), QString::fromStdString(table->updated()));
        savePriceState(state);
        prices_->add(table);
        emit tableAdded();
        finishPrices(true, manual, task_card::fmt(strings_, QStringLiteral("prices.updatedTo"), {table->version()}));
    });
}

void NetworkService::finishPrices(bool ok, bool manual, const QString& message) {
    priceChecking_ = false;
    priceResult_ = manual ? (ok ? message : task_card::fmt(strings_, QStringLiteral("prices.failed"), {message})) : QString();
    emit pricesChanged();
}

QString NetworkService::priceLine() const {
    if (!prefs_.networkEnabled()) return strings_.t(QStringLiteral("settings.network.offHint"));
    if (priceChecking_) return strings_.t(QStringLiteral("prices.checking"));
    const auto last = static_cast<qint64>(priceState().value(QStringLiteral("lastSuccessMs")).toDouble());
    if (last <= 0) return strings_.t(QStringLiteral("prices.never"));
    return task_card::fmt(strings_, QStringLiteral("prices.lastSync"), {shortDate(last)});
}

// ---- Rates (NETWORK 4)

void NetworkService::loadStoredRates() {
    QFile file(dataDir_ + QStringLiteral("/rates.json"));
    if (!file.open(QIODevice::ReadOnly)) return;
    const auto o = QJsonDocument::fromJson(file.readAll()).object();
    Rates r;
    r.provider = o.value(QStringLiteral("provider")).toString();
    r.url = o.value(QStringLiteral("url")).toString();
    r.dateMs = static_cast<std::int64_t>(o.value(QStringLiteral("date")).toDouble());
    r.fetchedMs = static_cast<std::int64_t>(o.value(QStringLiteral("fetchedMs")).toDouble());
    const auto rates = o.value(QStringLiteral("rates")).toObject();
    for (auto it = rates.begin(); it != rates.end(); ++it) {
        if (const auto d = agentspeed::money::Decimal::parse(it.value().toString().toStdString())) r.rates[it.key().toStdString()] = *d;
    }
    if (r.rates.empty()) return;
    rates_ = r;
    catalog_.setAcceptedRates(r.provider, r.dateMs, r.rates);
}

void NetworkService::refreshRates() { syncRates(true); }

void NetworkService::syncRates(bool manual) {
    if (!prefs_.networkEnabled() || rateRefreshing_ || prefs_.currency() == QLatin1String("USD")) return;
    rateRefreshing_ = true;
    rateError_.clear();
    lastRateAttemptMs_ = QDateTime::currentMSecsSinceEpoch();
    emit ratesChanged();
    tryProvider(0, manual, std::nullopt, {}, 0);
}

void NetworkService::tryProvider(int index, bool manual, std::optional<std::map<std::string, agentspeed::money::Decimal>> first,
                                 QString firstProvider, std::int64_t firstDate) {
    if (index >= static_cast<int>(std::size(kProviders))) {
        finishRates(false, manual, rateError_.isEmpty() ? strings_.t(QStringLiteral("update.failed.network")) : rateError_);
        return;
    }
    const auto& p = kProviders[index];
    const QString url = QString::fromLatin1(p.url);
    fetch(QUrl(url), "application/json", kRateCap, {QString::fromLatin1(p.host)}, {}, [=, this](const Fetched& f) {
        if (!f.error.isEmpty()) {
            rateError_ = f.error;
            tryProvider(index + 1, manual, first, firstProvider, firstDate);
            return;
        }
        std::set<std::string> supported;
        for (const auto& c : agentspeed::money::CurrencyTable::bundled().currencies()) supported.insert(c.code);
        std::string reason;
        const auto parsed = agentspeed::money::rates::parse(p.shape, std::string_view(f.body.constData(), static_cast<std::size_t>(f.body.size())),
                                                            supported, &reason);
        if (!parsed) {
            rateError_ = QString::fromStdString(reason);
            tryProvider(index + 1, manual, first, firstProvider, firstDate);
            return;
        }
        const auto selected = prefs_.currency().toStdString();
        std::optional<agentspeed::money::rates::Reference> last;
        if (rates_) last = agentspeed::money::rates::Reference{rates_->dateMs, rates_->fetchedMs, rates_->rates};
        std::map<std::string, agentspeed::money::Decimal> fallback;
        for (const auto& [code, rate] : agentspeed::money::CurrencyTable::bundled().fallbackRates().rates) fallback[code] = rate;
        const auto verdict = agentspeed::money::rates::check(parsed->dateMs, parsed->rates, selected, QDateTime::currentMSecsSinceEpoch(), last,
                                                             &fallback, first ? &*first : nullptr, &reason);
        using agentspeed::money::rates::Verdict;
        if (verdict == Verdict::Accepted) {
            // With a cross-check, the first provider's rates are taken.
            if (first) acceptRates(firstProvider, url, firstDate, *first);
            else acceptRates(QString::fromLatin1(p.host), url, parsed->dateMs, parsed->rates);
            finishRates(true, manual, {});
        } else if (verdict == Verdict::NeedsSecondProvider && !first) {
            tryProvider(index + 1, manual, parsed->rates, QString::fromLatin1(p.host), parsed->dateMs);
        } else {
            rateError_ = QString::fromStdString(reason);
            tryProvider(index + 1, manual, first, firstProvider, firstDate);
        }
    });
}

void NetworkService::acceptRates(const QString& provider, const QString& url, std::int64_t dateMs,
                                 const std::map<std::string, agentspeed::money::Decimal>& rates) {
    Rates r;
    r.provider = provider;
    r.url = url;
    r.dateMs = dateMs;
    r.fetchedMs = QDateTime::currentMSecsSinceEpoch();
    // Provider 3's missing currencies keep their previous rates.
    if (rates_) r.rates = rates_->rates;
    for (const auto& [code, rate] : rates) r.rates[code] = rate;
    rates_ = r;
    QJsonObject o;
    o.insert(QStringLiteral("provider"), provider);
    o.insert(QStringLiteral("url"), url);
    o.insert(QStringLiteral("date"), static_cast<double>(dateMs));
    o.insert(QStringLiteral("fetchedMs"), static_cast<double>(r.fetchedMs));
    QJsonObject list;
    for (const auto& [code, rate] : r.rates) list.insert(QString::fromStdString(code), QString::fromStdString(rate.text()));
    o.insert(QStringLiteral("rates"), list);
    QDir().mkpath(dataDir_);
    QSaveFile file(dataDir_ + QStringLiteral("/rates.json"));
    if (file.open(QIODevice::WriteOnly)) {
        file.write(QJsonDocument(o).toJson(QJsonDocument::Compact));
        file.commit();
    }
    catalog_.setAcceptedRates(provider, dateMs, r.rates);
}

void NetworkService::finishRates(bool ok, bool manual, const QString& message) {
    rateRefreshing_ = false;
    rateError_ = ok || !manual ? QString() : task_card::fmt(strings_, QStringLiteral("rates.failed"), {message});
    emit ratesChanged();
}

QString NetworkService::rateLine() const {
    if (prefs_.currency() == QLatin1String("USD")) return {};
    if (rateRefreshing_) return strings_.t(QStringLiteral("rates.refreshing"));
    if (!rateError_.isEmpty()) return rateError_;
    auto line = catalog_.rateStatus(prefs_.currency());
    if (rates_ && QDateTime::currentMSecsSinceEpoch() - rates_->dateMs > 7 * kDayMs) line += QStringLiteral(" · ") + strings_.t(QStringLiteral("rates.stale"));
    return line;
}

bool NetworkService::rateAttribution() const { return rates_ && rates_->provider == QLatin1String("open.er-api.com"); }

// ---- Updates (UPDATES)

void NetworkService::checkUpdates() { runUpdateCheck(true); }

void NetworkService::runUpdateCheck(bool manual) {
    if (!prefs_.networkEnabled() || updateState_ == QLatin1String("checking") || updateState_ == QLatin1String("downloading")) return;
    if (!manual && (!prefs_.autoCheckUpdates() || QCoreApplication::applicationVersion() == QLatin1String("0.0.0"))) return;
    updateState_ = QStringLiteral("checking");
    emit updateChanged();
    fetch(QUrl(QStringLiteral("https://api.github.com/repos/zoefix/agent-speed/releases/latest")), "application/vnd.github+json", kReleaseCap,
          kUpdateHosts, {}, [this, manual](const Fetched& f) {
              if (!f.error.isEmpty()) {
                  updateState_ = manual ? QStringLiteral("failed") : QStringLiteral("idle");
                  updateError_ = strings_.t(QStringLiteral("update.failed.network"));
                  emit updateChanged();
                  return;
              }
              QSettings().setValue(QStringLiteral("update/lastCheckMs"), QDateTime::currentMSecsSinceEpoch());
              const auto o = QJsonDocument::fromJson(f.body).object();
              if (o.value(QStringLiteral("draft")).toBool() || o.value(QStringLiteral("prerelease")).toBool()) {
                  updateState_ = QStringLiteral("upToDate");
                  emit updateChanged();
                  return;
              }
              Release r;
              r.tag = o.value(QStringLiteral("tag_name")).toString();
              r.version = r.tag.startsWith(QLatin1Char('v')) ? r.tag.mid(1) : r.tag;
              r.notes = o.value(QStringLiteral("body")).toString();
              r.page = o.value(QStringLiteral("html_url")).toString();
              const auto arch = QSysInfo::currentCpuArchitecture() == QLatin1String("arm64") ? QStringLiteral("aarch64") : QStringLiteral("x86_64");
              r.assetName = QStringLiteral("AgentSpeed-Linux-%1.AppImage").arg(arch);
              for (const auto& a : o.value(QStringLiteral("assets")).toArray()) {
                  const auto asset = a.toObject();
                  const auto name = asset.value(QStringLiteral("name")).toString();
                  if (name == r.assetName) {
                      r.assetUrl = asset.value(QStringLiteral("browser_download_url")).toString();
                      r.assetSize = static_cast<qint64>(asset.value(QStringLiteral("size")).toDouble());
                  } else if (name == QLatin1String("SHA256SUMS.txt")) {
                      r.sumsUrl = asset.value(QStringLiteral("browser_download_url")).toString();
                  }
              }
              const auto current = parseVersion(QCoreApplication::applicationVersion());
              const auto offered = parseVersion(r.tag);
              const bool skipped = !manual && QSettings().value(QStringLiteral("update/skipped")).toString() == r.tag;
              if (current && offered && *current < *offered && !skipped) {
                  const bool fresh = release_.tag != r.tag;
                  release_ = r;
                  updateState_ = QStringLiteral("available");
                  emit updateChanged();
                  if (fresh && !manual) emit updateFound(r.version);
              } else {
                  updateState_ = QStringLiteral("upToDate");
                  emit updateChanged();
              }
          });
}

bool NetworkService::canInstall() const {
    return !qEnvironmentVariable("APPIMAGE").isEmpty() && !release_.assetUrl.isEmpty() && !release_.sumsUrl.isEmpty();
}

void NetworkService::skipUpdate() {
    QSettings().setValue(QStringLiteral("update/skipped"), release_.tag);
    updateState_ = QStringLiteral("idle");
    emit updateChanged();
}

void NetworkService::openRelease() {
    QDesktopServices::openUrl(QUrl(release_.page.isEmpty() ? QStringLiteral("https://github.com/zoefix/agent-speed/releases") : release_.page));
}

void NetworkService::download(const QUrl& url, const QString& path, qint64 cap, std::function<void(bool, const QString&)> done) {
    QNetworkRequest request(url);
    request.setTransferTimeout(60'000);
    request.setAttribute(QNetworkRequest::CacheLoadControlAttribute, QNetworkRequest::AlwaysNetwork);
    request.setRawHeader("User-Agent", userAgent());
    request.setRawHeader("Accept", "application/octet-stream");
    auto* file = new QFile(path, this);
    if (!file->open(QIODevice::WriteOnly)) {
        delete file;
        done(false, strings_.t(QStringLiteral("update.failed.permission")));
        return;
    }
    auto* reply = nam_.get(request);
    connect(reply, &QNetworkReply::redirected, reply, [reply](const QUrl& to) {
        if (to.scheme() == QLatin1String("https") && kUpdateHosts.contains(to.host())) emit reply->redirectAllowed();
        else reply->abort();
    });
    connect(reply, &QNetworkReply::readyRead, reply, [reply, file, cap] {
        file->write(reply->readAll());
        if (file->size() > cap) reply->abort();
    });
    connect(reply, &QNetworkReply::downloadProgress, this, [this](qint64 received, qint64 total) {
        if (total > 0) {
            progress_ = static_cast<double>(received) / static_cast<double>(total);
            emit updateChanged();
        }
    });
    connect(reply, &QNetworkReply::finished, this, [this, reply, file, done] {
        file->write(reply->readAll());
        file->close();
        const bool ok = reply->error() == QNetworkReply::NoError;
        reply->deleteLater();
        file->deleteLater();
        done(ok, ok ? QString() : strings_.t(QStringLiteral("update.failed.network")));
    });
}

void NetworkService::failUpdate(const QString& reason) {
    updateDir_.reset();
    updateError_ = reason;
    updateState_ = QStringLiteral("failed");
    emit updateChanged();
}

// UPDATES 5 on Linux: an AppImage replaces itself — download with SHA256SUMS.txt into a private folder, verify the
// checksum and size, stage next to the running file, rename over it and start the new one.
void NetworkService::installUpdate() {
    if (!prefs_.networkEnabled()) return;
    if (!canInstall()) {
        openRelease();
        return;
    }
    updateDir_ = std::make_unique<QTemporaryDir>();
    if (!updateDir_->isValid()) return failUpdate(strings_.t(QStringLiteral("update.failed.permission")));
    updateState_ = QStringLiteral("downloading");
    progress_ = 0;
    emit updateChanged();
    const auto sums = updateDir_->filePath(QStringLiteral("SHA256SUMS.txt"));
    const auto asset = updateDir_->filePath(release_.assetName);
    download(QUrl(release_.sumsUrl), sums, kReleaseCap, [this, sums, asset](bool ok, const QString& error) {
        if (!ok) return failUpdate(error);
        download(QUrl(release_.assetUrl), asset, kAssetCap, [this, sums, asset](bool ok2, const QString& error2) {
            if (!ok2) return failUpdate(error2);
            updateState_ = QStringLiteral("verifying");
            emit updateChanged();
            QFile sumsFile(sums), assetFile(asset);
            if (!sumsFile.open(QIODevice::ReadOnly) || !assetFile.open(QIODevice::ReadOnly)) return failUpdate(strings_.t(QStringLiteral("update.failed.invalid")));
            QString expected;
            for (const auto& line : QString::fromUtf8(sumsFile.readAll()).split(QLatin1Char('\n'))) {
                const auto parts = line.simplified().split(QLatin1Char(' '));
                if (parts.size() == 2 && parts[1] == release_.assetName) expected = parts[0].toLower();
            }
            QCryptographicHash hash(QCryptographicHash::Sha256);
            hash.addData(&assetFile);
            if (expected.isEmpty() || QString::fromLatin1(hash.result().toHex()) != expected) return failUpdate(strings_.t(QStringLiteral("update.failed.checksum")));
            if (release_.assetSize > 0 && assetFile.size() != release_.assetSize) return failUpdate(strings_.t(QStringLiteral("update.failed.invalid")));
            assetFile.close();
            updateState_ = QStringLiteral("installing");
            emit updateChanged();
            const auto target = qEnvironmentVariable("APPIMAGE");
            const auto staged = QFileInfo(target).absolutePath() + QStringLiteral("/.AgentSpeed-update.AppImage");
            QFile::remove(staged);
            if (!QFile::copy(asset, staged)) return failUpdate(strings_.t(QStringLiteral("update.failed.permission")));
            ::chmod(staged.toLocal8Bit().constData(), 0755);
            if (::rename(staged.toLocal8Bit().constData(), target.toLocal8Bit().constData()) != 0) {
                QFile::remove(staged);
                return failUpdate(strings_.t(QStringLiteral("update.failed.permission")));
            }
            updateDir_.reset();
            QProcess::startDetached(target, {QStringLiteral("--after-update"), QCoreApplication::applicationVersion()});
            QCoreApplication::quit();
        });
    });
}

QString NetworkService::updateLine() const {
    if (!prefs_.networkEnabled()) return strings_.t(QStringLiteral("settings.network.offHint"));
    if (updateState_ == QLatin1String("checking")) return strings_.t(QStringLiteral("update.checking"));
    if (updateState_ == QLatin1String("available")) return task_card::fmt(strings_, QStringLiteral("update.available.short"), {release_.version});
    if (updateState_ == QLatin1String("failed")) return task_card::fmt(strings_, QStringLiteral("update.failed"), {updateError_});
    if (updateState_ == QLatin1String("downloading")) {
        return task_card::fmt(strings_, QStringLiteral("update.downloading"), {QString::number(qRound(progress_ * 100)) + QLatin1Char('%')});
    }
    if (updateState_ == QLatin1String("verifying")) return strings_.t(QStringLiteral("update.verifying"));
    if (updateState_ == QLatin1String("installing")) return strings_.t(QStringLiteral("update.installing"));
    const auto last = QSettings().value(QStringLiteral("update/lastCheckMs"), 0).toLongLong();
    if (updateState_ == QLatin1String("upToDate")) return strings_.t(QStringLiteral("update.upToDate"));
    return last > 0 ? task_card::fmt(strings_, QStringLiteral("update.lastChecked"), {shortDate(last)}) : QString();
}
