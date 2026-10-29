#include "catalog.hpp"

#include <QCollator>
#include <QCoreApplication>
#include <QDateTime>
#include <QDesktopServices>
#include <QFileDialog>
#include <QLocale>
#include <QRegularExpression>
#include <QUrl>
#include <algorithm>

#include "agentspeed/money.hpp"
#include "agentspeed/price_book.hpp"
#include "strings.hpp"

namespace {

QString fill(QString pattern, const QStringList& values) {
    for (const auto& value : values) {
        const auto at = pattern.indexOf(QRegularExpression(QStringLiteral("%[@d]")));
        if (at < 0) break;
        pattern.replace(at, 2, value);
    }
    return pattern;
}

QString displayDate(const std::string& isoDate) {
    auto text = QString::fromStdString(isoDate);
    return text.replace(QLatin1Char('-'), QLatin1Char('/'));
}

}  // namespace

Catalog::Catalog(const Strings& strings, std::shared_ptr<agentspeed::pricing::PriceBook> prices, QObject* parent)
    : QObject(parent), strings_(strings), prices_(std::move(prices)) {}

QString Catalog::version() const { return QCoreApplication::applicationVersion(); }

QVariantList Catalog::currencies() const {
    const auto& table = agentspeed::money::CurrencyTable::bundled();
    const auto language = strings_.language().toStdString();
    QVariantList out;
    QStringList seen;
    const auto row = [&](const agentspeed::money::Currency& c, bool common) {
        const auto code = QString::fromStdString(c.code);
        QVariantMap map;
        map.insert(QStringLiteral("code"), code);
        map.insert(QStringLiteral("name"), strings_.t(QStringLiteral("currency.") + code));
        map.insert(QStringLiteral("symbol"), QString::fromStdString(c.symbolFor(language)));
        map.insert(QStringLiteral("common"), common);
        return map;
    };
    for (const auto* c : table.commonGroup(language)) {
        out.push_back(row(*c, true));
        seen.push_back(QString::fromStdString(c->code));
    }
    QVariantList rest;
    for (const auto& c : table.currencies()) {
        if (!seen.contains(QString::fromStdString(c.code))) rest.push_back(row(c, false));
    }
    QCollator collator(QLocale(strings_.language()));
    collator.setCaseSensitivity(Qt::CaseInsensitive);
    std::sort(rest.begin(), rest.end(), [&](const QVariant& a, const QVariant& b) {
        const auto order = collator.compare(a.toMap().value(QStringLiteral("name")).toString(), b.toMap().value(QStringLiteral("name")).toString());
        return order != 0 ? order < 0 : a.toMap().value(QStringLiteral("code")).toString() < b.toMap().value(QStringLiteral("code")).toString();
    });
    out.append(rest);
    return out;
}

QString Catalog::currencyLabel(const QString& code) const {
    return code + QStringLiteral(" · ") + strings_.t(QStringLiteral("currency.") + code);
}

void Catalog::setAcceptedRates(const QString& provider, std::int64_t dateMs, std::map<std::string, agentspeed::money::Decimal> rates) {
    provider_ = provider;
    ratesDateMs_ = dateMs;
    rates_ = std::move(rates);
    ++revision_;
    emit changed();
}

QString Catalog::rateFor(const QString& code) const {
    if (const auto it = rates_.find(code.toStdString()); it != rates_.end()) return QString::fromStdString(it->second.text());
    const auto& rates = agentspeed::money::CurrencyTable::bundled().fallbackRates().rates;
    const auto it = rates.find(code.toStdString());
    return it == rates.end() ? QStringLiteral("1") : QString::fromStdString(it->second.text());
}

QString Catalog::rateStatus(const QString& code) const {
    if (code == QStringLiteral("USD")) return {};
    const auto& table = agentspeed::money::CurrencyTable::bundled();
    if (const auto it = rates_.find(code.toStdString()); it != rates_.end()) {
        // `US$1 = NT$32.55 · 2026/10/22 · open.er-api.com`
        const auto language = strings_.language().toStdString();
        const auto& usd = table.currencyOrUsd("USD");
        const auto& target = table.currencyOrUsd(code.toStdString());
        const auto date = QDateTime::fromMSecsSinceEpoch(ratesDateMs_).toUTC().toString(QStringLiteral("yyyy/MM/dd"));
        return fill(strings_.t(QStringLiteral("rates.status")),
                    {QString::fromStdString(usd.symbolFor(language)) + QStringLiteral("1"),
                     QString::fromStdString(target.symbolFor(language) + agentspeed::money::significant(it->second)), date, provider_});
    }
    return fill(strings_.t(QStringLiteral("rates.fallback")), {displayDate(table.fallbackRates().date)});
}

QString Catalog::priceStatus() const {
    const auto table = prices_->current();
    return fill(strings_.t(QStringLiteral("prices.status")),
                {QString::number(table->version()), displayDate(table->updated()), QString::number(table->models().size())});
}

QString Catalog::chooseFolder(const QString& title, const QString& start) const {
    return QFileDialog::getExistingDirectory(nullptr, title, start, QFileDialog::ShowDirsOnly | QFileDialog::DontResolveSymlinks);
}

QString Catalog::chooseFileOrFolder(const QString& title, const QString& start) const {
    QFileDialog dialog(nullptr, title, start);
    dialog.setFileMode(QFileDialog::AnyFile);
    dialog.setOption(QFileDialog::DontResolveSymlinks);
    if (dialog.exec() != QDialog::Accepted || dialog.selectedFiles().isEmpty()) return {};
    return dialog.selectedFiles().constFirst();
}

void Catalog::openUrl(const QString& url) const { QDesktopServices::openUrl(QUrl(url)); }
