#include <QApplication>
#include <QQmlApplicationEngine>
#include <QQmlContext>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QThread>

#include "app_state.hpp"
#include "monitor_worker.hpp"
#include "strings.hpp"
#include "tray_icon.hpp"

int main(int argc, char* argv[]) {
    QApplication app(argc, argv);
    QApplication::setApplicationName(QStringLiteral("Agent Speed"));
    QApplication::setApplicationVersion(QStringLiteral("1.5.0"));
    QApplication::setOrganizationName(QStringLiteral("zoefix"));
    QGuiApplication::setDesktopFileName(QStringLiteral("agent-speed"));
    QApplication::setQuitOnLastWindowClosed(false);
    QQuickStyle::setStyle(QStringLiteral("Fusion"));

    const bool demo = app.arguments().contains(QStringLiteral("--demo"));
    const bool hidden = app.arguments().contains(QStringLiteral("--hidden"));
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

    QQmlApplicationEngine engine;
    engine.rootContext()->setContextProperty(QStringLiteral("S"), &strings);
    engine.rootContext()->setContextProperty(QStringLiteral("appState"), &state);
    engine.rootContext()->setContextProperty(QStringLiteral("uiLanguage"), strings.language());
    engine.loadFromModule(QStringLiteral("AgentSpeed"), QStringLiteral("Dashboard"));
    if (engine.rootObjects().isEmpty()) return 1;
    auto* window = qobject_cast<QQuickWindow*>(engine.rootObjects().constFirst());

    TrayIcon tray(strings, demo);
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
    QObject::connect(&tray, &TrayIcon::toggleDashboard, window, [window] {
        if (window->isVisible()) {
            window->hide();
        } else {
            window->show();
            window->raise();
            window->requestActivate();
        }
    });
    // Without a tray (GNOME without the AppIndicator extension) the dashboard is the only way in.
    if (!hidden || !tray.available()) window->show();

    thread.start();
    const int code = app.exec();
    thread.quit();
    thread.wait();
    return code;
}
