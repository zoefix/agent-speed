#include "history_service.hpp"

#include <QClipboard>
#include <QDate>
#include <QDateTime>
#include <QElapsedTimer>
#include <QFile>
#include <QFileDialog>
#include <QGuiApplication>
#include <QStandardPaths>
#include <QTimer>
#include <QTimeZone>
#include <algorithm>
#include <cmath>
#include <sys/utsname.h>
#include <tuple>

#include "agentspeed/money.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/remote_protocol.hpp"
#include "agentspeed/tz.hpp"
#include "format.hpp"
#include "perf_card.hpp"
#include "strings.hpp"

namespace {

constexpr int kIntervalMs = 10 * 60 * 1000;

QString csvField(const QString& value) {
    QString text = value;
    if (!text.isEmpty() && QStringLiteral("=+-@\t\r").contains(text.front())) text.prepend(QLatin1Char('\''));
    if (text.contains(QLatin1Char(',')) || text.contains(QLatin1Char('"')) || text.contains(QLatin1Char('\n')) ||
        text.contains(QLatin1Char('\r'))) {
        text.replace(QStringLiteral("\""), QStringLiteral("\"\""));
        text = QLatin1Char('"') + text + QLatin1Char('"');
    }
    return text;
}

QString oneDecimal(double value) { return QString::number(value, 'f', 1); }

std::optional<agentspeed::SourceKind> sourceOf(const QString& id) {
    if (id.isEmpty() || id == QLatin1String("all")) return std::nullopt;
    return agentspeed::sourceFromId(id.toStdString());
}

// `host` empty: every host (DESIGN 13: source and host filters combine).
bool inView(const agentspeed::history::DailyUsage& day, const std::string& first, const std::string& last,
            std::optional<agentspeed::SourceKind> source, const std::string& host = {}) {
    return day.key.day >= first && day.key.day <= last && (!source || day.key.source == *source) && (host.empty() || day.host == host);
}

// METRICS 9.10 Reading of the remote hosts' days: a relay's day is one merged contribution of the current analysis.
std::map<std::string, std::vector<agentspeed::unpriced::Part>> remoteParts(const std::vector<agentspeed::history::DailyUsage>& days) {
    std::map<std::string, std::vector<agentspeed::unpriced::Part>> byHost;
    for (const auto& day : days) {
        if (day.host == agentspeed::remote::kLocalHost) continue;
        agentspeed::unpriced::Stored stored;
        stored.fileVersion = agentspeed::history::Analyzer::kVersion;
        stored.requests = day.totals.requests;
        stored.unpriced = day.totals.unpricedRequests;
        for (const auto& [model, totals] : day.totals.models) {
            if (totals.unpricedRequests && *totals.unpricedRequests > 0) stored.models[model] = *totals.unpricedRequests;
        }
        for (const auto& [fact, n] : day.totals.unpriced) {
            stored.side.push_back({fact.model, fact.provider, fact.fast, n, agentspeed::history::Analyzer::kVersion});
        }
        auto part = agentspeed::unpriced::readPart(stored);
        part.day = day.key.day;
        part.source = day.key.source;
        byHost[day.host].push_back(std::move(part));
    }
    for (auto& [host, parts] : byHost) parts = agentspeed::unpriced::mergeParts(parts);
    return byHost;
}

// The days of every host summed per (day, source), or one host's (the mode CSV keeps one row per day and source).
std::vector<agentspeed::history::DailyUsage> modeDays(const std::vector<agentspeed::history::DailyUsage>& days, const std::string& first,
                                                      const std::string& last, std::optional<agentspeed::SourceKind> source,
                                                      const std::string& host) {
    std::map<agentspeed::history::DayKey, agentspeed::history::DailyUsage> merged;
    for (const auto& day : days) {
        if (!inView(day, first, last, source, host)) continue;
        auto [it, inserted] = merged.try_emplace(day.key, day);
        if (!inserted) it->second.totals.merge(day.totals);
    }
    std::vector<agentspeed::history::DailyUsage> out;
    for (auto& [key, day] : merged) out.push_back(std::move(day));
    return out;
}

// The app line of the copied diagnostics (METRICS 9.10 Text) names Linux and its kernel's major version.
agentspeed::unpriced::App appLine() {
    utsname name{};
    const int major = uname(&name) == 0 ? std::clamp(std::atoi(name.release), 1, 99) : 1;
    return {AGENTSPEED_VERSION, "linux", "linux-" + std::to_string(major)};
}

}  // namespace

// The remote rows remember the zone they were computed in (REMOTE 8.3): the id the connections send in `start.zone`.
HistoryWorker::HistoryWorker(std::shared_ptr<agentspeed::pricing::PriceBook> prices, QString storePath)
    : prices_(std::move(prices)), storePath_(std::move(storePath)),
      ledger_({}, agentspeed::remote::HistorySyncState::kFlushBytes,
               [] { return agentspeed::remote::currentZone(QString::fromUtf8(QTimeZone::systemTimeZoneId()).toStdString()).id; }) {}

HistoryWorker::~HistoryWorker() = default;

void HistoryWorker::setScope(agentspeed::SourceLocations locations, std::set<agentspeed::SourceKind> enabled) {
    std::lock_guard lock(scopeMutex_);
    locations_ = std::move(locations);
    enabled_ = std::move(enabled);
}

