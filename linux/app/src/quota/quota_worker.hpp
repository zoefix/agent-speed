#pragma once

#include <QObject>
#include <QString>
#include <QTimer>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "agentspeed/model.hpp"
#include "quota/quota_view.hpp"

namespace agentspeed::quota {
class CodexCollector;
class RunningSources;
class SignInMarker;
class VendorService;
namespace bridge {
class Bridge;
}
namespace cli {
class FileReader;
}
namespace desktop {
class Reader;
}
}  // namespace agentspeed::quota
namespace agentspeed::traffic::sampler {
class ProcessReader;
}
class QuotaTransport;
class SecretService;

// What the readers may do (QUOTAS 9.1): only enabled sources whose quota switch is on are read, vendor routes only
// while the network is on.
struct QuotaWorkerSettings {
    std::set<agentspeed::SourceKind> enabled;
    std::set<std::string, std::less<>> switchesOn;
    bool networkEnabled = false;
    QString zcodeRegion = QStringLiteral("global");
    bool dashboardOpen = false;
    bool operator==(const QuotaWorkerSettings&) const = default;
};
Q_DECLARE_METATYPE(QuotaWorkerSettings)


// Reads every quota route on its own thread (QUOTAS 3–9): the Codex collector the monitor feeds, Claude Desktop's
// cache, the Claude CLI quota file and its bridge, the signed-in markers, the running agents, and the phase 2 vendor
// service with the Qt transport and the Secret Service. One tick a second runs what is due.
class QuotaWorker : public QObject {
    Q_OBJECT
public:
    QuotaWorker(std::shared_ptr<agentspeed::quota::CodexCollector> codex, QString helperSource);
    ~QuotaWorker() override;

public slots:
    void start();
    void setSettings(QuotaWorkerSettings settings);
    // QUOTAS 5.2 / 5.3.
    void enableCli();
    void disableCli();
    // QUOTAS 9.8: the key goes to the secret store and is dropped from memory.
    void saveZcodeKey(QString key);
    void removeZcodeKey();
    // Settings opened: the bridge's status and the local files are checked now.
    void checkNow();

signals:
    void published(QuotaPublication publication);
    // The bridge was turned on (QUOTAS 5.2): the switch follows; or it failed with that state.
    void cliEnabled(bool on, QString failure);
    void cliDisabled();

private:
    void tick();
    bool on(std::string_view key) const;
    bool enabled(agentspeed::SourceKind source) const;
    void refreshBridge(std::int64_t nowMs);
    void publish(std::int64_t nowMs);

    std::shared_ptr<agentspeed::quota::CodexCollector> codex_;
    QString helperSource_;
    QuotaWorkerSettings settings_;
    QTimer* timer_ = nullptr;
    std::unique_ptr<QuotaTransport> transport_;
    std::unique_ptr<SecretService> secrets_;
    std::unique_ptr<agentspeed::quota::VendorService> vendors_;
    std::unique_ptr<agentspeed::quota::desktop::Reader> desktop_;
    std::unique_ptr<agentspeed::quota::cli::FileReader> cliFile_;
    std::unique_ptr<agentspeed::quota::bridge::Bridge> bridge_;
    std::unique_ptr<agentspeed::quota::SignInMarker> codexMarker_, claudeMarker_;
    std::unique_ptr<agentspeed::quota::RunningSources> running_;
    std::unique_ptr<agentspeed::traffic::sampler::ProcessReader> processes_;
    std::optional<std::int64_t> listedMs_, cliCheckedMs_, bridgeCheckedMs_;
    QString cliState_ = QStringLiteral("off");
    QString cliFailure_;
    bool zcodeKeySaved_ = false;
    bool zcodeKeyKnown_ = false;
    bool readLocalNow_ = false;
    bool helperInstalled_ = false;
    std::optional<QuotaPublication> last_;
};
