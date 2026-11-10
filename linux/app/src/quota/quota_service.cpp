#include "quota/quota_service.hpp"

#include <QCoreApplication>

#include "agentspeed/quota/codex.hpp"
#include "agentspeed/quota/sources.hpp"
#include "preferences.hpp"
#include "quota/quota_view.hpp"
#include "remote_service.hpp"

namespace quota = agentspeed::quota;

QuotaService::QuotaService(Preferences& prefs, QuotaView& view, RemoteService& remote, std::shared_ptr<quota::CodexCollector> codex,
                           QObject* parent)
    : QObject(parent), prefs_(prefs), view_(view), remote_(remote), codex_(std::move(codex)) {
    qRegisterMetaType<QuotaPublication>();
    qRegisterMetaType<QuotaWorkerSettings>();
    thread_.setObjectName(QStringLiteral("quota"));
    // QUOTAS 5.1: the helper ships beside the app's executable.
    worker_ = new QuotaWorker(codex_, QCoreApplication::applicationDirPath() + QStringLiteral("/agent-speed-statusline"));
    worker_->moveToThread(&thread_);
    connect(&thread_, &QThread::started, worker_, &QuotaWorker::start);
    connect(&thread_, &QThread::finished, worker_, &QObject::deleteLater);
    connect(worker_, &QuotaWorker::published, &view_, &QuotaView::setPublication);
    connect(worker_, &QuotaWorker::cliEnabled, this, [this](bool on, const QString&) {
        if (on) prefs_.setQuotaSwitch(QStringLiteral("quotaClaudeCli"), true);
    });

    connect(&view_, &QuotaView::enableCliRequested, worker_, &QuotaWorker::enableCli);
    connect(&view_, &QuotaView::disableCliRequested, this, [this] {
        prefs_.setQuotaSwitch(QStringLiteral("quotaClaudeCli"), false);
        QMetaObject::invokeMethod(worker_, &QuotaWorker::disableCli, Qt::QueuedConnection);
    });
    connect(&view_, &QuotaView::zcodeKeyEntered, worker_, &QuotaWorker::saveZcodeKey);
    connect(&view_, &QuotaView::zcodeKeyRemoved, worker_, &QuotaWorker::removeZcodeKey);
    connect(&view_, &QuotaView::settingsOpened, worker_, &QuotaWorker::checkNow);

    for (auto signal : {&Preferences::quotaChanged, &Preferences::sourcesChanged, &Preferences::networkEnabledChanged}) {
        connect(&prefs_, signal, this, [this] {
            push();
            refreshInvite();
            refreshHosts();
        });
    }
    // REMOTE 11.1: the hosts' lines, from the coordinator's thread to the view.
    remote_.setQuotaSink([view = &view_](const agentspeed::remote::RelayMessage& message) {
        QMetaObject::invokeMethod(view, [view, message] { view->applyHost(message); }, Qt::QueuedConnection);
    });
    connect(&remote_, &RemoteService::hostsChanged, this, &QuotaService::refreshHosts);
    hostsTimer_.setInterval(5'000);
    connect(&hostsTimer_, &QTimer::timeout, this, &QuotaService::refreshHosts);
}

QuotaService::~QuotaService() { stop(); }

void QuotaService::start() {
    // The first tick already knows the switches: the Codex collector must not drop the seeded lines (QUOTAS 3).
    worker_->setSettings(settings());
    thread_.start();
    refreshInvite();
    refreshHosts();
    hostsTimer_.start();
}

void QuotaService::stop() {
    hostsTimer_.stop();
    remote_.setQuotaSink(nullptr);
    if (thread_.isRunning()) {
        thread_.quit();
        thread_.wait();
    }
    // The worker is deleted with its thread's end; nothing is sent to it any more (windows closing at quit).
    worker_ = nullptr;
}

void QuotaService::setDashboardOpen(bool open) {
    if (dashboardOpen_ == open) return;
    dashboardOpen_ = open;
    push();
}

QuotaWorkerSettings QuotaService::settings() const {
    QuotaWorkerSettings s;
    s.enabled = prefs_.monitorConfig().enabled;
    for (const auto& route : quota::sourceRoutes()) {
        const auto key = QString::fromUtf8(route.switchKey);
        if (prefs_.quotaSwitch(key)) s.switchesOn.insert(std::string(route.switchKey));
    }
    if (prefs_.quotaSwitch(QStringLiteral("quotaGrokBuildLive"))) s.switchesOn.insert(std::string(quota::kGrokLiveSwitch));
    s.networkEnabled = prefs_.networkEnabled();
    s.zcodeRegion = prefs_.quotaZcodeRegion();
    s.dashboardOpen = dashboardOpen_;
    return s;
}

void QuotaService::push() {
    if (!worker_ || !thread_.isRunning()) return;
    QMetaObject::invokeMethod(worker_, [worker = worker_, s = settings()] { worker->setSettings(s); }, Qt::QueuedConnection);
}

// DESIGN 4: the invitation shows while Claude Code is enabled, its bridge is off and it was not dismissed.
void QuotaService::refreshInvite() {
    const bool claudeCode = prefs_.monitorConfig().enabled.contains(agentspeed::SourceKind::ClaudeCode);
    view_.setInvite(claudeCode && !prefs_.quotaSwitch(QStringLiteral("quotaClaudeCli")) && !prefs_.quotaCliInviteDismissed());
}

void QuotaService::refreshHosts() { view_.setHosts(remote_.hostNames(), remote_.connectedHosts(), remote_.hostQuotaSources()); }