void HistoryWorker::start() {
    try {
        store_ = agentspeed::history::Store::open(storePath_.toStdString());
        analyzer_ = std::make_unique<agentspeed::history::Analyzer>(*store_, prices_);
    } catch (const std::exception& error) {
        qWarning("history store unavailable: %s", error.what());
        return;
    }
    // REMOTE 8.3: the hosts' side tables are created on open, without a schema change.
    try {
        store_->ensureRemoteTables();
        ledger_.load(*store_);
        remoteReady_ = true;
    } catch (const std::exception&) {
        qWarning("remote history unavailable");
    }
    publish();
    try {
        const auto today = QDate::currentDate().toString(Qt::ISODate).toStdString();
        TrafficRows saved;
        for (const auto& r : store_->trafficRows()) {
            if (r.day == today) saved.push_back(r);
        }
        emit trafficRestored(std::move(saved));
    } catch (const std::exception&) {
    }
    timer_ = new QTimer(this);
    timer_->setInterval(60 * 1000);
    connect(timer_, &QTimer::timeout, this, [this] {
        // Every 10 minutes, and at once when the day changed (METRICS 8).
        const auto now = QDateTime::currentDateTime();
        const auto dayStart = QDateTime(now.date(), QTime(0, 0)).toMSecsSinceEpoch();
        if (dayStart != lastDayStart_ || now.toMSecsSinceEpoch() - lastRunMs_ >= kIntervalMs) analyze();
    });
    timer_->start();
    QTimer::singleShot(4000, this, &HistoryWorker::analyze);
}

void HistoryWorker::analyze() {
    if (!analyzer_) return;
    agentspeed::SourceLocations locations;
    std::set<agentspeed::SourceKind> enabled;
    {
        std::lock_guard lock(scopeMutex_);
        locations = locations_;
        enabled = enabled_;
    }
    const auto now = QDateTime::currentDateTime();
    lastDayStart_ = QDateTime(now.date(), QTime(0, 0)).toMSecsSinceEpoch();
    lastRunMs_ = now.toMSecsSinceEpoch();
    cancelled_ = false;
    emit analyzing(true);
    try {
        const auto environment = agentspeed::processEnvironment();
        const auto zoneId = QString::fromUtf8(QTimeZone::systemTimeZoneId()).toStdString();
        QElapsedTimer sinceProgress;
        sinceProgress.start();
        const auto onProgress = [&](int done, int total) {
            if (done != 0 && done != total && sinceProgress.elapsed() < 150) return;
            sinceProgress.restart();
            emit progress(done, total);
        };
        analyzer_->run(locations, enabled, agentspeed::localZone(environment), zoneId, [this] { return cancelled_.load(); }, onProgress);
    } catch (const std::exception& error) {
        qWarning("history analysis failed: %s", error.what());
    }
    emit analyzing(false);
    publish();
}

void HistoryWorker::clear() {
    if (!analyzer_) return;
    analyzer_->clear();
    ledger_.reset();
    publish();
    publishTooLarge();
    // The hosts' rows went too: every connected host is asked for all of its days (REMOTE 8.3).
    if (remoteReady_) {
        for (const auto& host : ledger_.connectedHosts()) requestRemote(host, {});
    }
    analyze();
}

void HistoryWorker::receiveRemote(agentspeed::remote::HostEvent event) {
    QMetaObject::invokeMethod(
        this, [this, event = std::make_shared<const agentspeed::remote::HostEvent>(std::move(event))] { applyRemote(*event); },
        Qt::QueuedConnection);
}

void HistoryWorker::setRemoteHosts(std::map<std::string, std::string> names) {
    QMetaObject::invokeMethod(
        this,
        [this, names = std::move(names)]() mutable {
            applyEffect(ledger_.setHosts(std::move(names), remoteReady_ ? store_.get() : nullptr));
        },
        Qt::QueuedConnection);
}

void HistoryWorker::deleteRemoteHost(std::string id) {
    QMetaObject::invokeMethod(
        this,
        [this, id = std::move(id)] {
            if (remoteReady_) applyEffect(ledger_.deleteHost(id, *store_));
        },
        Qt::QueuedConnection);
}

void HistoryWorker::setRemoteRequester(RemoteHistoryRequester requester) {
    std::lock_guard lock(requesterMutex_);
    requester_ = std::move(requester);
}

void HistoryWorker::applyRemote(const agentspeed::remote::HostEvent& event) {
    if (!remoteReady_) return;
    applyEffect(ledger_.receive(event, *store_));
}

void HistoryWorker::applyEffect(const agentspeed::remote::HistoryEffect& effect) {
    using Kind = agentspeed::remote::HistoryEffect::Kind;
    switch (effect.kind) {
        case Kind::None: break;
        case Kind::Changed:
            publish();
            publishTooLarge();
            break;
        case Kind::TooLargeChanged: publishTooLarge(); break;
        case Kind::Request: requestRemote(effect.host, effect.digests); break;
    }
}

void HistoryWorker::requestRemote(const std::string& host, const std::map<std::string, std::string>& digests) {
    RemoteHistoryRequester requester;
    {
        std::lock_guard lock(requesterMutex_);
        requester = requester_;
    }
    if (requester) requester(host, digests);
}

void HistoryWorker::publishTooLarge() {
    QStringList hosts;
    for (const auto& host : ledger_.tooLarge()) hosts.push_back(QString::fromStdString(host));
    emit remoteTooLarge(hosts);
}

