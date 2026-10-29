#include <QApplication>
#include <QQmlApplicationEngine>
#include <QQmlComponent>
#include <QStandardPaths>
#include <QQmlContext>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QPalette>
#include <QStyle>
#include <QThread>
#include <QTimer>

#include "agentspeed/price_book.hpp"
#include "app_state.hpp"
#include "catalog.hpp"
#include "flyout.hpp"
#include "history_service.hpp"
#include "graphics.hpp"
#include "monitor_worker.hpp"
#include "network_service.hpp"
#include "orb/orb_window.hpp"
#include "preferences.hpp"
#include "report/report_controller.hpp"
#include "strings.hpp"
#include "task_card.hpp"
#include "tray_icon.hpp"

namespace {

// The settings pane between 網路流量 and 資料來源 (DESIGN 11).
constexpr int kApiPage = 5;


QPalette paletteFor(bool dark) {
    QPalette p;
    if (dark) {
        p.setColor(QPalette::Window, QColor(0x20, 0x22, 0x28));
        p.setColor(QPalette::WindowText, QColor(0xEC, 0xEF, 0xF4));
        p.setColor(QPalette::Base, QColor(0x17, 0x19, 0x1F));
        p.setColor(QPalette::AlternateBase, QColor(0x22, 0x25, 0x2C));
        p.setColor(QPalette::Text, QColor(0xEC, 0xEF, 0xF4));
        p.setColor(QPalette::Button, QColor(0x2A, 0x2D, 0x35));
        p.setColor(QPalette::ButtonText, QColor(0xEC, 0xEF, 0xF4));
        p.setColor(QPalette::Highlight, QColor(0x5B, 0x8C, 0xFF));
        p.setColor(QPalette::HighlightedText, Qt::white);
    } else {
        p.setColor(QPalette::Window, QColor(0xF4, 0xF5, 0xF8));
        p.setColor(QPalette::WindowText, QColor(0x16, 0x18, 0x1D));
        p.setColor(QPalette::Base, Qt::white);
        p.setColor(QPalette::AlternateBase, QColor(0xF2, 0xF3, 0xF7));
        p.setColor(QPalette::Text, QColor(0x16, 0x18, 0x1D));
        p.setColor(QPalette::Button, QColor(0xEE, 0xEF, 0xF3));
        p.setColor(QPalette::ButtonText, QColor(0x16, 0x18, 0x1D));
        p.setColor(QPalette::Highlight, QColor(0x2F, 0x6B, 0xFF));
        p.setColor(QPalette::HighlightedText, Qt::white);
    }
    return p;
}

// DESIGN 0: Auto follows the desktop; Light and Dark override it for every window.
void applyAppearance(const QString& choice) {
    if (choice == QStringLiteral("dark") || choice == QStringLiteral("light")) {
        QGuiApplication::setPalette(paletteFor(choice == QStringLiteral("dark")));
    } else {
        QGuiApplication::setPalette(QPalette());
    }
}

WorkerOptions optionsFrom(const Preferences& prefs, const Catalog& catalog, const Strings& strings,
                          const std::shared_ptr<agentspeed::pricing::PriceBook>& prices) {
    WorkerOptions options;
    options.config = prefs.monitorConfig();
    options.config.prices = prices;
    options.currency = prefs.currency();
    options.rate = catalog.rateFor(options.currency);
    options.subscriptions = prefs.subscriptions();
    options.includeSubscriptions = prefs.includeSubscriptions();
    options.language = strings.language();
    options.trafficEnabled = prefs.trafficEnabled();
    options.trafficIncludeTools = prefs.trafficIncludeTools();
    options.trafficBits = prefs.trafficUnit() == QLatin1String("bits");
    return options;
}

}  // namespace

