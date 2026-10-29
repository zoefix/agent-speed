#pragma once

#include <QObject>
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

private:
    const Strings& strings_;
    std::shared_ptr<agentspeed::pricing::PriceBook> prices_;
};