void HistoryWorker::recordPeak(const QString& day, double peak) {
    if (analyzer_ && analyzer_->recordPeaks({{day.toStdString(), peak}})) publish();
}

void HistoryWorker::saveTraffic(TrafficRows rows) {
    if (!store_) return;
    try {
        store_->saveTraffic(rows);
        publishTraffic();
    } catch (const std::exception& error) {
        qWarning("traffic history not saved: %s", error.what());
    }
}

void HistoryWorker::publishTraffic() {
    QVariantList rows;
    for (const auto& r : store_->trafficRows()) {
        QVariantMap row;
        row.insert(QStringLiteral("date"), QString::fromStdString(r.day));
        row.insert(QStringLiteral("key"), QString::fromStdString(r.key));
        row.insert(QStringLiteral("rx"), static_cast<qint64>(r.rx));
        row.insert(QStringLiteral("tx"), static_cast<qint64>(r.tx));
        row.insert(QStringLiteral("rxTools"), static_cast<qint64>(r.rxTools));
        row.insert(QStringLiteral("txTools"), static_cast<qint64>(r.txTools));
        row.insert(QStringLiteral("sampledMs"), static_cast<qint64>(r.sampledMs));
        rows.push_back(row);
    }
    emit trafficReady(rows, QString::fromStdString(store_->meta("trafficSince").value_or("")));
}

void HistoryWorker::publish() {
    QVariantList rows;
    auto details = std::make_shared<HistoryDetail>();
    details->days = analyzer_->days();
    details->unpriced = analyzer_->unpricedParts();
    // REMOTE 8.3: every host's rows join the local analysis, each with the day's live peak.
    const auto& remoteRows = ledger_.rows();
    if (!remoteRows.empty()) {
        const auto peaks = store_->peaks();
        const auto peakOf = [&peaks](const std::string& day) -> std::optional<double> {
            const auto it = peaks.find(day);
            return it == peaks.end() ? std::nullopt : std::optional<double>(it->second);
        };
        details->days = agentspeed::remote::mergeDays(std::move(details->days), remoteRows.usage(nullptr, peakOf));
        details->hostUnpriced = remoteParts(details->days);
        auto all = details->unpriced;
        for (const auto& [host, parts] : details->hostUnpriced) all.insert(all.end(), parts.begin(), parts.end());
        details->hostUnpriced[std::string(agentspeed::remote::kLocalHost)] = std::move(details->unpriced);
        details->unpriced = agentspeed::unpriced::mergeParts(all);
    }
    const auto nameOf = [&remoteRows](const std::string& host) {
        if (host == agentspeed::remote::kLocalHost) return QString();
        const auto it = remoteRows.names().find(host);
        return QString::fromStdString(it == remoteRows.names().end() ? host : it->second);
    };
    for (const auto& day : details->days) {
        const auto& t = day.totals;
        QVariantMap row;
        row.insert(QStringLiteral("date"), QString::fromStdString(day.key.day));
        row.insert(QStringLiteral("source"), QString::fromUtf8(agentspeed::sourceId(day.key.source)));
        row.insert(QStringLiteral("host"), QString::fromStdString(day.host));
        row.insert(QStringLiteral("hostName"), nameOf(day.host));
        row.insert(QStringLiteral("requests"), static_cast<qint64>(t.requests));
        row.insert(QStringLiteral("input"), static_cast<qint64>(t.input));
        row.insert(QStringLiteral("cachedInput"), static_cast<qint64>(t.cachedInput));
        row.insert(QStringLiteral("cacheWriteInput"), static_cast<qint64>(t.cacheWriteInput));
        row.insert(QStringLiteral("output"), static_cast<qint64>(t.output));
        row.insert(QStringLiteral("reasoning"), static_cast<qint64>(t.reasoning));
        row.insert(QStringLiteral("measuredOutput"), static_cast<qint64>(t.measuredOutput));
        row.insert(QStringLiteral("measuredMs"), static_cast<qint64>(t.measuredMs));
        row.insert(QStringLiteral("unfinalized"), static_cast<qint64>(t.unfinalized));
        row.insert(QStringLiteral("agents"), t.agents);
        row.insert(QStringLiteral("cost"), t.costMicroUsd ? QVariant(static_cast<qint64>(*t.costMicroUsd)) : QVariant());
        row.insert(QStringLiteral("unpriced"), t.unpricedRequests ? QVariant(static_cast<qint64>(*t.unpricedRequests)) : QVariant());
        row.insert(QStringLiteral("peak"), day.peak ? QVariant(*day.peak) : QVariant());
        QVariantList models;
        for (const auto& [name, m] : t.models) {
            QVariantMap model;
            model.insert(QStringLiteral("name"), QString::fromStdString(name));
            model.insert(QStringLiteral("requests"), static_cast<qint64>(m.requests));
            model.insert(QStringLiteral("output"), static_cast<qint64>(m.output));
            model.insert(QStringLiteral("measuredOutput"), static_cast<qint64>(m.measuredOutput));
            model.insert(QStringLiteral("measuredMs"), static_cast<qint64>(m.measuredMs));
            model.insert(QStringLiteral("cost"), m.costMicroUsd ? QVariant(static_cast<qint64>(*m.costMicroUsd)) : QVariant());
            models.push_back(model);
        }
        row.insert(QStringLiteral("models"), models);
        rows.push_back(row);
    }
    QVariantList hosts;
    for (const auto& host : remoteRows.hosts()) hosts.push_back(QVariantMap{{QStringLiteral("id"), QString::fromStdString(host)}, {QStringLiteral("name"), nameOf(host)}});
    std::stable_sort(hosts.begin(), hosts.end(), [](const QVariant& a, const QVariant& b) {
        const auto x = a.toMap(), y = b.toMap();
        const int order = x.value(QStringLiteral("name")).toString().compare(y.value(QStringLiteral("name")).toString(), Qt::CaseInsensitive);
        return order != 0 ? order < 0 : x.value(QStringLiteral("id")).toString() < y.value(QStringLiteral("id")).toString();
    });
    emit hostsReady(hosts);
    emit daysReady(rows, QDateTime::currentMSecsSinceEpoch());
    emit detailsReady(std::move(details));
    // METRICS 6.11 Reading: a day's record of a (source, model) is the sum over files; every host's records are pooled
    // (the baseline compares a model with itself across the user's machines, REMOTE 8.3).
    std::map<std::tuple<std::string, agentspeed::SourceKind, std::string>, agentspeed::perf::Record> sums;
    for (const auto& r : analyzer_->perfRecords()) sums[{r.day, r.source, r.model}].add(r.record);
    std::vector<agentspeed::perf::DayRecord> local;
    for (const auto& [key, record] : sums) local.push_back({std::get<0>(key), std::get<1>(key), std::get<2>(key), record});
    auto records = std::make_shared<std::vector<agentspeed::perf::DayRecord>>(agentspeed::remote::mergePerf(local, remoteRows.perfRecords()));
    const auto today = QDate::currentDate().toString(Qt::ISODate).toStdString();
    PerfBaselines baselines;
    for (const auto& r : *records) {
        const auto key = std::make_pair(r.source, r.model);
        if (baselines.contains(key)) continue;
        if (const auto value = agentspeed::perf::baseline(*records, r.source, r.model, today)) baselines[key] = *value;
    }
    emit perfReady(std::move(records), std::move(baselines));
    publishTraffic();
}

