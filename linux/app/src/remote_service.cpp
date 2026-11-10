#include "remote_service.hpp"

#include <QCoreApplication>
#include <QNetworkInformation>
#include <QTimeZone>

#include "agentspeed/host_selection.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/remote_relay_bundle.hpp"
#include "agentspeed/remote_ssh.hpp"
#include "board/board_store.hpp"
#include "history_service.hpp"
#include "orbs/orbs_store.hpp"
#include "preferences.hpp"
#include "agentspeed/quota/sources.hpp"

namespace {

namespace remote = agentspeed::remote;

std::string utf8(const QString& text) { return text.toStdString(); }
QString qt(const std::string& text) { return QString::fromStdString(text); }

std::optional<std::string> optionalText(const QString& text) {
    const auto trimmed = text.trimmed();
    return trimmed.isEmpty() ? std::nullopt : std::optional<std::string>(utf8(trimmed));
}

QString issueText(const std::optional<remote::FieldIssue>& issue) { return issue ? qt(std::string(remote::fieldIssueName(*issue))) : QString(); }

// The zone id the connections send and the history compares (REMOTE 5.3, 8.3).
QString systemZoneId() { return QString::fromUtf8(QTimeZone::systemTimeZoneId()); }

QStringList removing(const QStringList& ids, const QString& id) {
    std::vector<std::string> list;
    for (const auto& i : ids) list.push_back(utf8(i));
    const auto selection = agentspeed::hosts::HostSelection::normalized(list).removing(utf8(id));
    QStringList out;
    for (const auto& i : selection.ids()) out.push_back(qt(i));
    return out;
}

}  // namespace

RemoteService::RemoteService(Preferences& prefs, std::shared_ptr<agentspeed::pricing::PriceBook> prices, QObject* parent)
    : QObject(parent), prefs_(prefs), prices_(std::move(prices)) {
    const auto environment = remote::ssh::processEnvironment();
    const auto folder = remote::RelayBundle::locate(environment, utf8(QCoreApplication::applicationFilePath()));
    auto bundle = std::make_shared<remote::RelayBundle>(folder, utf8(QCoreApplication::applicationVersion()));
    relaysBundled_ = bundle->executable(remote::Arch::X86_64) || bundle->executable(remote::Arch::Aarch64);
    if (!relaysBundled_) qWarning("remote: no relay executables found; hosts will show uploadFailed");
    remote::Coordinator::Options options;
    options.feed = feed_;
    options.relays = bundle;
    options.zone = [] { return remote::currentZone(utf8(systemZoneId())); };
    options.processEnvironment = environment;
    coordinator_ = std::make_unique<remote::Coordinator>(std::move(options));
    load();
    connect(&prefs_, &Preferences::remoteHostsChanged, this, [this] {
        load();
        apply();
    });
    connect(&prefs_, &Preferences::networkEnabledChanged, this, &RemoteService::apply);
    connect(&prefs_, &Preferences::sourcesChanged, this, &RemoteService::apply);
    connect(&prefs_, &Preferences::quotaChanged, this, &RemoteService::apply);
    coordinator_->setQuotaSink([this](const remote::HostEvent& event) {
        const auto* message = std::get_if<remote::HostEvent::Message>(&event.kind);
        if (!message) return;
        if (const auto* bridge = std::get_if<remote::HostQuotaBridge>(&message->message);
            bridge && bridge->state == remote::HostQuotaBridge::State::Off) {
            QMetaObject::invokeMethod(this, [this, id = qt(bridge->hostId)] {
                if (pendingRemovals_.contains(id)) finishRemoval(id);
            }, Qt::QueuedConnection);
        }
        std::function<void(const remote::RelayMessage&)> sink;
        {
            std::lock_guard lock(sinkMutex_);
            sink = quotaSink_;
        }
        if (sink) sink(message->message);
    });
    watchSystem();
    apply();
}

RemoteService::~RemoteService() { stop(); }