int main(int argc, char* argv[]) {
    // The flyout (and later the orbs) need global positions and keep-above, which Wayland does not give clients:
    // run on XWayland whenever it is there.
    if (qEnvironmentVariableIsEmpty("QT_QPA_PLATFORM") && !qEnvironmentVariableIsEmpty("DISPLAY")) {
        qputenv("QT_QPA_PLATFORM", "xcb");
    }
    QApplication app(argc, argv);
    QApplication::setApplicationName(QStringLiteral("Agent Speed"));
    QApplication::setApplicationVersion(QStringLiteral(AGENTSPEED_VERSION));
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
    if (app.arguments().contains(QStringLiteral("--dark"))) snapshotDark = true;
    Preferences prefs;
    applyAppearance(snapshotDark ? QStringLiteral("dark") : prefs.appearance());
    QObject::connect(&prefs, &Preferences::appearanceChanged, &app, [&] { applyAppearance(prefs.appearance()); });
    Strings strings;
    strings.setChoice(prefs.language());
    QObject::connect(&prefs, &Preferences::languageChanged, &app, [&] { strings.setChoice(prefs.language()); });
    AppState state(demo);
    auto prices = std::make_shared<agentspeed::pricing::PriceBook>();
    Catalog catalog(strings, prices);

    QThread thread;
    thread.setObjectName(QStringLiteral("monitor"));
    auto* worker = new MonitorWorker(&strings, demo, optionsFrom(prefs, catalog, strings, prices));
    const auto pushOptions = [&](bool reconfigure) { worker->setOptions(optionsFrom(prefs, catalog, strings, prices), reconfigure); };
    QObject::connect(&prefs, &Preferences::sourcesChanged, &app, [=] { pushOptions(true); });
    for (auto signal : {&Preferences::currencyChanged, &Preferences::subscriptionsChanged, &Preferences::includeSubscriptionsChanged,
                        &Preferences::trafficChanged}) {
        QObject::connect(&prefs, signal, &app, [=] { pushOptions(false); });
    }
    QObject::connect(&strings, &Strings::changed, &app, [=] { pushOptions(false); });
    worker->moveToThread(&thread);
    QObject::connect(&thread, &QThread::started, worker, &MonitorWorker::start);
    QObject::connect(&thread, &QThread::finished, worker, &QObject::deleteLater);
    QObject::connect(worker, &MonitorWorker::snapshotReady, &state, &AppState::applySnapshot);
    QObject::connect(&state, &AppState::demoChanged, worker, &MonitorWorker::setDemo);

    // The daily history (METRICS 8) on its own thread, sharing the price book with the monitor.
    qRegisterMetaType<TaskRecords>();
    qRegisterMetaType<TaskModels>();
    HistoryModel historyModel(&strings);
    historyModel.setDisabledSources(prefs.disabledSources());
    QObject::connect(&prefs, &Preferences::sourcesChanged, &historyModel, [&] { historyModel.setDisabledSources(prefs.disabledSources()); });
    QThread historyThread;
    historyThread.setObjectName(QStringLiteral("history"));
    auto* historyWorker = new HistoryWorker(
        prices, QStandardPaths::writableLocation(QStandardPaths::AppDataLocation) + QStringLiteral("/history.sqlite"));
    historyWorker->moveToThread(&historyThread);
    const auto pushHistoryScope = [&] {
        const auto config = prefs.monitorConfig();
        historyWorker->setScope(config.locations, config.enabled);
    };
    pushHistoryScope();
    QObject::connect(&prefs, &Preferences::sourcesChanged, &app, pushHistoryScope);
    QObject::connect(&historyThread, &QThread::started, historyWorker, &HistoryWorker::start);
    QObject::connect(&historyThread, &QThread::finished, historyWorker, &QObject::deleteLater);
    QObject::connect(historyWorker, &HistoryWorker::daysReady, &historyModel, &HistoryModel::applyRows);
    QObject::connect(historyWorker, &HistoryWorker::analyzing, &historyModel, &HistoryModel::setAnalyzing);
    QObject::connect(historyWorker, &HistoryWorker::tasksReady, &historyModel, &HistoryModel::applyTasks);
    qRegisterMetaType<TrafficRows>();
    QObject::connect(worker, &MonitorWorker::trafficSave, historyWorker, &HistoryWorker::saveTraffic);
    QObject::connect(
        historyWorker, &HistoryWorker::trafficRestored, historyWorker, [worker](TrafficRows rows) { worker->restoreTraffic(std::move(rows)); },
        Qt::DirectConnection);
    QObject::connect(historyWorker, &HistoryWorker::trafficReady, &historyModel, &HistoryModel::applyTraffic);
    QObject::connect(
        historyWorker, &HistoryWorker::tasksReady, historyWorker, [worker](TaskRecords, TaskModels models) { worker->setTaskModels(std::move(models)); },
        Qt::DirectConnection);
    QObject::connect(&historyModel, &HistoryModel::refreshRequested, historyWorker, &HistoryWorker::analyze);
    QObject::connect(&historyModel, &HistoryModel::clearRequested, historyWorker, &HistoryWorker::clear);
    // Peaks are stored only for days observed live (METRICS 8).
    double recordedPeak = 0;
    QString recordedDay;
    QObject::connect(&state, &AppState::snapshotChanged, &app, [&] {
        const auto snap = state.snapshot();
        if (snap.value(QStringLiteral("demo")).toBool()) return;
        const auto day = QDate::currentDate().toString(Qt::ISODate);
        const double peak = snap.value(QStringLiteral("peak")).toDouble();
        if (day != recordedDay) {
            recordedDay = day;
            recordedPeak = 0;
        }
        if (peak > recordedPeak + 0.5) {
            recordedPeak = peak;
            QMetaObject::invokeMethod(historyWorker, "recordPeak", Qt::QueuedConnection, Q_ARG(QString, day), Q_ARG(double, peak));
        }
    });

    TrayIcon tray(strings, demo);
    QObject::connect(&strings, &Strings::changed, &tray, &TrayIcon::retranslate);
    orb::OrbWindow orbWindow(strings);

    QQmlApplicationEngine engine;
    const bool popup = tray.available() || !snapshotPath.isEmpty();
    engine.setInitialProperties({{QStringLiteral("flyout"), popup}});
    engine.rootContext()->setContextProperty(QStringLiteral("S"), &strings);
    engine.rootContext()->setContextProperty(QStringLiteral("appState"), &state);
    engine.rootContext()->setContextProperty(QStringLiteral("softwareRendering"), softwareRendering);
    engine.rootContext()->setContextProperty(QStringLiteral("prefs"), &prefs);
    engine.rootContext()->setContextProperty(QStringLiteral("catalog"), &catalog);
    engine.rootContext()->setContextProperty(QStringLiteral("orb"), &orbWindow);
    engine.rootContext()->setContextProperty(QStringLiteral("history"), &historyModel);
    // NETWORK 1–4 and UPDATES: price sync, exchange rates and the update check, under the master switch.
    NetworkService network(prefs, strings, catalog, prices, QStandardPaths::writableLocation(QStandardPaths::AppDataLocation));
    engine.rootContext()->setContextProperty(QStringLiteral("network"), &network);
    QObject::connect(&network, &NetworkService::tableAdded, &app, [&] {
        worker->requestReprice();
        QMetaObject::invokeMethod(historyWorker, "analyze", Qt::QueuedConnection);
    });
    QObject::connect(&catalog, &Catalog::changed, &app, [=] { pushOptions(false); });
    // DESIGN 11: live reporting, fed by every live engine tick.
    qRegisterMetaType<ReportTick>();
    ReportController report(prefs, strings, prices, QStandardPaths::writableLocation(QStandardPaths::AppDataLocation));
    engine.rootContext()->setContextProperty(QStringLiteral("report"), &report);
    QObject::connect(worker, &MonitorWorker::reportTick, &report, &ReportController::onTick);
    engine.loadFromModule(QStringLiteral("AgentSpeed"), QStringLiteral("Dashboard"));
    if (engine.rootObjects().isEmpty()) return 1;
    auto* window = qobject_cast<QQuickWindow*>(engine.rootObjects().constFirst());
    QQmlComponent settingsComponent(&engine, QStringLiteral("AgentSpeed"), QStringLiteral("SettingsWindow"));
    QObject* settingsWindow = nullptr;
    const auto openSettings = [&](int page) {
        if (!settingsWindow) {
            settingsWindow = settingsComponent.create(engine.rootContext());
            if (!settingsWindow) {
                qWarning() << settingsComponent.errorString();
                return;
            }
        }
        QMetaObject::invokeMethod(settingsWindow, "present", Q_ARG(QVariant, page));
    };
    QObject::connect(window, SIGNAL(settingsRequested()), &tray, SIGNAL(settingsRequested()));
    QQmlComponent historyComponent(&engine, QStringLiteral("AgentSpeed"), QStringLiteral("HistoryWindow"));
    QObject* historyWindow = nullptr;
    const auto openHistory = [&](const QString& source) {
        if (!historyWindow) {
            historyWindow = historyComponent.create(engine.rootContext());
            if (!historyWindow) {
                qWarning() << historyComponent.errorString();
                return;
            }
        }
        QMetaObject::invokeMethod(historyWindow, "present", Q_ARG(QVariant, source));
    };
    QObject::connect(window, SIGNAL(historyRequested()), &tray, SIGNAL(historyRequested()));
    QObject::connect(&tray, &TrayIcon::historyRequested, &app, [&] { openHistory(QString()); });
    QObject::connect(&state, &AppState::historyRequested, &app, [&](const QString& source) { openHistory(source); });
    QObject::connect(&tray, &TrayIcon::settingsRequested, &app, [&] { openSettings(-1); });
    QObject::connect(&report, &ReportController::settingsRequested, &app, [&] { openSettings(kApiPage); });
    QObject::connect(&tray, &TrayIcon::reportSettings, &app, [&] { openSettings(kApiPage); });
    QObject::connect(&tray, &TrayIcon::reportPause, &report, &ReportController::pause);
    QObject::connect(&tray, &TrayIcon::reportResume, &report, &ReportController::resume);
    QObject::connect(&tray, &TrayIcon::reportLeaderboard, &report, [&] { report.openLink(report.leaderboardUrl()); });
    QObject::connect(&report, &ReportController::changed, &tray, [&] {
        tray.setReport(report.menuVisible(), report.menuText(), report.paused(), !report.leaderboardUrl().isEmpty());
    });
    // The preview and the consent sheet are windows of their own (DESIGN 11).
    QQmlComponent previewComponent(&engine, QStringLiteral("AgentSpeed"), QStringLiteral("ReportPreviewWindow"));
    QQmlComponent consentComponent(&engine, QStringLiteral("AgentSpeed"), QStringLiteral("ReportConsentWindow"));
    QQuickWindow* previewWindow = nullptr;
    QQuickWindow* consentWindow = nullptr;
    const auto present = [&](QQmlComponent& component, QQuickWindow*& slot) {
        if (!slot) {
            slot = qobject_cast<QQuickWindow*>(component.create(engine.rootContext()));
            if (!slot) {
                qWarning() << component.errorString();
                return;
            }
        }
        slot->show();
        slot->raise();
        slot->requestActivate();
    };
    QObject::connect(&report, &ReportController::previewRequested, &app, [&] { present(previewComponent, previewWindow); });
    QObject::connect(&report, &ReportController::consentRequested, &app, [&] { present(consentComponent, consentWindow); });
    // The update window (UPDATES 4), from the tray item, the notification, the dashboard pill or Settings.
    QQmlComponent updateComponent(&engine, QStringLiteral("AgentSpeed"), QStringLiteral("UpdateWindow"));
    QQuickWindow* updateWindow = nullptr;
    const auto openUpdate = [&] {
        if (!updateWindow) {
            updateWindow = qobject_cast<QQuickWindow*>(updateComponent.create(engine.rootContext()));
            if (!updateWindow) {
                qWarning() << updateComponent.errorString();
                return;
            }
        }
        updateWindow->show();
        updateWindow->raise();
        updateWindow->requestActivate();
    };
    QObject::connect(&network, &NetworkService::updateWindowRequested, &app, openUpdate);
    QObject::connect(&tray, &TrayIcon::updateRequested, &app, openUpdate);
    QObject::connect(&network, &NetworkService::updateChanged, &tray, [&] {
        tray.setUpdate(network.updateState() == QLatin1String("available") ? network.availableVersion() : QString());
    });
    QObject::connect(&network, &NetworkService::updateFound, &tray, [&](const QString& version) {
        tray.notify(task_card::fmt(strings, QStringLiteral("update.available.title"), {version}), strings.t(QStringLiteral("update.notification.body")));
    });
    Flyout flyout(window, popup);
    QObject::connect(&state, &AppState::snapshotChanged, &tray, [&] {
        const auto snap = state.snapshot();
        const auto live = snap.value(QStringLiteral("liveText")).toString();
        QString tip = strings.t(QStringLiteral("app.name")) + QStringLiteral(" — ") + live + QLatin1Char(' ') +
                      strings.t(QStringLiteral("unit.tokS"));
        // DESIGN 10: the taskbar tooltip adds the highest 智力 and the project that supplies it, then the caption.
        const auto sets = snap.value(QStringLiteral("scoreSets")).toList();
        if (!sets.isEmpty()) {
            const auto top = sets.front().toMap();
            if (const int shown = top.value(QStringLiteral("shown"), -1).toInt(); shown >= 0) {
                tip += QLatin1Char('\n') + task_card::fmt(strings, QStringLiteral("task.tooltip.top"),
                                                          {QString::number(shown), top.value(QStringLiteral("project")).toString()});
                tip += QLatin1Char('\n') + strings.t(QStringLiteral("intel.disclaimer"));
            }
        }
        tray.update(snap.value(QStringLiteral("liveOutput")).toDouble(), snap.value(QStringLiteral("compactText")).toString(),
                    tip);
    });
    QObject::connect(&tray, &TrayIcon::demoToggled, &state, &AppState::setDemo);
    QObject::connect(&state, &AppState::demoChanged, &tray, &TrayIcon::setDemo);
    // Quitting sends a leaving report when it can go out within 1 s (METRICS 11.5).
    const auto quitApp = [&] { report.quitApp(); };
    QObject::connect(&tray, &TrayIcon::quitRequested, &app, quitApp);
    QObject::connect(&tray, &TrayIcon::toggleDashboard, &flyout, &Flyout::toggle);

    QObject::connect(&state, &AppState::snapshotChanged, &orbWindow, [&] { orbWindow.applySnapshot(state.snapshot()); });
    QObject::connect(&orbWindow, &orb::OrbWindow::dashboardRequested, &flyout, &Flyout::toggle);
    QObject::connect(&orbWindow, &orb::OrbWindow::quitRequested, &app, quitApp);
    QObject::connect(&orbWindow, &orb::OrbWindow::settingsRequested, &app, [&] { openSettings(1); });
    QObject::connect(&orbWindow, &orb::OrbWindow::orbVisibilityChanged, &tray, &TrayIcon::setOrbVisible);
    QObject::connect(&orbWindow, &orb::OrbWindow::clickThroughChanged, &tray, &TrayIcon::setClickThrough);
    QObject::connect(&tray, &TrayIcon::orbToggled, &orbWindow, &orb::OrbWindow::setOrbVisible);
    QObject::connect(&tray, &TrayIcon::clickThroughToggled, &orbWindow, &orb::OrbWindow::setClickThrough);
    tray.setClickThrough(orbWindow.clickThrough());

    tray.setOrbVisible(false);
    const auto arguments = app.arguments();
    network.start();
    report.start();
    // Development aid: --report-selftest=<http://127.0.0.1:port> reports to a local reference server.
    for (const auto& arg : arguments) {
        if (arg.startsWith(QStringLiteral("--report-selftest=")) && !report.startSelfTest(arg.mid(18))) qWarning("report selftest: loopback http only");
    }
    // After a one-click update the new instance says so once (UPDATES 4).
    if (const auto at = arguments.indexOf(QStringLiteral("--after-update")); at >= 0) {
        QTimer::singleShot(1500, &tray, [&] {
            tray.notify(strings.t(QStringLiteral("app.name")),
                        task_card::fmt(strings, QStringLiteral("update.done"), {QCoreApplication::applicationVersion()}));
        });
    }
    const bool offscreenShot = !snapshotPath.isEmpty() ||
                               std::any_of(arguments.begin(), arguments.end(),
                                           [](const QString& a) {
                                               return a.startsWith(QStringLiteral("--settings-snapshot=")) ||
                                                      a.startsWith(QStringLiteral("--history-snapshot=")) ||
                                                      a.startsWith(QStringLiteral("--card-snapshot="));
                                           });
    if (!offscreenShot) orbWindow.restore();
    // Development aid: --orb-traffic=<mode> sets the orb's readout (sources, machine, intelligence, off).
    for (const auto& arg : arguments) {
        if (arg.startsWith(QStringLiteral("--orb-traffic="))) orbWindow.setTraffic(arg.mid(14));
    }
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
    // Development aid: --settings[=page] opens the settings window; --settings-snapshot=<file.png> saves it and quits;
    // --history[=source] opens the history window, --history-snapshot=<file.png> saves it after the first analysis.
    QString settingsShot, historyShot;
    for (const auto& arg : app.arguments()) {
        if (arg.startsWith(QStringLiteral("--history-snapshot="))) {
            historyShot = arg.mid(19);
        } else if (arg.startsWith(QStringLiteral("--history"))) {
            openHistory(arg.contains(QLatin1Char('=')) ? arg.section(QLatin1Char('='), 1) : QString());
        } else if (arg.startsWith(QStringLiteral("--settings-snapshot="))) {
            settingsShot = arg.mid(20);
        } else if (arg.startsWith(QStringLiteral("--settings"))) {
            openSettings(arg.contains(QLatin1Char('=')) ? arg.section(QLatin1Char('='), 1).toInt() : 0);
        }
    }
    if (!historyShot.isEmpty()) {
        if (!historyWindow) openHistory(QString());
        QTimer::singleShot(12000, &app, [&] {
            if (auto* quick = qobject_cast<QQuickWindow*>(historyWindow)) {
                // AGENT_SPEED_SHOT_HEIGHT renders a taller window, so the sections below the fold are in the image.
                if (const int tall = qEnvironmentVariableIntValue("AGENT_SPEED_SHOT_HEIGHT"); tall > 0) {
                    quick->resize(quick->width(), tall);
                    QCoreApplication::processEvents();
                }
                quick->grabWindow().save(historyShot);
            }
            QApplication::quit();
        });
    }
    if (!settingsShot.isEmpty()) {
        if (!settingsWindow) openSettings(0);
        QTimer::singleShot(3500, &app, [&] {
            if (auto* quick = qobject_cast<QQuickWindow*>(settingsWindow)) quick->grabWindow().save(settingsShot);
            QApplication::quit();
        });
    }
    if (!snapshotPath.isEmpty()) {
        QTimer::singleShot(3000, &app, [&] {
            window->grabWindow().save(snapshotPath);
            QApplication::quit();
        });
    }
    // Development aid: --card-snapshot=<file.png>[:row] opens the score card of a dashboard row, saves it and quits.
    for (const auto& arg : arguments) {
        if (!arg.startsWith(QStringLiteral("--card-snapshot="))) continue;
        auto spec = arg.mid(16);
        int row = 0;
        if (const auto colon = spec.lastIndexOf(QLatin1Char(':')); colon > 0) {
            row = spec.mid(colon + 1).toInt();
            spec = spec.left(colon);
        }
        QTimer::singleShot(2500, &app, [&, row] {
            window->show();
            QMetaObject::invokeMethod(window, "showCardFor", Q_ARG(QVariant, row));
        });
        QTimer::singleShot(3500, &app, [&, spec] {
            if (auto* card = qobject_cast<QQuickWindow*>(window->property("card").value<QObject*>())) card->grabWindow().save(spec);
            QApplication::quit();
        });
    }

    thread.start();
    historyThread.start();
    const int code = app.exec();
    // Today's traffic is saved at quit (METRICS 10.9), before both threads stop.
    TrafficRows lastTraffic;
    QMetaObject::invokeMethod(worker, [&] { lastTraffic = worker->trafficToday(); }, Qt::BlockingQueuedConnection);
    QMetaObject::invokeMethod(historyWorker, [&] { historyWorker->saveTraffic(lastTraffic); }, Qt::BlockingQueuedConnection);
    historyWorker->cancel();
    historyThread.quit();
    historyThread.wait();
    thread.quit();
    thread.wait();
    return code;
}