void HistoryModel::applyRows(const QVariantList& rows, qint64 updatedMs) {
    rows_ = rows;
    updatedMs_ = updatedMs;
    emit rowsChanged();
}

void HistoryModel::applyHosts(const QVariantList& hosts) {
    if (hosts == hosts_) return;
    hosts_ = hosts;
    emit hostsChanged();
}

void HistoryModel::applyTooLarge(const QStringList& hosts) {
    if (hosts == tooLargeHosts_) return;
    tooLargeHosts_ = hosts;
    emit tooLargeHostsChanged();
}

void HistoryModel::setAnalyzing(bool running) {
    if (running == analyzing_) return;
    analyzing_ = running;
    emit analyzingChanged();
}

QString HistoryModel::money(qint64 microUsd, const QString& currency, const QString& rate, const QString& language) const {
    using namespace agentspeed::money;
    const auto& c = CurrencyTable::bundled().currencyOrUsd(currency.toStdString());
    const auto r = Decimal::parse(rate.toStdString()).value_or(Decimal(1));
    return QString::fromStdString(agentspeed::money::format(amount(microUsd, r), c, language.toStdString(), Form::Amount).text);
}

QString HistoryModel::chooseCsvPath(const QString& suggestedName) const {
    const auto folder = QStandardPaths::writableLocation(QStandardPaths::DocumentsLocation);
    return QFileDialog::getSaveFileName(nullptr, QString(), folder + QLatin1Char('/') + suggestedName, QStringLiteral("CSV (*.csv)"));
}