void RemoteService::stop() {
    if (coordinator_) coordinator_->stop();
}

void RemoteService::attachHistory(HistoryWorker* worker) {
    history_ = worker;
    if (!worker) return;
    coordinator_->setHistorySink([worker](const remote::HostEvent& event) { worker->receiveRemote(event); });
    auto* coordinator = coordinator_.get();
    worker->setRemoteRequester([coordinator](const std::string& host, const std::map<std::string, std::string>& digests) {
        coordinator->requestHistory(host, digests);
    });
    connect(worker, &HistoryWorker::remoteTooLarge, this, [this](const QStringList& hosts) {
        std::set<std::string> ids;
        for (const auto& h : hosts) ids.insert(utf8(h));
        coordinator_->setHistoryTooLarge(std::move(ids));
    });
    apply();
}

void RemoteService::setSelectionStores(OrbsStore* orbs, BoardStore* boards) {
    orbs_ = orbs;
    boards_ = boards;
}

void RemoteService::setWatch(bool on) { coordinator_->setWatch(on); }

void RemoteService::load() {
    const auto text = prefs_.remoteHosts();
    if (text.isEmpty()) {
        document_ = {};
    } else if (auto parsed = remote::HostsDocument::parse(utf8(text))) {
        document_ = std::move(*parsed);
    } else {
        qWarning("remote: the remoteHosts setting is not a hosts document; starting from none");
        document_ = {};
    }
    emit hostsChanged();
}

void RemoteService::save() {
    prefs_.setRemoteHosts(qt(document_.json()));
    emit hostsChanged();
    apply();
}

void RemoteService::setQuotaSink(std::function<void(const remote::RelayMessage&)> sink) {
    std::lock_guard lock(sinkMutex_);
    quotaSink_ = std::move(sink);
}

// REMOTE 11.1: the sources whose quota switch is on and that are enabled (QUOTAS 9.1); each host adds its bridge.
remote::QuotaSwitches RemoteService::quotaSwitches() const {
    remote::QuotaSwitches out;
    const auto enabled = prefs_.monitorConfig().enabled;
    for (const auto& route : agentspeed::quota::sourceRoutes()) {
        if (!enabled.contains(route.source) || !remote::quotas::hostReads(route.source)) continue;
        if (route.source == agentspeed::SourceKind::ClaudeCode || prefs_.quotaSwitch(QString::fromUtf8(route.switchKey))) out.sources.insert(route.source);
    }
    out.network = prefs_.networkEnabled();
    out.grokLive = prefs_.quotaSwitch(QStringLiteral("quotaGrokBuildLive"));
    out.claudeCode = enabled.contains(agentspeed::SourceKind::ClaudeCode);
    return out;
}

void RemoteService::apply() {
    const auto table = prices_ ? prices_->current() : agentspeed::pricing::PriceBook::bundled();
    coordinator_->update(document_, prefs_.networkEnabled(), prefs_.monitorConfig().enabled, table ? table->data() : std::string(),
                         quotaSwitches());
    if (history_) history_->setRemoteHosts(document_.displayNames());
}

