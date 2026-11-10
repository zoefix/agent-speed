#pragma once

#include <QMetaType>
#include <QObject>
#include <QSet>
#include <QStringList>
#include <QTimer>
#include <QVariantList>
#include <QVariantMap>
#include <map>
#include <optional>
#include <vector>

#include "agentspeed/quota/window.hpp"
#include "agentspeed/remote_messages.hpp"

class Strings;

// One source's quota as the worker reads it: only sources whose quota switch is on and that are enabled come here.
struct QuotaSourceState {
    agentspeed::SourceKind source = agentspeed::SourceKind::Codex;
    std::optional<agentspeed::quota::Snapshot> snapshot;
    bool running = false;
    bool signedIn = false;
    bool operator==(const QuotaSourceState&) const = default;
};

// One switch's status before it is put into words (DESIGN 5): a route status code, or the bridge's state.
struct QuotaRawStatus {
    QString code;  // ok, noCredential, …; the bridge: waiting, ok, replaced, unreadable, failed
    QString host;
    std::int64_t atMs = 0;
    bool operator==(const QuotaRawStatus&) const = default;
};

// What the worker read, for the UI thread.
struct QuotaPublication {
    std::vector<QuotaSourceState> sources;
    std::map<QString, QuotaRawStatus> statuses;
    QString cliState = QStringLiteral("off");
    QString cliPath;
    bool zcodeKeySaved = false;
};
Q_DECLARE_METATYPE(QuotaPublication)

// A Settings status line (DESIGN 5 額度): the text, whether it shows in the warning style, and whether it is a healthy
// state (the check mark).
struct QuotaStatusLine {
    QString text;
    bool warning = false;
    bool ok = false;
};

// The status codes of DESIGN 5 (phase 2 routes) and QUOTAS 5.4 (the bridge), in words.
namespace quota_status {
// `code`: ok, noCredential, noBrowser, expired, denied, failed, networkOff, notRunning, noCertificates. `name` is the
// agent's or site's name, `host` the vendor's host, `atMs` the snapshot's time for ok.
QuotaStatusLine route(const Strings& strings, const QString& code, const QString& name, const QString& host, std::int64_t atMs,
                      std::int64_t nowMs);
// `state`: waiting, ok, replaced, unreadable, failed (off says nothing).
QuotaStatusLine bridge(const Strings& strings, const QString& state, const QString& path, std::optional<std::int64_t> atMs, std::int64_t nowMs);
}  // namespace quota_status

// The quotas as the UI shows them (UI thread): the dashboard's Sources lines and invitation, Dashboard → Hosts
// sub-lines, the Settings status lines and the 額度 card's items. Texts follow the language and are refreshed every
// 15 s, so reset times count down.
class QuotaView : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantMap lines READ lines NOTIFY changed)
    Q_PROPERTY(QVariantMap spoken READ spoken NOTIFY changed)
    Q_PROPERTY(QStringList listed READ listed NOTIFY changed)
    Q_PROPERTY(bool invite READ invite NOTIFY changed)
    Q_PROPERTY(QVariantMap hostLines READ hostLines NOTIFY changed)
    Q_PROPERTY(QVariantMap statuses READ statuses NOTIFY changed)
    Q_PROPERTY(int revision READ revision NOTIFY changed)
    // Demo mode only: the demo's hosts as Dashboard → Hosts rows ({key, name}).
    Q_PROPERTY(QVariantList demoHosts READ demoHosts NOTIFY changed)
    // Settings → 額度 (DESIGN 5): the Claude Code settings file as shown (`~/.claude/settings.json`), the bridge's state
    // (QUOTAS 5.4: off, waiting, ok, replaced, unreadable, failed), whether a ZCode key is saved, and whether the
    // invitation asked Settings to confirm the bridge.
    Q_PROPERTY(QString cliPath READ cliPath NOTIFY settingsChanged)
    Q_PROPERTY(QString cliState READ cliState NOTIFY settingsChanged)
    Q_PROPERTY(bool zcodeKeySaved READ zcodeKeySaved NOTIFY settingsChanged)
    Q_PROPERTY(bool cliConfirmRequested READ cliConfirmRequested WRITE setCliConfirmRequested NOTIFY settingsChanged)