bool HistoryModel::exportCsv(const QString& path, const QVariantList& rows, const QString& sourceCode, const QString& currency,
                             const QString& rate, const QString& host, bool localTraffic) const {
    // A JS null (no peak, no cost) is no value.
    const auto present = [](const QVariant& v) { return v.isValid() && !v.isNull(); };
    if (path.isEmpty()) return false;
    QFile file(path);
    if (!file.open(QIODevice::WriteOnly | QIODevice::Truncate)) return false;
    const auto& table = agentspeed::money::CurrencyTable::bundled();
    const auto& c = table.currencyOrUsd(currency.toStdString());
    const auto r = agentspeed::money::Decimal::parse(rate.toStdString()).value_or(agentspeed::money::Decimal(1));
    QStringList header{QStringLiteral("date"), QStringLiteral("source"), QStringLiteral("requests"), QStringLiteral("input_tokens"),
                       QStringLiteral("cached_input_tokens"), QStringLiteral("cache_write_input_tokens"), QStringLiteral("output_tokens"),
                       QStringLiteral("reasoning_tokens"), QStringLiteral("cache_hit_percent"), QStringLiteral("measured_output_tokens"),
                       QStringLiteral("measured_ms"), QStringLiteral("avg_speed_tok_s"), QStringLiteral("peak_tok_s"), QStringLiteral("agents"),
                       QStringLiteral("unfinalized"), QStringLiteral("models"), QStringLiteral("cost_usd"),
                       QStringLiteral("cost_") + currency.toLower(), QStringLiteral("unpriced_requests"),
                       QStringLiteral("traffic_rx_bytes"), QStringLiteral("traffic_tx_bytes"), QStringLiteral("traffic_rx_tools_bytes"),
                       QStringLiteral("traffic_tx_tools_bytes")};
    // DESIGN 13: the trailing `host` column while host history exists.
    if (!host.isEmpty()) header << QStringLiteral("host");
    const int trafficEnd = static_cast<int>(header.size()) - (host.isEmpty() ? 0 : 1);
    QByteArray out("\xEF\xBB\xBF");
    out += header.join(QLatin1Char(',')).toUtf8() + "\r\n";
    for (const auto& value : rows) {
        const auto row = value.toMap();
        const auto input = row.value(QStringLiteral("input")).toLongLong();
        const auto cached = row.value(QStringLiteral("cachedInput")).toLongLong();
        const auto measuredMs = row.value(QStringLiteral("measuredMs")).toLongLong();
        const auto measuredOutput = row.value(QStringLiteral("measuredOutput")).toLongLong();
        QStringList models;
        for (const auto& m : row.value(QStringLiteral("models")).toList()) {
            const auto name = m.toMap().value(QStringLiteral("name")).toString();
            if (!name.isEmpty()) models.push_back(name);
        }
        const auto cost = row.value(QStringLiteral("cost"));
        QStringList fields{
            row.value(QStringLiteral("date")).toString(),
            sourceCode,
            QString::number(row.value(QStringLiteral("requests")).toLongLong()),
            QString::number(input),
            QString::number(cached),
            QString::number(row.value(QStringLiteral("cacheWriteInput")).toLongLong()),
            QString::number(row.value(QStringLiteral("output")).toLongLong()),
            QString::number(row.value(QStringLiteral("reasoning")).toLongLong()),
            input > 0 ? oneDecimal(100.0 * static_cast<double>(cached) / static_cast<double>(input)) : QString(),
            QString::number(measuredOutput),
            QString::number(measuredMs),
            measuredMs > 0 ? oneDecimal(static_cast<double>(measuredOutput) * 1000.0 / static_cast<double>(measuredMs)) : QString(),
            present(row.value(QStringLiteral("peak"))) ? oneDecimal(row.value(QStringLiteral("peak")).toDouble()) : QString(),
            QString::number(row.value(QStringLiteral("agents")).toInt()),
            QString::number(row.value(QStringLiteral("unfinalized")).toLongLong()),
            models.join(QStringLiteral("; ")),
            present(cost) ? QString::fromStdString(agentspeed::money::csvUsd(cost.toLongLong())) : QString(),
            present(cost) ? QString::fromStdString(agentspeed::money::csv(cost.toLongLong(), r, c.digits)) : QString(),
            present(row.value(QStringLiteral("unpriced"))) ? QString::number(row.value(QStringLiteral("unpriced")).toLongLong()) : QString(),
        };
        // DESIGN 10 CSV: the day's traffic of the row's source(s), empty before measuring began; then this computer's row.
        const auto date = row.value(QStringLiteral("date")).toString();
        const auto t = trafficSums(date, sourceCode == QLatin1String("all") ? QString() : row.value(QStringLiteral("source")).toString());
        // A remote host has no agent traffic (REMOTE 7): its rows leave the traffic empty and no machine row follows.
        const bool recorded = localTraffic && !trafficSince_.isEmpty() && date >= trafficSince_;
        for (const auto v : {t.rx, t.tx, t.rxTools, t.txTools}) fields << (recorded ? QString::number(v) : QString());
        if (!host.isEmpty()) fields << host;
        for (auto& field : fields) field = csvField(field);
        out += fields.join(QLatin1Char(',')).toUtf8() + "\r\n";
        if (recorded) {
            const auto m = trafficSums(date, QStringLiteral("machine"));
            QStringList machine(header.size());
            machine[0] = date;
            machine[1] = QStringLiteral("machine");
            machine[trafficEnd - 4] = QString::number(m.rx);
            machine[trafficEnd - 3] = QString::number(m.tx);
            if (!host.isEmpty()) machine[trafficEnd] = QString::fromUtf8(agentspeed::remote::kLocalHost.data(), agentspeed::remote::kLocalHost.size());
            out += machine.join(QLatin1Char(',')).toUtf8() + "\r\n";
        }
    }
    return file.write(out) == out.size();
}

HistoryModel::TrafficSums HistoryModel::trafficSums(const QString& date, const QString& source) const {
    TrafficSums sums;
    for (const auto& value : trafficRows_) {
        const auto row = value.toMap();
        if (row.value(QStringLiteral("date")).toString() != date) continue;
        const auto key = row.value(QStringLiteral("key")).toString();
        const bool wanted = source.isEmpty() ? key != QLatin1String("machine") && !disabled_.contains(key) : key == source;
        if (!wanted) continue;
        sums.rx += row.value(QStringLiteral("rx")).toLongLong();
        sums.tx += row.value(QStringLiteral("tx")).toLongLong();
        sums.rxTools += row.value(QStringLiteral("rxTools")).toLongLong();
        sums.txTools += row.value(QStringLiteral("txTools")).toLongLong();
    }
    return sums;
}

QString HistoryModel::bytes(double value) const { return QString::fromStdString(agentspeed::traffic::formatTotal(value)); }

QVariantMap HistoryModel::trafficOf(const QString& date, const QString& source, bool includeTools) const {
    QVariantMap out;
    const bool recorded = !trafficSince_.isEmpty() && date >= trafficSince_;
    out.insert(QStringLiteral("recorded"), recorded);
    if (!recorded) return out;
    const auto s = trafficSums(date, source == QLatin1String("all") ? QString() : source);
    const auto rx = s.rx + (includeTools ? s.rxTools : 0), tx = s.tx + (includeTools ? s.txTools : 0);
    out.insert(QStringLiteral("rx"), rx);
    out.insert(QStringLiteral("tx"), tx);
    out.insert(QStringLiteral("rxText"), QString::fromStdString(agentspeed::traffic::formatTotal(static_cast<double>(rx))));
    out.insert(QStringLiteral("txText"), QString::fromStdString(agentspeed::traffic::formatTotal(static_cast<double>(tx))));
    return out;
}