// The computer's network coming back retries at once (REMOTE 4.4); a suspend is noticed by the coordinator itself.
void RemoteService::watchSystem() {
    zoneId_ = systemZoneId();
    zoneTimer_.setInterval(60'000);
    connect(&zoneTimer_, &QTimer::timeout, this, [this] {
        const auto id = systemZoneId();
        if (id == zoneId_) return;
        zoneId_ = id;
        coordinator_->noteTimeZoneChange();
    });
    zoneTimer_.start();
    if (!QNetworkInformation::loadDefaultBackend() || !QNetworkInformation::instance()) return;
    auto* info = QNetworkInformation::instance();
    connect(info, &QNetworkInformation::reachabilityChanged, this,
            [this, previous = info->reachability()](QNetworkInformation::Reachability now) mutable {
                const bool cameBack = now == QNetworkInformation::Reachability::Online && previous != now;
                previous = now;
                if (cameBack) coordinator_->noteNetworkAvailable();
            });
}

QVariantList RemoteService::hosts() const {
    QVariantList out;
    const auto names = document_.displayNames();
    for (const auto& h : document_.hosts()) {
        QVariantMap row;
        row.insert(QStringLiteral("id"), qt(h.id));
        row.insert(QStringLiteral("destination"), qt(h.destination));
        row.insert(QStringLiteral("name"), h.name ? qt(*h.name) : QString());
        row.insert(QStringLiteral("displayName"), names.contains(h.id) ? qt(names.at(h.id)) : qt(h.automaticName()));
        row.insert(QStringLiteral("automaticName"), qt(h.automaticName()));
        row.insert(QStringLiteral("enabled"), h.enabled);
        row.insert(QStringLiteral("relayFolder"), h.relayFolder ? qt(*h.relayFolder) : QString());
        row.insert(QStringLiteral("effectiveRelayFolder"), qt(h.effectiveRelayFolder()));
        row.insert(QStringLiteral("connectable"), h.connectable());
        out.push_back(row);
    }
    return out;
}

QString RemoteService::destinationIssue(const QString& destination) const {
    return issueText(remote::validation::destinationIssue(utf8(destination)));
}

QString RemoteService::folderIssue(const QString& folder) const {
    return folder.trimmed().isEmpty() ? QString() : issueText(remote::validation::folderIssue(utf8(folder.trimmed())));
}

QString RemoteService::automaticName(const QString& destination) const { return qt(remote::validation::automaticName(utf8(destination))); }

QString RemoteService::addHost(const QString& destination, const QString& name, const QString& relayFolder) {
    if (!destinationIssue(destination).isEmpty() || !folderIssue(relayFolder).isEmpty()) return {};
    auto host = remote::Host::make(remote::validation::newId(), utf8(destination), optionalText(name), true, optionalText(relayFolder));
    const auto id = qt(host.id);
    if (!document_.add(std::move(host))) return {};
    save();
    return id;
}

bool RemoteService::updateHost(const QString& id, const QString& destination, const QString& name, const QString& relayFolder) {
    const auto* current = document_.find(utf8(id));
    if (!current || !destinationIssue(destination).isEmpty() || !folderIssue(relayFolder).isEmpty()) return false;
    auto host = remote::Host::make(current->id, utf8(destination), optionalText(name), current->enabled, optionalText(relayFolder));
    host.extra = current->extra;
    if (!document_.update(std::move(host))) return false;
    save();
    // An edit retries a host that needs the user at once (REMOTE 4.4); a changed destination reconnects anyway.
    coordinator_->retryNow(utf8(id));
    return true;
}

void RemoteService::setHostEnabled(const QString& id, bool enabled) {
    const auto* current = document_.find(utf8(id));
    if (!current || current->enabled == enabled) return;
    auto host = *current;
    host.enabled = enabled;
    document_.update(std::move(host));
    save();
}

void RemoteService::setHostQuotaBridge(const QString& id, bool on) {
    const auto* found = document_.find(utf8(id));
    if (!found || found->quotaClaudeCli == on) return;
    auto host = *found;
    host.quotaClaudeCli = on;
    document_.update(std::move(host));
    save();
}

std::map<QString, QString> RemoteService::hostNames() const {
    std::map<QString, QString> out;
    for (const auto& [id, name] : document_.displayNames()) out[qt(id)] = qt(name);
    return out;
}

QSet<QString> RemoteService::connectedHosts() const {
    QSet<QString> out;
    for (const auto& id : coordinator_->connectedHostIds()) out.insert(qt(id));
    return out;
}

std::map<QString, QSet<QString>> RemoteService::hostQuotaSources() const {
    std::map<QString, QSet<QString>> out;
    const auto switches = quotaSwitches();
    for (const auto& host : document_.hosts()) {
        auto& set = out[qt(host.id)];
        for (const auto source : remote::HostQuotaConfig::forHost(host.quotaClaudeCli, switches).sources) {
            set.insert(QString::fromUtf8(agentspeed::sourceId(source)));
        }
    }
    return out;
}

bool RemoteService::hostQuotaBridge(const QString& id) const {
    const auto* host = document_.find(utf8(id));
    return host && host->quotaClaudeCli;
}

void RemoteService::removeHost(const QString& id, bool deleteHistory) {
    const auto* host = document_.find(utf8(id));
    if (!host) return;
    // REMOTE 2.4: a host whose bridge is on restores its status line first, when it can be reached.
    if (host->quotaClaudeCli && coordinator_->connectedHostIds().contains(utf8(id))) {
        pendingRemovals_[id] = deleteHistory;
        setHostQuotaBridge(id, false);
        QTimer::singleShot(10'000, this, [this, id] {
            if (pendingRemovals_.contains(id)) finishRemoval(id);
        });
        return;
    }
    pendingRemovals_[id] = deleteHistory;
    finishRemoval(id);
}

