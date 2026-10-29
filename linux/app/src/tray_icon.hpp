#pragma once

#include <QObject>
#include <QString>

class QAction;
class QMenu;
class QSystemTrayIcon;
class Strings;

// The StatusNotifierItem tray icon with the live number drawn into it (no text label on KDE).
class TrayIcon : public QObject {
    Q_OBJECT
public:
    TrayIcon(const Strings& strings, bool demo, QObject* parent = nullptr);
    ~TrayIcon() override;
    bool available() const;

public slots:
    void update(double liveOutput, const QString& compactText, const QString& tooltip);
    void setDemo(bool demo);
    void setOrbVisible(bool visible);
    void setClickThrough(bool on);

signals:
    void toggleDashboard();
    void demoToggled(bool demo);
    void quitRequested();
    void orbToggled(bool visible);
    void clickThroughToggled(bool on);

private:
    QSystemTrayIcon* tray_;
    QMenu* menu_;
    QAction* demo_;
    QAction* orb_;
    QAction* clickThrough_;
    const Strings& strings_;
    QString lastText_;
    int lastBand_ = -1;
};
