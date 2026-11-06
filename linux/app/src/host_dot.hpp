#pragma once

#include <QColor>
#include <QPixmap>
#include <QString>

// DESIGN 13: a host's status dot outside QML (menus): `connected`, `progress`, `retrying`, `attention` or `idle`
// (hollow), in the colors of the QML HostDot for the current appearance.
QColor hostDotColor(const QString& kind, bool dark);
QPixmap hostDot(const QString& kind);
