#pragma once

#include <QString>
#include <optional>

#include "agentspeed/report.hpp"

// DESIGN 11 Storage on Linux: <data>/report/<deviceId>.key (the private scalar as hex), <deviceId>.pending (a rotated
// key not yet confirmed, REPORTING 4.5) and <deviceId>.apikey, each mode 0600 in a 0700 folder, written atomically.
// Keys never go to the settings store or the log.
class ReportKeys {
public:
    explicit ReportKeys(QString folder);

    std::optional<agentspeed::report::PrivateKey> key(const QString& deviceId) const;
    std::optional<agentspeed::report::PrivateKey> pending(const QString& deviceId) const;
    QString apiKey(const QString& deviceId) const;

    bool saveKey(const QString& deviceId, const agentspeed::report::PrivateKey& key);
    bool savePending(const QString& deviceId, const agentspeed::report::PrivateKey& key);
    // The pending key became the current one.
    bool promotePending(const QString& deviceId);
    void dropPending(const QString& deviceId);
    bool saveApiKey(const QString& deviceId, const QString& apiKey);
    // Forget, Reset and a forgotten identity delete every file of the device.
    void forget(const QString& deviceId);

private:
    QString path(const QString& deviceId, const char* suffix) const;
    bool write(const QString& file, const QByteArray& data);
    QByteArray read(const QString& file) const;

    QString folder_;
};
