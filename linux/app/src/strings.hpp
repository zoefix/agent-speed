#pragma once

#include <QHash>
#include <QObject>
#include <QString>

// The shared UI strings (spec/strings.json) in the system language: zh-Hant, zh-Hans, ja or en.
class Strings : public QObject {
    Q_OBJECT
public:
    explicit Strings(QObject* parent = nullptr);
    Q_INVOKABLE QString t(const QString& key) const;
    QString language() const { return language_; }

private:
    QString language_;
    QHash<QString, QString> table_;
};