void HistoryModel::applyTraffic(const QVariantList& rows, const QString& since) {
    trafficRows_ = rows;
    trafficSince_ = since;
    emit trafficChanged();
}

void HistoryModel::applyProgress(int done, int total) {
    filesDone_ = done;
    filesTotal_ = total;
    emit progressChanged();
}

void HistoryModel::applyDetails(HistoryDetails details) {
    details_ = std::move(details);
    ++detailRevision_;
    emit detailsChanged();
}

QVariantMap HistoryModel::modeSection(const QString& first, const QString& last, const QString& source, const QString& kindName,
                                      const QString& host) const {
    using namespace agentspeed;
    QVariantMap out;
    QVariantList rows;
    const auto kind = kindName == QLatin1String("speed") ? ModeKind::Speed : ModeKind::Effort;
    const auto only = sourceOf(source);
    const auto from = first.toStdString(), to = last.toStdString();
    struct Sum {
        history::ModeTotals totals;
        std::map<std::string, std::int64_t> models;
    };
    std::map<std::string, Sum> byMode;
    std::int64_t output = 0;
    if (details_) {
        const auto onHost = host.toStdString();
        for (const auto& day : details_->days) {
            if (!inView(day, from, to, only, onHost)) continue;
            for (const auto& row : history::modeBreakdown(day.totals)) {
                if (row.key.kind != kind) continue;
                auto& sum = byMode[row.key.mode];
                sum.totals.requests += row.totals.requests;
                sum.totals.output += row.totals.output;
                sum.totals.measuredOutput += row.totals.measuredOutput;
                sum.totals.measuredMs += row.totals.measuredMs;
                sum.models[row.key.model] += row.totals.output;
                output += row.totals.output;
            }
        }
    }
    std::vector<std::string> order;
    for (const auto& [mode, sum] : byMode) {
        if (mode != "unknown" && sum.totals.requests + sum.totals.output > 0) order.push_back(mode);
    }
    std::stable_sort(order.begin(), order.end(), [&](const std::string& a, const std::string& b) { return effort_speed::less(kind, a, b); });
    const bool known = !order.empty();
    order.push_back("unknown");
    for (const auto& mode : order) {
        const auto& sum = byMode[mode];
        QVariantMap row;
        row.insert(QStringLiteral("mode"), QString::fromStdString(mode));
        row.insert(QStringLiteral("unknown"), mode == "unknown");
        row.insert(QStringLiteral("requests"), static_cast<qint64>(sum.totals.requests));
        row.insert(QStringLiteral("output"), static_cast<qint64>(sum.totals.output));
        row.insert(QStringLiteral("speed"), sum.totals.measuredMs > 0 ? static_cast<double>(sum.totals.measuredOutput) * 1000.0 / static_cast<double>(sum.totals.measuredMs) : -1.0);
        row.insert(QStringLiteral("share"), output > 0 ? static_cast<double>(sum.totals.output) / static_cast<double>(output) : 0.0);
        const auto level = kind == ModeKind::Effort ? effort_speed::level(mode) : std::nullopt;
        row.insert(QStringLiteral("level"), level ? *level : -1);
        row.insert(QStringLiteral("top"), kind == ModeKind::Effort && effort_speed::isTopRung(mode));
        row.insert(QStringLiteral("bolts"), kind == ModeKind::Speed ? effort_speed::bolts(mode) : 0);
        std::vector<std::pair<std::string, std::int64_t>> models(sum.models.begin(), sum.models.end());
        std::stable_sort(models.begin(), models.end(), [](const auto& a, const auto& b) { return a.second > b.second; });
        QVariantList top;
        for (std::size_t i = 0; i < std::min<std::size_t>(3, models.size()); ++i) {
            if (models[i].second <= 0) break;
            QVariantMap m;
            m.insert(QStringLiteral("name"), QString::fromStdString(models[i].first));
            m.insert(QStringLiteral("share"), sum.totals.output > 0 ? static_cast<double>(models[i].second) / static_cast<double>(sum.totals.output) : 0.0);
            top.push_back(m);
        }
        row.insert(QStringLiteral("models"), top);
        rows.push_back(row);
    }
    out.insert(QStringLiteral("rows"), rows);
    out.insert(QStringLiteral("known"), known);
    return out;
}

bool HistoryModel::exportModeCsv(const QString& path, const QString& first, const QString& last, const QString& source,
                                 const QString& host) const {
    if (path.isEmpty() || !details_) return false;
    const auto merged = modeDays(details_->days, first.toStdString(), last.toStdString(), sourceOf(source), host.toStdString());
    std::vector<agentspeed::history::ModeCsvDay> days;
    for (const auto& day : merged) days.push_back({day.key.day, day.key.source, &day.totals});
    QFile file(path);
    if (!file.open(QIODevice::WriteOnly | QIODevice::Truncate)) return false;
    const auto text = agentspeed::history::modeCsv(days);
    return file.write(text.data(), static_cast<qint64>(text.size())) == static_cast<qint64>(text.size());
}

