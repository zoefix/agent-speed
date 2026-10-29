#pragma once

#include <QElapsedTimer>
#include <QObject>

class QQuickWindow;

// Shows the dashboard as a flyout in the tray's corner of the work area, keeps it there as its height changes, and
// hides it when it loses focus. Without a tray it is a plain window.
class Flyout : public QObject {
    Q_OBJECT
public:
    Flyout(QQuickWindow* window, bool popup, QObject* parent = nullptr);

public slots:
    void toggle();
    void present();
    void dismiss();

private slots:
    void place();

private:
    QQuickWindow* window_;
    bool popup_;
    bool centered_ = false;
    bool placing_ = false;
    QElapsedTimer hiddenByFocus_;
};
