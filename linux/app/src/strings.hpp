#pragma once

#include <QHash>
#include <QObject>
#include <QString>
#include <QVariantList>

// The shared UI strings (spec/strings.json) in the chosen language: zh-Hant, zh-Hans, ja or en ("system" follows the
// system locale). QML bindings read `S.t(key)` and depend on `rev`, so a language change retranslates live.
class Strings : public QObject {
    Q_OBJECT
    Q_PROPERTY(int rev READ revision NOTIFY changed)
    Q_PROPERTY(QString language READ language NOTIFY changed)
public:
    explicit Strings(QObject* parent = nullptr);
    Q_INVOKABLE QString t(const QString& key) const;
    // The same, for bindings that must re-evaluate when the language changes.
    Q_INVOKABLE QString tr2(int revision, const QString& key) const;
    // `fmt("perf.status.none", {2, 3})`: the string's %@ and %d filled in order, %% kept as %.
    QString fmt(const QString& key, const QVariantList& args = {}) const;
    QString language() const { return language_; }
    int revision() const { return revision_; }
    void setChoice(const QString& choice);

signals:
    void changed();

private:
    void load();

    QString language_;
    int revision_ = 0;
    QHash<QString, QString> table_;
};