QVariantMap HistoryModel::unpricedView(const QString& first, const QString& last, const QString& source, const QStringList& excludedIds,
                                       const QString& view, const QString& host) const {
    using namespace agentspeed;
    QVariantMap out;
    QVariantList groups;
    std::int64_t count = 0, pricedNow = 0;
    const auto table = prices_ ? prices_->current() : nullptr;
    if (details_ && table && !first.isEmpty() && !last.isEmpty()) {
        const auto only = sourceOf(source);
        std::set<SourceKind> excluded;
        for (const auto& id : excludedIds) {
            if (const auto kind = sourceFromId(id.toStdString())) excluded.insert(*kind);
        }
        const auto from = first.toStdString(), to = last.toStdString();
        // One host's facts while host history exists; this computer's are the local ones.
        const auto* parts = &details_->unpriced;
        if (!host.isEmpty() && !details_->hostUnpriced.empty()) {
            static const std::vector<unpriced::Part> kNone;
            const auto it = details_->hostUnpriced.find(host.toStdString());
            parts = it == details_->hostUnpriced.end() ? &kNone : &it->second;
        }
        const auto found = unpriced::aggregate(*parts, *table, from, to, only, excluded);
        for (const auto& g : found) {
            count += g.unpriced;
            pricedNow += g.pricedNow;
            if (g.rows.empty()) continue;
            QVariantMap group;
            group.insert(QStringLiteral("source"), QString::fromUtf8(sourceId(g.source)));
            std::int64_t requests = 0, moreRequests = 0;
            QVariantList rows;
            for (std::size_t i = 0; i < g.rows.size(); ++i) {
                const auto& r = g.rows[i];
                requests += r.requests;
                if (i >= 20) {
                    moreRequests += r.requests;
                    continue;
                }
                QVariantMap row;
                row.insert(QStringLiteral("model"), QString::fromStdString(r.model));
                row.insert(QStringLiteral("reason"), QString::fromUtf8(unpriced::reasonId(r.reason)));
                row.insert(QStringLiteral("provider"), r.provider ? QString::fromStdString(*r.provider) : QString());
                row.insert(QStringLiteral("fast"), r.fast);
                row.insert(QStringLiteral("canonical"), r.canonical && *r.canonical != r.model ? QString::fromStdString(*r.canonical) : QString());
                row.insert(QStringLiteral("rule"), r.rule ? QString::fromStdString(*r.rule) : QString());
                row.insert(QStringLiteral("requests"), static_cast<qint64>(r.requests));
                row.insert(QStringLiteral("first"), QString::fromStdString(r.first));
                row.insert(QStringLiteral("last"), QString::fromStdString(r.last));
                rows.push_back(row);
            }
            group.insert(QStringLiteral("requests"), static_cast<qint64>(requests));
            group.insert(QStringLiteral("rows"), rows);
            group.insert(QStringLiteral("more"), static_cast<int>(g.rows.size() > 20 ? g.rows.size() - 20 : 0));
            group.insert(QStringLiteral("moreRequests"), static_cast<qint64>(moreRequests));
            groups.push_back(group);
        }
        out.insert(QStringLiteral("text"),
                   QString::fromStdString(unpriced::render(found, appLine(), *table, view.toStdString(), from, to, only, excluded)));
    }
    out.insert(QStringLiteral("groups"), groups);
    out.insert(QStringLiteral("count"), static_cast<qint64>(count));
    out.insert(QStringLiteral("pricedNow"), static_cast<qint64>(pricedNow));
    return out;
}

bool HistoryModel::copyText(const QString& text) const {
    auto* clipboard = QGuiApplication::clipboard();
    if (!clipboard) return false;
    clipboard->setText(text);
    return clipboard->text() == text;
}

void HistoryModel::applyPerf(PerfRecords records, PerfBaselines baselines) {
    perf_ = std::move(records);
    baselines_ = std::move(baselines);
    ++perfRevision_;
    emit perfChanged();
}

