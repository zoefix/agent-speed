#pragma once

#include <QObject>
#include <QString>

// DESIGN 0: the app's appearance — Auto follows the desktop; Light and Dark override it for every window, the orbs
// and their name plates included.
class Appearance : public QObject {
    Q_OBJECT
public:
    static Appearance& instance();

    void apply(const QString& choice);
    bool dark() const;

signals:
    void changed();

private:
    Appearance();

    QString choice_;
};
