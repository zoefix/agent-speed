#pragma once

#include <QObject>
#include <QThread>
#include <QTimer>
#include <memory>

#include "quota/quota_worker.hpp"

namespace agentspeed::quota {
class CodexCollector;
}
class Preferences;
class QuotaView;
class RemoteService;

// Keeps the quota worker (its own thread) in step with the settings (QUOTAS 9.1: switches, enabled sources, the
// network) and the dashboard, and hands what it reads to the view; the view's Settings actions go back to it. The
// hosts' quota lines and connections reach the view from the remote service.
class QuotaService : public QObject {
    Q_OBJECT
public:
    QuotaService(Preferences& prefs, QuotaView& view, RemoteService& remote, std::shared_ptr<agentspeed::quota::CodexCollector> codex,
                 QObject* parent = nullptr);
    ~QuotaService() override;

    // The collector the monitor's Codex parsers feed (QUOTAS 3).
    std::shared_ptr<agentspeed::quota::CodexCollector> codexCollector() const { return codex_; }
    void start();
    void stop();
    // While the dashboard is open the vendors may be asked even when their agent is not running (QUOTAS 9.1).
    void setDashboardOpen(bool open);

private:
    QuotaWorkerSettings settings() const;
    void push();
    void refreshHosts();
    void refreshInvite();

    Preferences& prefs_;
    QuotaView& view_;
    RemoteService& remote_;
    std::shared_ptr<agentspeed::quota::CodexCollector> codex_;
    QThread thread_;
    QuotaWorker* worker_ = nullptr;
    QTimer hostsTimer_;
    bool dashboardOpen_ = false;
};
