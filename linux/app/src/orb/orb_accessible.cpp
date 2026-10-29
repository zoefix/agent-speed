#include "orb/orb_accessible.hpp"

#include <QGuiApplication>

#include "orb/orb_window.hpp"

namespace orb {

namespace {

const char* const kActions[] = {"menu.dashboard", "a11y.orb.showMenu", "a11y.orb.toggleUnit", "a11y.orb.moveCorner", "menu.orb.settings"};

}  // namespace

OrbAccessible::OrbAccessible(OrbWindow* window) : QAccessibleObject(window) {}

OrbWindow* OrbAccessible::orb() const { return static_cast<OrbWindow*>(object()); }

QWindow* OrbAccessible::window() const { return orb(); }

QAccessibleInterface* OrbAccessible::parent() const { return QAccessible::queryAccessibleInterface(qApp); }

QString OrbAccessible::text(QAccessible::Text t) const {
    switch (t) {
    case QAccessible::Name: return orb()->accessibleLabel();
    case QAccessible::Description:
    case QAccessible::Help: return orb()->accessibleHint();
    default: return {};
    }
}

QRect OrbAccessible::rect() const {
    const QPointF c = orb()->discCenter();
    const double r = orb()->diameter() / 2;
    return QRectF(c.x() - r, c.y() - r, 2 * r, 2 * r).toAlignedRect();
}

QAccessible::State OrbAccessible::state() const {
    QAccessible::State s;
    s.invisible = !orb()->isVisible();
    s.readOnly = true;
    return s;
}

void* OrbAccessible::interface_cast(QAccessible::InterfaceType type) {
    if (type == QAccessible::ActionInterface) return static_cast<QAccessibleActionInterface*>(this);
    return QAccessibleObject::interface_cast(type);
}

QStringList OrbAccessible::actionNames() const {
    QStringList names;
    for (const char* key : kActions) names.push_back(orb()->actionName(QString::fromLatin1(key)));
    return names;
}

void OrbAccessible::doAction(const QString& name) {
    for (const char* key : kActions) {
        if (orb()->actionName(QString::fromLatin1(key)) == name) orb()->performAction(QString::fromLatin1(key));
    }
}

QAccessibleInterface* OrbAccessible::factory(const QString&, QObject* object) {
    if (auto* window = qobject_cast<OrbWindow*>(object)) return new OrbAccessible(window);
    return nullptr;
}

}  // namespace orb