void RemoteService::finishRemoval(const QString& id) {
    const bool deleteHistory = pendingRemovals_[id];
    pendingRemovals_.erase(id);
    if (!document_.find(utf8(id))) return;
    document_.remove(utf8(id));
    save();
    if (orbs_) {
        const auto orbs = orbs_->orbs();
        for (auto orb : orbs) {
            if (!orb.hosts || !orb.hosts->contains(id)) continue;
            const auto rest = removing(*orb.hosts, id);
            orb.hosts = rest.isEmpty() ? std::nullopt : std::optional<QStringList>(rest);
            orbs_->update(orb);
        }
    }
    if (boards_) {
        for (const auto& entry : boards_->boards()) {
            const auto board = entry.toMap();
            const auto hosts = board.value(QStringLiteral("hosts"));
            if (hosts.typeId() != QMetaType::QVariantList && hosts.typeId() != QMetaType::QStringList) continue;
            const auto ids = hosts.toStringList();
            if (!ids.contains(id)) continue;
            const auto rest = removing(ids, id);
            boards_->setHosts(board.value(QStringLiteral("id")).toString(), rest.isEmpty() ? QVariant(QStringLiteral("all")) : QVariant(rest));
        }
    }
    if (deleteHistory && history_) history_->deleteRemoteHost(utf8(id));
}

void RemoteService::retryNow(const QString& id) { coordinator_->retryNow(utf8(id)); }

QString RemoteService::sshCommand(const QString& id) const {
    const auto* host = document_.find(utf8(id));
    return host ? qt(remote::ssh::terminalCommand(host->destination)) : QString();
}

int RemoteService::testConnection(const QString& destination, const QString& relayFolder) {
    const int token = nextTest_++;
    auto host = remote::Host::make(remote::validation::newId(), utf8(destination.trimmed()), std::nullopt, true, optionalText(relayFolder));
    tests_[token] = coordinator_->testConnection(std::move(host), [this, token](remote::TestOutcome outcome) {
        QMetaObject::invokeMethod(
            this,
            [this, token, outcome] {
                if (!tests_.erase(token)) return;
                QVariantMap result;
                result.insert(QStringLiteral("supported"), outcome.supported);
                result.insert(QStringLiteral("system"), qt(outcome.supported ? outcome.system : outcome.failure.system));
                result.insert(QStringLiteral("arch"), outcome.arch ? qt(std::string(remote::archName(*outcome.arch))) : QString());
                result.insert(QStringLiteral("status"), outcome.supported ? QString() : qt(std::string(outcome.failure.name())));
                result.insert(QStringLiteral("exitStatus"), outcome.failure.exitStatus);
                emit testFinished(token, result);
            },
            Qt::QueuedConnection);
    });
    return token;
}

void RemoteService::cancelTest(int token) {
    const auto it = tests_.find(token);
    if (it == tests_.end()) return;
    coordinator_->cancelTest(it->second);
    tests_.erase(it);
}
