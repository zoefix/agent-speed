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
    void retranslate();
    // UPDATES 4: a top item while an update is available (empty: none).
    void setUpdate(const QString& version);
    void notify(const QString& title, const QString& body);
    // DESIGN 11 Quick menu: while reporting is on and registered, an item with a submenu (pause, resume, leaderboard,
    // settings).
    void setReport(bool visible, const QString& text, bool paused, bool leaderboard);
    // DESIGN 2c: show / hide every board and add one.
    void setBoards(bool any, bool visible, bool canAdd);

signals:
    void toggleDashboard();
    void demoToggled(bool demo);
    void quitRequested();
    void settingsRequested();
    void historyRequested();
    void orbToggled(bool visible);
    void clickThroughToggled(bool on);
    void updateRequested();
    void reportPause(int hours);
    void reportResume();
    void reportLeaderboard();
    void reportSettings();
    void boardsToggled();
    void boardAdd();

private:
    QSystemTrayIcon* tray_;
    QMenu* menu_;
    QAction* open_;
    QAction* settings_;
    QAction* history_;
    QAction* quit_;
    QAction* demo_;
    QAction* orb_;
    QAction* clickThrough_;
    QAction* update_;
    QAction* updateSeparator_;
    QMenu* reportMenu_ = nullptr;
    QAction* reportItem_ = nullptr;
    QAction* reportHour_ = nullptr;
    QAction* reportIndefinite_ = nullptr;
    QAction* reportResume_ = nullptr;
    QAction* reportLeaderboard_ = nullptr;
    QAction* reportSettings_ = nullptr;
    QAction* boardsToggle_ = nullptr;
    QAction* boardAdd_ = nullptr;
    bool boardsVisible_ = false;
    QString updateVersion_;
    const Strings& strings_;
    QString lastText_;
    int lastBand_ = -1;
};
