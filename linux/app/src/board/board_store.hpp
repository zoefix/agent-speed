#pragma once

#include <QObject>
#include <QPoint>
#include <QVariantList>
#include <QVariantMap>
#include <optional>

// DESIGN 2c Data model: 0–4 boards persisted as one JSON value (`boards`), written on every change.
class BoardStore : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantList boards READ boards NOTIFY changed)
    Q_PROPERTY(bool canAdd READ canAdd NOTIFY changed)
    Q_PROPERTY(bool anyVisible READ anyVisible NOTIFY changed)

public:
    static constexpr int kMaxBoards = 4;
    explicit BoardStore(QObject* parent = nullptr);

    QVariantList boards() const { return boards_; }
    bool canAdd() const { return boards_.size() < kMaxBoards; }
    bool anyVisible() const;
    QVariantMap board(const QString& id) const;
    std::optional<QPoint> position(const QString& id) const;

    // A new board: the last used style (else neon), M, every source, every readout on.
    Q_INVOKABLE QString add();
    Q_INVOKABLE void remove(const QString& id);
    Q_INVOKABLE void set(const QString& id, const QString& key, const QVariant& value);
    Q_INVOKABLE void setReadout(const QString& id, const QString& readout, bool on);
    Q_INVOKABLE void setSources(const QString& id, const QVariant& sources);
    Q_INVOKABLE void setAllVisible(bool visible);
    void setPosition(const QString& id, QPoint topLeft);

signals:
    void changed();

private:
    void load();
    void save();
    static QVariantMap normalized(QVariantMap board);
    QVariantList boards_;
    QVariantMap positions_;
    QString lastStyle_ = QStringLiteral("neon");
};
