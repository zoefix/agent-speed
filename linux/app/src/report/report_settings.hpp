#pragma once

#include <QSettings>
#include <QString>
#include <QStringList>
#include <optional>
#include <vector>

#include "agentspeed/report_service.hpp"

// One service identity (DESIGN 11 Storage): the confirmed document, the device and its registration. Keys live in
// ReportKeys, never here.
struct ReportIdentity {
    QString origin;   // scheme://authority of the service URL: the identity's key
    QString service;  // the normalized service URL
    QString deviceId;
    QString username;  // the registered name; empty until registered
    QString profileUrl;
    qint64 keyCreatedMs = 0;
    bool pendingKey = false;
    bool registered = false;
    std::optional<agentspeed::report::ConsentPin> pin;  // what the user confirmed
    QString discovery;                                   // the stored document (report::discoveryJson)
    qint64 fetchedMs = 0;                                // the latest successful fetch
    qint64 lastUsedMs = 0;
};

// The plain report settings (DESIGN 11 Storage) in the settings store, group "report".
class ReportSettings {
public:
    static constexpr int kMaxIdentities = 8;
    static constexpr int kMaxHttpOrigins = 16;

    bool enabled() const;
    void setEnabled(bool value);
    // The selected service: a preset's URL or the custom one.
    QString service() const;
    void setService(const QString& url);
    bool custom() const;
    void setCustom(bool value);
    QString customUrl() const;
    void setCustomUrl(const QString& text);
    bool sectionOn(agentspeed::report::Section section) const;
    void setSection(agentspeed::report::Section section, bool on);
    // 0: not paused; −1: until the user resumes; else the end in Unix ms.
    qint64 pauseUntil() const;
    void setPauseUntil(qint64 value);
    QString invite() const;  // unanswered, accepted or declined
    void setInvite(const QString& answer);
    QString username() const;  // the name the user typed for the next registration; empty: the computer-name default
    void setUsername(const QString& name);
    bool http(const QString& origin) const;  // the user chose HTTP for this origin
    void setHttp(const QString& origin, bool http);

    std::vector<ReportIdentity> identities() const;
    std::optional<ReportIdentity> identity(const QString& origin) const;
    // Stores the identity and returns the device ids of identities dropped to stay within kMaxIdentities.
    QStringList save(const ReportIdentity& identity);
    void remove(const QString& origin);

private:
    void write(const std::vector<ReportIdentity>& list);
    mutable QSettings settings_;
};
