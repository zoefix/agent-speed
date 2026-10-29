#include "history_service.hpp"

#include <QDate>
#include <QDateTime>
#include <QFile>
#include <QFileDialog>
#include <QStandardPaths>
#include <QTimer>
#include <QTimeZone>
#include <cmath>
#include <tuple>

#include "agentspeed/money.hpp"
#include "agentspeed/price_book.hpp"
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

}  // namespace

HistoryWorker::HistoryWorker(std::shared_ptr<agentspeed::pricing::PriceBook> prices, QString storePath)
    : prices_(std::move(prices)), storePath_(std::move(storePath)) {}

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
        analyzer_->run(locations, enabled, agentspeed::localZone(environment), zoneId, [this] { return cancelled_.load(); });
    } catch (const std::exception& error) {
        qWarning("history analysis failed: %s", error.what());
    }
    emit analyzing(false);
    publish();
}

void HistoryWorker::clear() {
    if (!analyzer_) return;
    analyzer_->clear();
    publish();
    analyze();
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
    for (const auto& day : analyzer_->days()) {
        const auto& t = day.totals;
        QVariantMap row;
        row.insert(QStringLiteral("date"), QString::fromStdString(day.key.day));
        row.insert(QStringLiteral("source"), QString::fromUtf8(agentspeed::sourceId(day.key.source)));
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
    emit daysReady(rows, QDateTime::currentMSecsSinceEpoch());
    // METRICS 6.11 Reading: a day's record of a (source, model) is the sum over files.
    std::map<std::tuple<std::string, agentspeed::SourceKind, std::string>, agentspeed::perf::Record> sums;
    for (const auto& r : analyzer_->perfRecords()) sums[{r.day, r.source, r.model}].add(r.record);
    auto records = std::make_shared<std::vector<agentspeed::perf::DayRecord>>();
    for (const auto& [key, record] : sums) records->push_back({std::get<0>(key), std::get<1>(key), std::get<2>(key), record});
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
                             const QString& rate) const {
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
            row.value(QStringLiteral("peak")).isValid() ? oneDecimal(row.value(QStringLiteral("peak")).toDouble()) : QString(),
            QString::number(row.value(QStringLiteral("agents")).toInt()),
            QString::number(row.value(QStringLiteral("unfinalized")).toLongLong()),
            models.join(QStringLiteral("; ")),
            cost.isValid() ? QString::fromStdString(agentspeed::money::csvUsd(cost.toLongLong())) : QString(),
            cost.isValid() ? QString::fromStdString(agentspeed::money::csv(cost.toLongLong(), r, c.digits)) : QString(),
            row.value(QStringLiteral("unpriced")).isValid() ? QString::number(row.value(QStringLiteral("unpriced")).toLongLong()) : QString(),
        };
        // DESIGN 10 CSV: the day's traffic of the row's source(s), empty before measuring began; then this computer's row.
        const auto date = row.value(QStringLiteral("date")).toString();
        const auto t = trafficSums(date, sourceCode == QLatin1String("all") ? QString() : row.value(QStringLiteral("source")).toString());
        const bool recorded = !trafficSince_.isEmpty() && date >= trafficSince_;
        for (const auto v : {t.rx, t.tx, t.rxTools, t.txTools}) fields << (recorded ? QString::number(v) : QString());
        for (auto& field : fields) field = csvField(field);
        out += fields.join(QLatin1Char(',')).toUtf8() + "\r\n";
        if (recorded) {
            const auto m = trafficSums(date, QStringLiteral("machine"));
            QStringList machine(header.size());
            machine[0] = date;
            machine[1] = QStringLiteral("machine");
            machine[header.size() - 4] = QString::number(m.rx);
            machine[header.size() - 3] = QString::number(m.tx);
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
