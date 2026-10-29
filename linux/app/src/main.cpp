#include <QApplication>
#include <QQmlApplicationEngine>
#include <QQmlContext>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QPalette>
#include <QThread>
#include <QTimer>

#include "app_state.hpp"
#include "flyout.hpp"
#include "graphics.hpp"
#include "monitor_worker.hpp"
#include "orb/orb_window.hpp"
#include "strings.hpp"
#include "tray_icon.hpp"

int main(int argc, char* argv[]) {
    // The flyout (and later the orbs) need global positions and keep-above, which Wayland does not give clients:
    // run on XWayland whenever it is there.
    if (qEnvironmentVariableIsEmpty("QT_QPA_PLATFORM") && !qEnvironmentVariableIsEmpty("DISPLAY")) {
        qputenv("QT_QPA_PLATFORM", "xcb");
    }
    QApplication app(argc, argv);
    QApplication::setApplicationName(QStringLiteral("Agent Speed"));
    QApplication::setApplicationVersion(QStringLiteral("1.5.0"));
    QApplication::setOrganizationName(QStringLiteral("zoefix"));
    QGuiApplication::setDesktopFileName(QStringLiteral("agent-speed"));
    QApplication::setQuitOnLastWindowClosed(false);
    QQuickStyle::setStyle(QStringLiteral("Fusion"));
    const bool softwareRendering = chooseGraphicsBackend();

    const bool demo = app.arguments().contains(QStringLiteral("--demo"));
    const bool hidden = app.arguments().contains(QStringLiteral("--hidden"));
    // Development aid: --snapshot=<file.png> renders the dashboard (offscreen works too), saves it and quits;
    // --snapshot-dark does the same with a dark palette.
    QString snapshotPath;
    bool snapshotDark = false;
    for (const auto& arg : app.arguments()) {
        if (arg.startsWith(QStringLiteral("--snapshot="))) snapshotPath = arg.mid(11);
        if (arg.startsWith(QStringLiteral("--snapshot-dark="))) {
            snapshotPath = arg.mid(16);
            snapshotDark = true;
        }
    }
    if (snapshotDark) {
        QPalette dark;
        dark.setColor(QPalette::Window, QColor(0x20, 0x22, 0x28));
        dark.setColor(QPalette::WindowText, QColor(0xEC, 0xEF, 0xF4));
        dark.setColor(QPalette::Base, QColor(0x17, 0x19, 0x1F));
        dark.setColor(QPalette::Text, QColor(0xEC, 0xEF, 0xF4));
        QGuiApplication::setPalette(dark);
    }
    Strings strings;
    AppState state(demo);

    QThread thread;
    thread.setObjectName(QStringLiteral("monitor"));
    auto* worker = new MonitorWorker(&strings, demo);
    worker->moveToThread(&thread);
    QObject::connect(&thread, &QThread::started, worker, &MonitorWorker::start);
    QObject::connect(&thread, &QThread::finished, worker, &QObject::deleteLater);
    QObject::connect(worker, &MonitorWorker::snapshotReady, &state, &AppState::applySnapshot);
    QObject::connect(&state, &AppState::demoChanged, worker, &MonitorWorker::setDemo);

    TrayIcon tray(strings, demo);

    QQmlApplicationEngine engine;
    const bool popup = tray.available() || !snapshotPath.isEmpty();
    engine.setInitialProperties({{QStringLiteral("flyout"), popup}});
    engine.rootContext()->setContextProperty(QStringLiteral("S"), &strings);
    engine.rootContext()->setContextProperty(QStringLiteral("appState"), &state);
    engine.rootContext()->setContextProperty(QStringLiteral("uiLanguage"), strings.language());
    engine.rootContext()->setContextProperty(QStringLiteral("softwareRendering"), softwareRendering);
    engine.loadFromModule(QStringLiteral("AgentSpeed"), QStringLiteral("Dashboard"));
    if (engine.rootObjects().isEmpty()) return 1;
    auto* window = qobject_cast<QQuickWindow*>(engine.rootObjects().constFirst());
    Flyout flyout(window, popup);
    QObject::connect(&state, &AppState::snapshotChanged, &tray, [&] {
        const auto snap = state.snapshot();
        const auto live = snap.value(QStringLiteral("liveText")).toString();
        QString tip = strings.t(QStringLiteral("app.name")) + QStringLiteral(" — ") + live + QLatin1Char(' ') +
                      strings.t(QStringLiteral("unit.tokS"));
        tray.update(snap.value(QStringLiteral("liveOutput")).toDouble(), snap.value(QStringLiteral("compactText")).toString(),
                    tip);
    });
    QObject::connect(&tray, &TrayIcon::demoToggled, &state, &AppState::setDemo);
    QObject::connect(&state, &AppState::demoChanged, &tray, &TrayIcon::setDemo);
    QObject::connect(&tray, &TrayIcon::quitRequested, &app, &QApplication::quit);
    QObject::connect(&tray, &TrayIcon::toggleDashboard, &flyout, &Flyout::toggle);

    orb::OrbWindow orbWindow(strings);
    QObject::connect(&state, &AppState::snapshotChanged, &orbWindow, [&] { orbWindow.applySnapshot(state.snapshot()); });
    QObject::connect(&orbWindow, &orb::OrbWindow::dashboardRequested, &flyout, &Flyout::toggle);
    QObject::connect(&orbWindow, &orb::OrbWindow::quitRequested, &app, &QApplication::quit);
    QObject::connect(&orbWindow, &orb::OrbWindow::orbVisibilityChanged, &tray, &TrayIcon::setOrbVisible);
    QObject::connect(&orbWindow, &orb::OrbWindow::clickThroughChanged, &tray, &TrayIcon::setClickThrough);
    QObject::connect(&tray, &TrayIcon::orbToggled, &orbWindow, &orb::OrbWindow::setOrbVisible);
    QObject::connect(&tray, &TrayIcon::clickThroughToggled, &orbWindow, &orb::OrbWindow::setClickThrough);
    tray.setClickThrough(orbWindow.clickThrough());
    tray.setOrbVisible(false);
    if (snapshotPath.isEmpty()) orbWindow.restore();
    // Development aid: --orb-snapshots=<prefix> saves the orb every 4 s, 16 times, then quits.
    QString orbShots;
    for (const auto& arg : app.arguments()) {
        if (arg.startsWith(QStringLiteral("--orb-snapshots="))) orbShots = arg.mid(16);
    }
    int orbShot = 0;
    QTimer orbShotTimer;
    if (!orbShots.isEmpty()) {
        orbWindow.forceDark(app.arguments().contains(QStringLiteral("--orb-dark")));
        QObject::connect(&orbShotTimer, &QTimer::timeout, &app, [&] {
            orbWindow.snapshot().save(orbShots + QStringLiteral("-%1.png").arg(orbShot, 2, 10, QLatin1Char('0')));
            if (++orbShot >= 16) QApplication::quit();
        });
        orbShotTimer.start(4000);
    }
    // Without a tray (GNOME without the AppIndicator extension) the dashboard is the only way in.
    if (!hidden || !tray.available()) flyout.present();
    if (!snapshotPath.isEmpty()) {
        QTimer::singleShot(3000, &app, [&] {
            window->grabWindow().save(snapshotPath);
            QApplication::quit();
        });
    }

    thread.start();
    const int code = app.exec();
    thread.quit();
    thread.wait();
    return code;
}
