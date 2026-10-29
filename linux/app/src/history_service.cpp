#include "history_service.hpp"

#include <QDateTime>
#include <QFile>
#include <QFileDialog>
#include <QStandardPaths>
#include <QTimer>
#include <QTimeZone>

#include "agentspeed/money.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/tz.hpp"
#include "strings.hpp"
#include "task_card.hpp"

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
    auto records = std::make_shared<std::vector<agentspeed::tasks::TaskRecord>>(analyzer_->taskRecords());
    TaskModels models;
    for (int k = 0; k <= static_cast<int>(agentspeed::SourceKind::Droid); ++k) {
        const auto source = static_cast<agentspeed::SourceKind>(k);
        if (!agentspeed::tasks::isScoredSource(source)) continue;
        std::vector<agentspeed::tasks::TaskRecord> part;
        for (const auto& r : *records) {
            if (r.source == source) part.push_back(r);
        }
        if (!part.empty()) models[source] = agentspeed::tasks::calibrate(part);
    }
    emit tasksReady(std::move(records), std::move(models));
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
    return QString::fromStdString(format(amount(microUsd, r), c, language.toStdString(), Form::Amount).text);
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

void HistoryModel::applyTasks(TaskRecords records) {
    tasks_ = std::move(records);
    ++tasksRevision_;
    emit tasksChanged();
}