QVariantMap HistoryModel::perfSection(const QString& first, const QString& last, const QString& source) const {
    QVariantMap out;
    QVariantList rows;
    out.insert(QStringLiteral("rows"), rows);
    out.insert(QStringLiteral("ready"), perf_ != nullptr);
    if (!perf_ || !strings_ || first.isEmpty() || last.isEmpty()) return out;
    const auto& s = *strings_;
    const auto only = source.isEmpty() ? std::nullopt : agentspeed::sourceFromId(source.toStdString());
    const auto firstDay = first.toStdString(), lastDay = last.toStdString();
    // A single-day range shows the 14 days ending on that day (DESIGN 12 Trend).
    const bool single = firstDay == lastDay;
    const auto trendFirst = single ? agentspeed::perf::dayMinus(lastDay, 13) : firstDay;
    const auto pooledRows = agentspeed::perf::rangeValues(*perf_, firstDay, lastDay, only);
    const auto trendRows = single ? agentspeed::perf::rangeValues(*perf_, trendFirst, lastDay, only) : pooledRows;
    for (const auto& row : pooledRows) {
        QVariantMap m;
        const auto id = QString::fromUtf8(agentspeed::sourceId(row.source));
        const auto& p = row.pooled;
        m.insert(QStringLiteral("source"), id);
        m.insert(QStringLiteral("sourceName"), s.t(QStringLiteral("source.") + id));
        m.insert(QStringLiteral("model"), QString::fromStdString(row.model));
        m.insert(QStringLiteral("modelName"), row.model.empty() ? s.t(QStringLiteral("cost.noModelName")) : QString::fromStdString(row.model));
        m.insert(QStringLiteral("key"), id + QLatin1Char('\n') + QString::fromStdString(row.model));
        m.insert(QStringLiteral("n"), static_cast<qlonglong>(p.n));
        m.insert(QStringLiteral("nText"), format::perfFull(p.n));
        m.insert(QStringLiteral("score"), p.shown ? static_cast<qlonglong>(*p.shown) : -1);
        m.insert(QStringLiteral("scoreText"), p.shown ? format::perfFull(*p.shown) : QStringLiteral("–"));
        m.insert(QStringLiteral("f"), p.shown ? format::perfScale(static_cast<double>(*p.shown)) : 0.0);
        m.insert(QStringLiteral("sampleHint"), s.fmt(QStringLiteral("history.perf.sample.hint"),
                                                     {static_cast<qlonglong>(p.n), static_cast<qlonglong>(agentspeed::perf::kPooledMinN)}));
        const auto base = baselines_.find({row.source, row.model});
        const bool hasBase = base != baselines_.end();
        m.insert(QStringLiteral("usual"), hasBase ? base->second : QVariant());
        m.insert(QStringLiteral("usualText"), hasBase ? format::perfFull(static_cast<std::int64_t>(std::floor(base->second + 0.5))) : QStringLiteral("–"));
        m.insert(QStringLiteral("usualHint"), hasBase ? s.t(QStringLiteral("history.perf.usual.hint"))
                                                      : s.fmt(QStringLiteral("perf.usual.none.hint"), {static_cast<qlonglong>(agentspeed::perf::kPooledMinN)}));
        QVariantList parts;
        QStringList present;
        for (const auto& c : perf_card::kCardOrder) {
            const auto& comp = p.components[static_cast<std::size_t>(c.id)];
            QVariantMap part;
            part.insert(QStringLiteral("key"), QString::fromLatin1(c.key));
            part.insert(QStringLiteral("column"), s.t(QStringLiteral("history.perf.column.") + c.key));
            part.insert(QStringLiteral("name"), s.t(QStringLiteral("perf.comp.") + c.key));
            part.insert(QStringLiteral("desc"), s.t(QStringLiteral("perf.comp.") + c.key + QStringLiteral(".desc")));
            part.insert(QStringLiteral("present"), comp.present);
            if (comp.present) {
                const auto value = perf_card::measureText(s, c.id, *comp.measure);
                part.insert(QStringLiteral("value"), value);
                part.insert(QStringLiteral("sub"), format::perfFull(static_cast<std::int64_t>(std::floor(*comp.subScore + 0.5))));
                part.insert(QStringLiteral("f"), format::perfScale(*comp.subScore));
                present << s.t(QStringLiteral("history.perf.column.") + c.key) + QLatin1Char(' ') + value;
            } else {
                const bool notRecorded = comp.reason == agentspeed::perf::Reason::NotRecorded;
                part.insert(QStringLiteral("value"), QStringLiteral("–"));
                part.insert(QStringLiteral("sub"), QStringLiteral("–"));
                part.insert(QStringLiteral("f"), 0.0);
                part.insert(QStringLiteral("hint"), notRecorded ? s.fmt(QStringLiteral("perf.reason.notRecorded.hint"), {s.t(QStringLiteral("source.") + id)})
                                                                : s.fmt(QStringLiteral("perf.reason.fewSamples.hint"),
                                                                        {static_cast<qlonglong>(agentspeed::perf::kMinSamples)}));
            }
            parts.push_back(part);
        }
        m.insert(QStringLiteral("parts"), parts);
        m.insert(QStringLiteral("partsText"), present.join(QStringLiteral(" · ")));
        // The daily trend: each day's pooled score, a gap below 20 timed requests.
        QVariantList trend;
        for (const auto& t : trendRows) {
            if (t.source != row.source || t.model != row.model) continue;
            for (const auto& [day, score] : t.trend) {
                std::int64_t n = 0;
                for (const auto& r : *perf_) {
                    if (r.day == day && r.source == row.source && r.model == row.model) n += r.record.n;
                }
                QVariantMap point;
                point.insert(QStringLiteral("day"), QString::fromStdString(day));
                point.insert(QStringLiteral("score"), score ? QVariant(*score) : QVariant());
                const auto shown = score ? static_cast<std::int64_t>(std::floor(*score + 0.5)) : 0;
                point.insert(QStringLiteral("text"), score ? format::perfFull(shown) : QStringLiteral("–"));
                point.insert(QStringLiteral("tier"), score ? perf_card::tierWord(s, shown) : QString());
                point.insert(QStringLiteral("n"), static_cast<qlonglong>(n));
                point.insert(QStringLiteral("requests"), s.fmt(QStringLiteral("history.perf.requests"), {static_cast<qlonglong>(n)}));
                point.insert(QStringLiteral("hint"), s.fmt(QStringLiteral("history.perf.sample.hint"),
                                                           {static_cast<qlonglong>(n), static_cast<qlonglong>(agentspeed::perf::kPooledMinN)}));
                trend.push_back(point);
            }
        }
        m.insert(QStringLiteral("trend"), trend);
        rows.push_back(m);
    }
    out.insert(QStringLiteral("rows"), rows);
    out.insert(QStringLiteral("single"), single);
    return out;
}