public:
    explicit QuotaView(const Strings& strings, QObject* parent = nullptr);

    // The local sources the worker read, in source order.
    void setLocal(std::vector<QuotaSourceState> sources);
    // What the worker read: the sources, the Settings statuses, the bridge and the ZCode key.
    void setPublication(const QuotaPublication& publication);
    // DESIGN 4 invitation: Claude Code is enabled, its bridge is off and the invitation was not dismissed.
    void setInvite(bool on);
    // REMOTE 11.1: a host's quota line (`quota`, `quotaRemoved`, `quotaBridge`).
    void applyHost(const agentspeed::remote::RelayMessage& message);
    // The configured hosts' names, which of them are connected, and which sources each host may show (its switches).
    void setHosts(std::map<QString, QString> names, QSet<QString> connected, std::map<QString, QSet<QString>> shown);
    std::optional<agentspeed::remote::HostQuotaBridge> hostBridge(const QString& hostId) const;
    // The host editor (DESIGN 13 Host quotas): the bridge's state as a `settings.quota.cli.*` line {text, warning, ok},
    // and the settings path the host reported (`~/.claude/settings.json` until it did).
    Q_INVOKABLE QVariantMap hostBridgeLine(const QString& hostId) const;
    Q_INVOKABLE QString hostBridgePath(const QString& hostId) const;
    // Demo mode: fixed snapshots of every route (screenshots, DESIGN 4).
    void loadDemo(std::int64_t nowMs);

    // The 額度 card's items for an orb (DESIGN 2a.6): this computer's sources that run, are signed in and have windows,
    // then each connected host's, within `sources` and `hosts` (empty: all). Each item: {key, source, name, lines}.
    QVariantList cardItems(const QStringList& sources, const QStringList& hosts, bool allHosts) const;

    QVariantMap lines() const { return lines_; }
    QVariantMap spoken() const { return spoken_; }
    QStringList listed() const { return listed_; }
    bool invite() const { return invite_; }
    QVariantMap hostLines() const { return hostLines_; }
    QVariantMap statuses() const { return statuses_; }
    int revision() const { return revision_; }
    QVariantList demoHosts() const;

    QString cliPath() const { return cliPath_; }
    QString cliState() const { return cliState_; }
    bool zcodeKeySaved() const { return zcodeKeySaved_; }
    bool cliConfirmRequested() const { return cliConfirmRequested_; }
    void setCliConfirmRequested(bool on);
    void setCli(const QString& path, const QString& state);
    void setZcodeKeySaved(bool saved);

    // The invitation's link: Settings opens on 資料來源 and asks to connect (DESIGN 4).
    Q_INVOKABLE void openCliSettings();
    // QUOTAS 5.2 (after the confirmation), 5.3, and 5.2 again for a replaced status line.
    Q_INVOKABLE void enableCli() { emit enableCliRequested(); }
    Q_INVOKABLE void disableCli() { emit disableCliRequested(); }
    // QUOTAS 9.8: the pasted key goes to the secret store and is never shown again.
    Q_INVOKABLE void saveZcodeKey(const QString& key) { emit zcodeKeyEntered(key); }
    Q_INVOKABLE void removeZcodeKey() { emit zcodeKeyRemoved(); }
    // The Settings page is shown: the bridge's status is checked now (QUOTAS 5.4).
    Q_INVOKABLE void settingsShown() { emit settingsOpened(); }

signals:
    void changed();
    void settingsChanged();
    void cliSettingsRequested();
    void enableCliRequested();
    void disableCliRequested();
    void zcodeKeyEntered(const QString& key);
    void zcodeKeyRemoved();
    void settingsOpened();

private:
    void rebuild();
    std::int64_t now() const;
    QuotaStatusLine statusLine(const QString& switchKey, const QuotaRawStatus& raw, std::int64_t nowMs) const;

    const Strings& strings_;
    std::vector<QuotaSourceState> local_;
    std::map<QString, QuotaRawStatus> rawStatuses_;
    struct Host {
        std::map<agentspeed::SourceKind, agentspeed::remote::HostQuota> reports;
        std::optional<agentspeed::remote::HostQuotaBridge> bridge;
    };
    std::map<QString, Host> hosts_;
    std::map<QString, QString> hostNames_;
    QSet<QString> connected_;
    std::map<QString, QSet<QString>> hostShown_;
    bool inviteOn_ = false;
    bool demo_ = false;

    QVariantMap lines_, spoken_, hostLines_, statuses_;
    QStringList listed_;
    bool invite_ = false;
    int revision_ = 0;
    QString cliPath_ = QStringLiteral("~/.claude/settings.json");
    QString cliState_ = QStringLiteral("off");
    bool zcodeKeySaved_ = false;
    bool cliConfirmRequested_ = false;
    QTimer refresh_;
};