namespace {

QString durationText(const Strings& s, double ms) {
    const auto seconds = agentspeed::tasks::roundHalfAway(ms / 1000);
    if (seconds < 60) return task_card::fmt(s, QStringLiteral("time.seconds"), {static_cast<qlonglong>(seconds)});
    const auto minutes = agentspeed::tasks::roundHalfAway(ms / 60'000);
    if (minutes < 60) return task_card::fmt(s, QStringLiteral("time.minutes"), {static_cast<qlonglong>(minutes)});
    const auto hours = minutes / 60, rest = minutes % 60;
    if (rest == 0) return task_card::fmt(s, QStringLiteral("time.hours"), {static_cast<qlonglong>(hours)});
    return task_card::fmt(s, QStringLiteral("time.hoursMinutes"), {static_cast<qlonglong>(hours), static_cast<qlonglong>(rest)});
}

QString percentText(double rate) { return QString::number(agentspeed::tasks::roundHalfAway(rate * 100)) + QLatin1Char('%'); }

QVariant number(const std::optional<double>& v) { return v ? QVariant(*v) : QVariant(); }

// The figures of one aggregate (a source side or a model row), with the texts the window shows.
QVariantMap figures(const Strings& s, const agentspeed::tasks::Aggregate& a) {
    QVariantMap out;
    out.insert(QStringLiteral("tasks"), static_cast<qlonglong>(a.tasks));
    out.insert(QStringLiteral("scored"), static_cast<qlonglong>(a.scoredTasks));
    out.insert(QStringLiteral("low"), a.scoredTasks < 10);
    out.insert(QStringLiteral("median"), number(a.medianScore));
    out.insert(QStringLiteral("medianText"), a.medianShown ? QString::number(*a.medianShown) : QStringLiteral("–"));
    out.insert(QStringLiteral("q1"), number(a.lowerQuartile));
    out.insert(QStringLiteral("q3"), number(a.upperQuartile));
    QVariantList bins;
    for (const auto b : a.histogram) bins.push_back(static_cast<qlonglong>(b));
    out.insert(QStringLiteral("histogram"), bins);
    int medianBin = -1;
    if (a.medianScore) {
        medianBin = 0;
        for (const double edge : {10, 20, 30, 40, 50, 60, 70, 80, 90}) medianBin += *a.medianScore >= edge ? 1 : 0;
    }
    out.insert(QStringLiteral("medianBin"), medianBin);
    out.insert(QStringLiteral("countsText"), task_card::fmt(s, QStringLiteral("history.tasks.counts"),
                                                             {static_cast<qlonglong>(a.scoredTasks), static_cast<qlonglong>(a.tasks)}));
    const auto metric = [&](const QString& key, const std::optional<double>& value, const QString& text, const QString& tooltip, bool lowerBetter) {
        QVariantMap m;
        m.insert(QStringLiteral("key"), key);
        m.insert(QStringLiteral("label"), s.t(QStringLiteral("history.tasks.metric.") + key));
        m.insert(QStringLiteral("hint"), s.t(QStringLiteral("history.tasks.metric.") + key + QStringLiteral(".hint")));
        m.insert(QStringLiteral("value"), number(value));
        m.insert(QStringLiteral("text"), value ? text : QStringLiteral("–"));
        m.insert(QStringLiteral("tooltip"), tooltip);
        m.insert(QStringLiteral("lowerBetter"), lowerBetter);
        return m;
    };
    QVariantList metrics;
    metrics.push_back(metric(QStringLiteral("success"), a.successRate, a.successRate ? percentText(*a.successRate) : QString(),
                             task_card::fmt(s, QStringLiteral("history.tasks.rate.scored"),
                                            {static_cast<qlonglong>(a.successes), static_cast<qlonglong>(a.scoredTasks)}),
                             false));
    metrics.push_back(metric(QStringLiteral("stall"), a.stallRate, a.stallRate ? percentText(*a.stallRate) : QString(),
                             task_card::fmt(s, QStringLiteral("history.tasks.rate.tasks"),
                                            {static_cast<qlonglong>(a.stalls), static_cast<qlonglong>(a.tasks)}),
                             true));
    metrics.push_back(metric(QStringLiteral("fixFail"), a.fixFailureRate, a.fixFailureRate ? percentText(*a.fixFailureRate) : QString(),
                             task_card::fmt(s, QStringLiteral("history.tasks.rate.fixes"),
                                            {static_cast<qlonglong>(a.fixFailures), static_cast<qlonglong>(a.fixAttempts)}),
                             true));
    metrics.push_back(metric(QStringLiteral("attempts"), a.recoverAttempts,
                             a.recoverAttempts ? task_card::fmt(s, QStringLiteral("history.tasks.attempts"), {QString::number(*a.recoverAttempts, 'f', 1)})
                                               : QString(),
                             a.recoverAttempts ? QString() : s.t(QStringLiteral("history.tasks.noValue.attempts")), true));
    metrics.push_back(metric(QStringLiteral("firstPass"), a.timeToFirstPassMs,
                             a.timeToFirstPassMs ? durationText(s, *a.timeToFirstPassMs) : QString(),
                             a.timeToFirstPassMs ? QString() : s.t(QStringLiteral("history.tasks.noValue.firstPass")), true));
    out.insert(QStringLiteral("metrics"), metrics);
    return out;
}

}  // namespace

QVariantMap HistoryModel::taskComparison(const QString& first, const QString& last, const QString& source) const {
    QVariantMap out;
    if (!tasks_ || !strings_) {
        out.insert(QStringLiteral("sources"), QVariantList());
        out.insert(QStringLiteral("byModel"), QVariantList());
        return out;
    }
    const auto only = source.isEmpty() ? std::nullopt : agentspeed::sourceFromId(source.toStdString());
    const auto result = agentspeed::tasks::compare(*tasks_, first.toStdString(), last.toStdString(), only);
    QVariantList sources;
    for (const auto& row : result.bySource) {
        auto side = figures(*strings_, row.aggregate);
        const auto id = QString::fromUtf8(agentspeed::sourceId(row.source));
        side.insert(QStringLiteral("source"), id);
        side.insert(QStringLiteral("name"), strings_->t(QStringLiteral("source.") + id));
        sources.push_back(side);
    }
    QVariantList models;
    for (const auto& row : result.byModel) {
        auto m = figures(*strings_, row.aggregate);
        m.insert(QStringLiteral("model"), QString::fromStdString(row.model));
        m.insert(QStringLiteral("name"), row.model.empty() ? strings_->t(QStringLiteral("cost.noModelName")) : QString::fromStdString(row.model));
        QStringList ids, names;
        for (const auto kind : row.sources) {
            const auto id = QString::fromUtf8(agentspeed::sourceId(kind));
            ids << id;
            names << strings_->t(QStringLiteral("source.") + id);
        }
        m.insert(QStringLiteral("sources"), ids);
        m.insert(QStringLiteral("sourcesText"), task_card::fmt(*strings_, QStringLiteral("history.tasks.model.sources"), {names.join(QStringLiteral(", "))}));
        models.push_back(m);
    }
    out.insert(QStringLiteral("sources"), sources);
    out.insert(QStringLiteral("byModel"), models);
    return out;
}
