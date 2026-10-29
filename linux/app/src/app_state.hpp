#pragma once

#include <QObject>
#include <QVariantMap>

// The latest snapshot for QML, plus the user's toggles.
class AppState : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantMap snapshot READ snapshot NOTIFY snapshotChanged)
    Q_PROPERTY(bool demo READ demo WRITE setDemo NOTIFY demoChanged)
public:
    explicit AppState(bool demo, QObject* parent = nullptr) : QObject(parent), demo_(demo) {}
    QVariantMap snapshot() const { return snapshot_; }
    bool demo() const { return demo_; }
    void setDemo(bool demo);

public slots:
    void applySnapshot(const QVariantMap& snapshot);

signals:
    void snapshotChanged();
    void historyRequested(const QString& source);
    void demoChanged(bool demo);

private:
    QVariantMap snapshot_;
    bool demo_;
};
