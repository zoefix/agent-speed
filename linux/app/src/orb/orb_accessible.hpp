#pragma once

#include <QAccessibleObject>

namespace orb {

class OrbWindow;

// DESIGN 2b Accessibility: each orb window is one element — its label (`a11y.orb.named`, `a11y.orb`, or
// `a11y.orb.unavailable`), its hint (`a11y.orb.sources`), and the actions dashboard, menu, toggle unit, move to the
// next corner and the orb's settings. Reached through QAccessible's factory for OrbWindow.
class OrbAccessible : public QAccessibleObject, public QAccessibleActionInterface {
public:
    explicit OrbAccessible(OrbWindow* window);

    QWindow* window() const override;
    QAccessibleInterface* parent() const override;
    int childCount() const override { return 0; }
    QAccessibleInterface* child(int) const override { return nullptr; }
    int indexOfChild(const QAccessibleInterface*) const override { return -1; }
    QAccessible::Role role() const override { return QAccessible::Window; }
    QString text(QAccessible::Text t) const override;
    QRect rect() const override;
    QAccessible::State state() const override;
    void* interface_cast(QAccessible::InterfaceType type) override;

    QStringList actionNames() const override;
    void doAction(const QString& name) override;
    QStringList keyBindingsForAction(const QString&) const override { return {}; }

    static QAccessibleInterface* factory(const QString& className, QObject* object);

private:
    OrbWindow* orb() const;
};

}  // namespace orb
