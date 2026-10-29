#include "report/report_keys.hpp"

#include <QDir>
#include <QFile>
#include <QRegularExpression>
#include <QSaveFile>

#include <sys/stat.h>

namespace {

bool safeId(const QString& deviceId) {
    static const QRegularExpression uuid(QStringLiteral("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"));
    return uuid.match(deviceId).hasMatch();
}

}  // namespace

ReportKeys::ReportKeys(QString folder) : folder_(std::move(folder)) {}

QString ReportKeys::path(const QString& deviceId, const char* suffix) const {
    return folder_ + QLatin1Char('/') + deviceId + QLatin1String(suffix);
}

QByteArray ReportKeys::read(const QString& file) const {
    QFile f(file);
    if (!f.open(QIODevice::ReadOnly)) return {};
    return f.read(4096).trimmed();
}

bool ReportKeys::write(const QString& file, const QByteArray& data) {
    if (!QDir().mkpath(folder_)) return false;
    ::chmod(QFile::encodeName(folder_).constData(), 0700);
    QSaveFile out(file);
    out.setDirectWriteFallback(false);
    if (!out.open(QIODevice::WriteOnly)) return false;
    // The temporary file gets the final permissions before any byte of the key is written.
    out.setPermissions(QFileDevice::ReadOwner | QFileDevice::WriteOwner);
    if (out.write(data) != data.size()) {
        out.cancelWriting();
        return false;
    }
    return out.commit();
}

std::optional<agentspeed::report::PrivateKey> ReportKeys::key(const QString& deviceId) const {
    if (!safeId(deviceId)) return std::nullopt;
    const auto hex = read(path(deviceId, ".key"));
    return agentspeed::report::PrivateKey::fromHex(std::string_view(hex.constData(), static_cast<std::size_t>(hex.size())));
}

std::optional<agentspeed::report::PrivateKey> ReportKeys::pending(const QString& deviceId) const {
    if (!safeId(deviceId)) return std::nullopt;
    const auto hex = read(path(deviceId, ".pending"));
    return agentspeed::report::PrivateKey::fromHex(std::string_view(hex.constData(), static_cast<std::size_t>(hex.size())));
}

QString ReportKeys::apiKey(const QString& deviceId) const {
    if (!safeId(deviceId)) return {};
    return QString::fromUtf8(read(path(deviceId, ".apikey")));
}

bool ReportKeys::saveKey(const QString& deviceId, const agentspeed::report::PrivateKey& key) {
    return safeId(deviceId) && write(path(deviceId, ".key"), QByteArray::fromStdString(key.hex()));
}

bool ReportKeys::savePending(const QString& deviceId, const agentspeed::report::PrivateKey& key) {
    return safeId(deviceId) && write(path(deviceId, ".pending"), QByteArray::fromStdString(key.hex()));
}

bool ReportKeys::promotePending(const QString& deviceId) {
    const auto next = pending(deviceId);
    if (!next || !saveKey(deviceId, *next)) return false;
    dropPending(deviceId);
    return true;
}

void ReportKeys::dropPending(const QString& deviceId) {
    if (safeId(deviceId)) QFile::remove(path(deviceId, ".pending"));
}

bool ReportKeys::saveApiKey(const QString& deviceId, const QString& apiKey) {
    if (!safeId(deviceId)) return false;
    if (apiKey.isEmpty()) return QFile::remove(path(deviceId, ".apikey")) || !QFile::exists(path(deviceId, ".apikey"));
    return write(path(deviceId, ".apikey"), apiKey.toUtf8());
}

void ReportKeys::forget(const QString& deviceId) {
    if (!safeId(deviceId)) return;
    for (const char* suffix : {".key", ".pending", ".apikey"}) QFile::remove(path(deviceId, suffix));
}
