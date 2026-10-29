#pragma once

#include <QPointF>

class QWindow;

// X11 window properties Qt does not expose; every function does nothing outside xcb.
namespace orb::x11 {

bool available();
// Clicks land only inside the disc (logical center and radius within the window); elsewhere they reach the desktop.
void setInputDisc(QWindow* window, QPointF center, double radius);
// KWin blurs the desktop behind the disc (_KDE_NET_WM_BLUR_BEHIND_REGION).
void setBlurDisc(QWindow* window, QPointF center, double radius);
bool blurSupported();
// Keeps the orb out of the task manager and the pager.
void skipTaskbar(QWindow* window);

}  // namespace orb::x11
