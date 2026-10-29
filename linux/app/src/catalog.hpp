#pragma once

#include <QObject>
#include <map>

#include "agentspeed/money.hpp"
#include <QVariantList>
#include <memory>

class Strings;

namespace agentspeed::pricing {
class PriceBook;
}

// Read-only facts and pickers for the settings window: currencies, the price table, rates and folder choosers.
class Catalog : public QObject {
    Q_OBJECT
    Q_PROPERTY(QString version READ version CONSTANT)
    // Changes when accepted exchange rates arrive, so bindings that convert re-evaluate.
    Q_PROPERTY(int revision READ revision NOTIFY changed)
public:
    Catalog(const Strings& strings, std::shared_ptr<agentspeed::pricing::PriceBook> prices, QObject* parent = nullptr);

    QString version() const;
    // {code, name, symbol, common} in picker order: the common group, then the rest by localized name.
    Q_INVOKABLE QVariantList currencies() const;
    Q_INVOKABLE QString currencyLabel(const QString& code) const;
    // Units per US dollar for the display currency (the bundled rates until a fetch is accepted, NETWORK 4).
    Q_INVOKABLE QString rateFor(const QString& code) const;
    Q_INVOKABLE QString rateStatus(const QString& code) const;
    Q_INVOKABLE QString priceStatus() const;
    Q_INVOKABLE QString chooseFolder(const QString& title, const QString& start) const;
    Q_INVOKABLE QString chooseFileOrFolder(const QString& title, const QString& start) const;
    Q_INVOKABLE void openUrl(const QString& url) const;
    int revision() const { return revision_; }
    // NETWORK 4: the last accepted rates (provider host, rate date), used instead of the bundled ones.
    void setAcceptedRates(const QString& provider, std::int64_t dateMs, std::map<std::string, agentspeed::money::Decimal> rates);

signals:
    void changed();

private:
    const Strings& strings_;
    std::shared_ptr<agentspeed::pricing::PriceBook> prices_;
    int revision_ = 0;
    QString provider_;
    std::int64_t ratesDateMs_ = 0;
    std::map<std::string, agentspeed::money::Decimal> rates_;
};
