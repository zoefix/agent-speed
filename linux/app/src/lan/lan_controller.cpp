#include "lan/lan_controller.hpp"

#include <QDateTime>
#include <QSysInfo>

#include "agentspeed/lan_address.hpp"
#include "lan/lan_net.hpp"
#include "preferences.hpp"

using namespace agentspeed::lan;

namespace {

constexpr int kRetryMs = 30'000;

std::optional<TargetKind> kindOf(const QString& kind) { return kindFromName(kind.toStdString()); }

std::optional<std::string> keyOf(const QString& key) { return cleanText(key.toStdString(), 1, kTargetKeyLimit); }

}  // namespace

LanController::LanController(Preferences& prefs, const Strings& strings, QObject* parent)
    : QObject(parent), prefs_(prefs), strings_(strings), feed_(newRunId()) {
    retryTimer_.setInterval(kRetryMs);
    connect(&retryTimer_, &QTimer::timeout, this, &LanController::applyServer);
    clockTimer_.setInterval(1000);
    connect(&clockTimer_, &QTimer::timeout, this, [this] {
        if (watchers_ > 0 && lan_net::steadyMs() - addressesAtMs_ >= 5000) refreshAddresses();
        emit changed();
    });
    connect(&server_, &LanServer::devicesChanged, this, &LanController::changed);
    connect(&targets_, &LanTargets::changed, this, &LanController::changed);
    connect(&prefs_, &Preferences::networkEnabledChanged, this, &LanController::apply);
}

LanController::~LanController() { shutdown(); }

void LanController::start() {
    load();
    started_ = true;
    apply();
}

void LanController::shutdown() {
    if (!started_) return;
    started_ = false;
    server_.stop();
    discovery_.withdraw();
}

bool LanController::networkEnabled() const { return prefs_.networkEnabled(); }

void LanController::load() {
    settings_ = Settings::parse(prefs_.lan().toStdString());
    // LAN 2.1: an enabled server without a valid key gets one now.
    const bool keyMissing = settings_.server.enabled && !settings_.server.key;
    if (keyMissing) settings_.server.key = newKey();
    // The instance id is kept once anything uses the document.
    if (keyMissing || (!settings_.instance && (settings_.server.enabled || !settings_.targets.empty()))) save();
}

const std::string& LanController::instance() {
    if (!settings_.instance) settings_.instance = newUuid();
    return *settings_.instance;
}

void LanController::save() {
    instance();
    prefs_.setLan(QString::fromStdString(settings_.json()));
}

std::string LanController::name() const { return computerName(QSysInfo::machineHostName().toStdString()); }

bool LanController::feedActive() const {
    if (!networkEnabled()) return false;
    return settings_.server.enabled || std::any_of(settings_.targets.begin(), settings_.targets.end(), [](const Target& t) { return t.enabled; });
}

void LanController::apply() {
    if (!started_) return;
    targets_.configure(settings_.targets, networkEnabled());
    applyServer();
    if (!feedActive() && feed_.running()) {
        feed_.stop();
        server_.clearFrame();
    }
    emit changed();
}

void LanController::applyServer() {
    const bool want = started_ && networkEnabled() && settings_.server.enabled && settings_.server.key;
    if (!want) {
        retryTimer_.stop();
        if (server_.state() != LanServer::State::Stopped) server_.stop();
        discovery_.withdraw();
        emit changed();
        return;
    }
    server_.setKey(*settings_.server.key);
    const auto port = static_cast<quint16>(settings_.server.port);
    if (server_.state() != LanServer::State::Listening || server_.port() != port) server_.start(port);
    if (server_.state() == LanServer::State::Listening) {
        retryTimer_.stop();
        discovery_.publish(QString::fromStdString(name()), port, QString::fromStdString(instance()));
    } else {
        discovery_.withdraw();
        if (!retryTimer_.isActive()) retryTimer_.start();
    }
    emit changed();
}

void LanController::onTick(const ReportTick& tick) {
    if (!tick.sample) return;
    Tick next;
    next.monotonicMs = tick.monotonicMs;
    next.wallMs = tick.wallMs;
    next.live = overallValues(*tick.sample);
    next.sources = sourceValues(*tick.sample);
    lastTick_ = next;
    if (!started_ || !feedActive()) {
        if (feed_.running()) {
            feed_.stop();
            server_.clearFrame();
        }
        return;
    }
    const auto built = feed_.tick(next, instance(), name());
    server_.publish(built.frame, built.changed, next.monotonicMs);
    targets_.publish(built.frame, built.changed, next.monotonicMs);
}

