#pragma once

#include <QObject>
#include <QPointer>
#include <QQuickView>
#include <QTimer>
#include <QVariantMap>
#include <map>
#include <memory>

#include "orb/idle_fade.hpp"

class BoardStore;
class QQmlEngine;

// DESIGN 2c Interaction and window: one frameless, transparent, non-activating window per visible board, keep-above
// and click-through as its settings say, dragged anywhere and snapped to a 12 pt margin near a screen edge, and faded
// when idle like an orb (the square and its bloom together, as one window).
class BoardWindows : public QObject {
    Q_OBJECT
public:
    // The margin around the board's rounded square, where its bloom fades out (DESIGN 2c Perimeter: 40 pt).
    static constexpr int kMargin = 40;
    BoardWindows(BoardStore& store, QQmlEngine& engine, QObject* parent = nullptr);
    ~BoardWindows() override;
    void apply(const QVariantMap& views);
    // Development aid: a board's window, for snapshots.
    QQuickWindow* window(const QString& id) const;

    Q_INVOKABLE void startMove(const QString& id);
    Q_INVOKABLE void openMenu(const QString& id);
    // The pointer is on the board's square (a drag included): a faded board comes back at once.
    Q_INVOKABLE void setHovered(const QString& id, bool hovered);

signals:
    void dashboardRequested();
    void settingsRequested();

private:
    struct Board {
        std::unique_ptr<QQuickView> view;
        QTimer settle;
        QString style, size;
        bool alwaysOnTop = true, clickThrough = false;
        double opacity = 1;
        bool fadeWhenIdle = false, idle = true, hovered = false;
        IdleFade fade;
        std::unique_ptr<IdleFadeMotion> motion;
    };
    void sync();
    void updateFade(Board& board);
    static void applyOpacity(Board& board);
    static int side(const QString& size);
    QPoint defaultPosition(int index, int side) const;

    BoardStore& store_;
    QQmlEngine& engine_;
    std::map<QString, std::unique_ptr<Board>> boards_;
    QVariantMap views_;
};
