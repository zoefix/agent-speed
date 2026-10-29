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
                       QStringLiteral("cost_") + currency.toLower(), QStringLiteral("unpriced_requests")};
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
        for (auto& field : fields) field = csvField(field);
        out += fields.join(QLatin1Char(',')).toUtf8() + "\r\n";
    }
    return file.write(out) == out.size();
}
