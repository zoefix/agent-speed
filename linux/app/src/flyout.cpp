#include "flyout.hpp"

#include <QCursor>
#include <QGuiApplication>
#include <QQuickWindow>
#include <QScreen>
#include <algorithm>

namespace {

constexpr int kGap = 8;
// A tray click that took focus away has already closed the flyout; it must not reopen it.
constexpr qint64 kRefocusWindowMs = 400;

}  // namespace

Flyout::Flyout(QQuickWindow* window, bool popup, QObject* parent) : QObject(parent), window_(window), popup_(popup) {
    connect(window_, SIGNAL(wantedHeightChanged()), this, SLOT(place()));
    connect(window_, SIGNAL(dismissed()), this, SLOT(dismiss()));
    connect(window_, &QWindow::activeChanged, this, [this] {
        if (popup_ && !window_->isActive() && window_->isVisible()) {
            window_->hide();
            hiddenByFocus_.start();
        }
    });
}

void Flyout::toggle() {
    if (window_->isVisible()) {
        dismiss();
        return;
    }
    if (hiddenByFocus_.isValid() && hiddenByFocus_.elapsed() < kRefocusWindowMs) return;
    present();
}

void Flyout::present() {
    place();
    window_->show();
    window_->raise();
    window_->requestActivate();
}

void Flyout::dismiss() { window_->hide(); }

void Flyout::place() {
    if (placing_) return;
    placing_ = true;
    QScreen* screen = QGuiApplication::screenAt(QCursor::pos());
    if (!screen) screen = QGuiApplication::primaryScreen();
    const QRect full = screen->geometry();
    const QRect work = screen->availableGeometry();
    const int margin = window_->property("margin").toInt();
    window_->setProperty("maxPanelHeight", work.height() - (popup_ ? 2 * kGap : 0));
    const int width = window_->width();
    const int height = window_->property("wantedHeight").toInt();
    placing_ = false;
    if (!popup_) {
        if (!centered_) {
            window_->setGeometry(QRect(QPoint(), QSize(width, std::min(height, work.height())))
                                     .translated(work.center() - QPoint(width / 2, std::min(height, work.height()) / 2)));
            centered_ = true;
        } else {
            window_->resize(width, height);
        }
        return;
    }
    const int panelWidth = width - 2 * margin;
    const int panelHeight = height - 2 * margin;
    const int minX = work.left() + kGap, maxX = std::max(minX, work.right() + 1 - kGap - panelWidth);
    const int minY = work.top() + kGap, maxY = std::max(minY, work.bottom() + 1 - kGap - panelHeight);
    const bool bottomPanel = work.bottom() < full.bottom();
    const bool topPanel = !bottomPanel && work.top() > full.top();
    const bool leftPanel = !bottomPanel && !topPanel && work.left() > full.left();
    int x = leftPanel ? minX : maxX;
    int y = topPanel ? minY : maxY;
    // X11 sessions report the cursor on the panel during the tray click: line the flyout up with it.
    const QPoint cursor = QCursor::pos();
    if (full.contains(cursor) && !work.contains(cursor)) {
        if (cursor.y() > work.bottom() || cursor.y() < work.top()) {
            x = std::clamp(cursor.x() - panelWidth / 2, minX, maxX);
        } else {
            y = std::clamp(cursor.y() - panelHeight / 2, minY, maxY);
        }
    }
    window_->setGeometry(x - margin, y - margin, width, height);
}