void LanController::setServerEnabled(bool on) {
    if (on == settings_.server.enabled) return;
    settings_.server.enabled = on;
    if (on && !settings_.server.key) settings_.server.key = newKey();
    save();
    apply();
    if (watchers_ > 0) refreshAddresses();
}

void LanController::regenerateKey() {
    settings_.server.key = newKey();
    save();
    qCInfo(lanLog) << "lan: the access key was regenerated";
    applyServer();
    if (watchers_ > 0) refreshAddresses();
}

QString LanController::portError(const QString& text) const {
    bool ok = false;
    const int value = text.trimmed().toInt(&ok);
    return ok && value >= kMinPort && value <= kMaxPort ? QString() : t("lan.port.invalid");
}

bool LanController::setPort(const QString& text) {
    if (!portError(text).isEmpty()) return false;
    const int value = text.trimmed().toInt();
    if (value == settings_.server.port) return true;
    settings_.server.port = value;
    save();
    applyServer();
    if (watchers_ > 0) refreshAddresses();
    return true;
}

Target* LanController::find(const QString& id) {
    const auto key = id.toStdString();
    for (auto& t : settings_.targets) {
        if (t.id == key) return &t;
    }
    return nullptr;
}

QString LanController::addTarget(const QString& kind, const QString& address, const QString& name, const QString& key) {
    const auto parsedKind = kindOf(kind);
    const auto cleanAddress = address.trimmed().toStdString();
    if (!canAdd() || !parsedKind || !validTarget(*parsedKind, cleanAddress)) return {};
    Target target;
    target.id = newUuid();
    target.kind = *parsedKind;
    target.address = cleanAddress;
    target.name = cleanText(name.toStdString(), 0, kTargetNameLimit).value_or("");
    target.key = keyOf(key);
    settings_.targets.push_back(target);
    save();
    apply();
    return QString::fromStdString(target.id);
}

bool LanController::updateTarget(const QString& id, const QString& kind, const QString& address, const QString& name, const QString& key) {
    auto* target = find(id);
    const auto parsedKind = kindOf(kind);
    const auto cleanAddress = address.trimmed().toStdString();
    if (!target || !parsedKind || !validTarget(*parsedKind, cleanAddress)) return false;
    target->kind = *parsedKind;
    target->address = cleanAddress;
    target->name = cleanText(name.toStdString(), 0, kTargetNameLimit).value_or("");
    target->key = keyOf(key);
    save();
    apply();
    return true;
}

void LanController::removeTarget(const QString& id) {
    std::erase_if(settings_.targets, [&](const Target& t) { return t.id == id.toStdString(); });
    save();
    apply();
}

void LanController::setTargetEnabled(const QString& id, bool on) {
    auto* target = find(id);
    if (!target || target->enabled == on) return;
    target->enabled = on;
    save();
    apply();
}

int LanController::test(const QString& kind, const QString& address, const QString& key) {
    const int token = ++tests_;
    const auto parsedKind = kindOf(kind);
    std::shared_ptr<const Frame> frame = feed_.latest();
    if (!frame) frame = feed_.single(lastTick_.value_or(Tick{lan_net::steadyMs(), QDateTime::currentMSecsSinceEpoch(), {}, {}}), instance(), name());
    if (!parsedKind) return token;
    targets_.test(*parsedKind, address.trimmed().toStdString(), keyOf(key), frame, [this, token](const LanTargets::Status& status) {
        QVariantMap result;
        const bool ok = status.status == TargetStatus::Ok;
        result.insert(QStringLiteral("ok"), ok);
        result.insert(QStringLiteral("dot"), dotOf(status.status));
        result.insert(QStringLiteral("text"), ok ? t("lan.target.test.ok")
                                              : status.status == TargetStatus::Unreachable ? t("remote.status.unreachable")
                                                                                           : statusLine(status, lan_net::steadyMs(), 0));
        // Emitted after the call returns, so a synchronous outcome reaches QML after it learned the token.
        QMetaObject::invokeMethod(this, [this, token, result] { emit testFinished(token, result); }, Qt::QueuedConnection);
    });
    return token;
}

void LanController::setWatching(bool on) {
    watchers_ = std::max(0, watchers_ + (on ? 1 : -1));
    if (watchers_ > 0) {
        refreshAddresses();
        if (!clockTimer_.isActive()) clockTimer_.start();
    } else {
        clockTimer_.stop();
    }
}
