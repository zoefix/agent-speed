#include <QBuffer>
#include <QDateTime>
#include <QFile>
#include <QFileDialog>
#include <QImageReader>
#include <QImageWriter>
#include <QPainter>

#include "preferences.hpp"
#include "report/report_controller.hpp"
#include "strings.hpp"

// The profile extensions (REPORTING-EXTENSIONS 4–6): explicit actions only — nothing here runs on its own, and the
// network master switch stops every request and the pairing poll.
namespace {

namespace profile = agentspeed::report::profile;

QString qs(const std::string& text) { return QString::fromStdString(text); }
std::string sv(const QString& text) { return text.toStdString(); }
std::string bytesOf(const QByteArray& data) { return std::string(data.constData(), static_cast<std::size_t>(data.size())); }

// REPORTING-EXTENSIONS 5: PNG or JPEG within the limits, oriented, center-cropped to a square and re-encoded as a
// 256 × 256 PNG without any metadata of the source.
QByteArray avatarPng(const QString& path) {
    QFile file(path);
    if (!file.open(QIODevice::ReadOnly)) return {};
    const auto data = file.read(static_cast<qint64>(profile::kMaxInputBytes) + 1);
    if (data.size() > static_cast<qint64>(profile::kMaxInputBytes) || !profile::imageHeader(bytesOf(data))) return {};
    QBuffer buffer;
    buffer.setData(data);
    buffer.open(QIODevice::ReadOnly);
    QImageReader reader(&buffer);
    reader.setAutoTransform(true);
    const auto source = reader.read();
    if (source.isNull()) return {};
    const int side = std::min(source.width(), source.height());
    const auto square = source.copy((source.width() - side) / 2, (source.height() - side) / 2, side, side)
                            .scaled(profile::kAvatarSize, profile::kAvatarSize, Qt::IgnoreAspectRatio, Qt::SmoothTransformation);
    QImage clean(profile::kAvatarSize, profile::kAvatarSize, square.hasAlphaChannel() ? QImage::Format_ARGB32 : QImage::Format_RGB32);
    clean.fill(Qt::transparent);
    QPainter painter(&clean);
    painter.drawImage(0, 0, square);
    painter.end();
    QByteArray out;
    QBuffer target(&out);
    target.open(QIODevice::WriteOnly);
    QImageWriter writer(&target, "png");
    if (!writer.write(clean)) return {};
    return profile::isUpload(bytesOf(out)) ? out : QByteArray();
}

}  // namespace

bool ReportController::avatarSupported() const { return discovery_ && discovery_->has("profileAvatar"); }
bool ReportController::identitySupported() const { return discovery_ && discovery_->has("sharedIdentity"); }

bool ReportController::profileAvailable() const {
    return identity_ && identity_->registered && key_ && (avatarSupported() || identitySupported());
}

QString ReportController::profileName() const { return profile_ ? qs(profile_->displayName) : username(); }

QString ReportController::avatarSource() const {
    const auto& image = pendingAvatar_.isEmpty() ? avatarImage_ : pendingAvatar_;
    return image.isEmpty() ? QString() : QStringLiteral("data:image/png;base64,") + QString::fromLatin1(image.toBase64());
}

QVariantList ReportController::profileDevices() const {
    QVariantList rows;
    if (!profile_) return rows;
    for (const auto& device : profile_->devices) {
        rows.append(QVariantMap{{QStringLiteral("platform"), qs(device.platform)},
                                {QStringLiteral("fingerprint"), qs(device.deviceId.substr(0, 8))},
                                {QStringLiteral("current"), device.isCurrent},
                                {QStringLiteral("online"), device.online}});
    }
    return rows;
}

QString ReportController::pairingClaimant() const {
    if (!pairing_ || !pairing_->claimant) return {};
    return qs(pairing_->claimant->platform) + QStringLiteral(" · ") + qs(pairing_->claimant->deviceId.substr(0, 8));
}

void ReportController::profileCall(const QByteArray& method, const std::string& path, const std::string& body, const char* contentType,
                                   std::function<void(const ReportResponse&)> done) {
    if (!networkEnabled() || !profileAvailable()) return;
    profileBusy_ = true;
    profileError_.clear();
    notify();
    const qint64 cap = path == "/v1/profile/avatar" && method == "GET" ? static_cast<qint64>(profile::kMaxPngBytes) : static_cast<qint64>(profile::kMaxJsonBytes);
    signedCall(
        method, path, body, false, false,
        [this, done = std::move(done)](const ReportResponse& response) {
            profileBusy_ = false;
            if (!response.reply || !response.reply->success()) profileError_ = t("profile.failed");
            done(response);
            notify();
        },
        contentType, cap);
}

void ReportController::applyProfile(const ReportResponse& response) {
    if (!response.reply || !response.reply->success()) return;
    const auto parsed = profile::parseProfile(bytesOf(response.body), rules());
    if (!parsed) {
        profileError_ = t("profile.failed");
        return;
    }
    const bool newAvatar = parsed->avatar && (!profile_ || !profile_->avatar || profile_->avatar->sha256 != parsed->avatar->sha256);
    if (!parsed->avatar) avatarImage_.clear();
    profile_ = parsed;
    if (newAvatar) fetchAvatar();
}

void ReportController::refreshProfile() {
    profileCall("GET", "/v1/profile", "", nullptr, [this](const ReportResponse& r) { applyProfile(r); });
}

// The current avatar comes from the same API with a signed GET; its bytes must match the advertised hash.
void ReportController::fetchAvatar() {
    if (!profile_ || !profile_->avatar) return;
    const auto expected = profile_->avatar->sha256;
    profileCall("GET", "/v1/profile/avatar", "", nullptr, [this, expected](const ReportResponse& r) {
        if (r.reply && r.reply->success() && agentspeed::report::sha256Hex(bytesOf(r.body)) == expected) avatarImage_ = r.body;
    });
}

void ReportController::chooseAvatar() {
    const auto path = QFileDialog::getOpenFileName(nullptr, t("profile.avatar.choose"), QString(), QStringLiteral("Images (*.png *.jpg *.jpeg)"));
    if (!path.isEmpty()) loadAvatar(path);
}

void ReportController::loadAvatar(const QString& path) {
    const auto png = avatarPng(path);
    if (png.isEmpty()) {
        profileError_ = t("profile.avatar.invalid");
    } else {
        pendingAvatar_ = png;
        profileError_.clear();
    }
    notify();
}

void ReportController::cancelAvatar() {
    pendingAvatar_.clear();
    notify();
}

void ReportController::uploadAvatar() {
    if (pendingAvatar_.isEmpty()) return;
    profileCall("PUT", "/v1/profile/avatar", bytesOf(pendingAvatar_), "image/png", [this](const ReportResponse& r) {
        if (r.reply && r.reply->success()) {
            avatarImage_ = pendingAvatar_;
            pendingAvatar_.clear();
            applyProfile(r);
        }
    });
}

void ReportController::removeAvatar() {
    profileCall("DELETE", "/v1/profile/avatar", "", nullptr, [this](const ReportResponse& r) {
        if (r.reply && r.reply->success()) avatarImage_.clear();
        applyProfile(r);
    });
}

// Bound: the shared display name; otherwise the device's own registered username (REPORTING 4.4).
void ReportController::saveProfileName(const QString& input) {
    const auto name = input.normalized(QString::NormalizationForm_KC);
    if (!profileBound()) {
        commitUsername(name);
        return;
    }
    if (agentspeed::report::validateUsername(sv(name), rules())) {
        profileError_ = usernameMessage(agentspeed::report::validateUsername(sv(name), rules()), rules());
        notify();
        return;
    }
    profileCall("PATCH", "/v1/profile", agentspeed::report::Json::object({{"displayName", agentspeed::report::Json::of(sv(name))}}).compact(), nullptr,
                [this](const ReportResponse& r) { applyProfile(r); });
}

void ReportController::createIdentity() {
    profileCall("POST", "/v1/profile/identity", "{}", nullptr, [this](const ReportResponse& r) { applyProfile(r); });
}

void ReportController::unlinkDevice() {
    profileCall("DELETE", "/v1/profile/identity", "", nullptr, [this](const ReportResponse& r) { applyProfile(r); });
}

void ReportController::invitePairing() {
    pairingInviter_ = true;
    profileCall("POST", "/v1/profile/pairings", "{}", nullptr, [this](const ReportResponse& r) {
        if (const auto p = r.reply && r.reply->success() ? profile::parsePairing(bytesOf(r.body), correctedMs()) : std::nullopt) {
            pairing_ = p;
            pairingCode_ = p->code ? qs(profile::displayCode(*p->code)) : QString();
            pollPairing();
        }
    });
}

void ReportController::joinPairing(const QString& code) {
    const auto normalized = profile::normalizeCode(sv(code.trimmed()));
    if (!normalized) {
        profileError_ = t("profile.pairing.expired");
        notify();
        return;
    }
    pairingInviter_ = false;
    pairingCode_.clear();
    profileCall("POST", "/v1/profile/pairings/claim", agentspeed::report::Json::object({{"code", agentspeed::report::Json::of(*normalized)}}).compact(), nullptr,
                [this](const ReportResponse& r) {
                    if (const auto p = r.reply && r.reply->success() ? profile::parsePairing(bytesOf(r.body), correctedMs()) : std::nullopt) {
                        pairing_ = p;
                        pollPairing();
                    }
                });
}

void ReportController::approvePairing() {
    if (!pairing_ || pairing_->state != "pending" || !pairingInviter_) return;
    profileCall("POST", "/v1/profile/pairings/" + pairing_->id + "/approve", "{}", nullptr, [this](const ReportResponse& r) {
        if (const auto p = r.reply && r.reply->success() ? profile::parsePairing(bytesOf(r.body), correctedMs()) : std::nullopt) pairing_ = p;
        refreshProfile();
    });
}

void ReportController::cancelPairing() {
    if (!pairing_) return;
    const auto id = pairing_->id;
    pairingTimer_.stop();
    profileCall("DELETE", "/v1/profile/pairings/" + id, "", nullptr, [this](const ReportResponse&) {
        pairing_.reset();
        pairingCode_.clear();
    });
}

void ReportController::checkPairing() {
    if (!pairing_) return;
    profileCall("GET", "/v1/profile/pairings/" + pairing_->id, "", nullptr, [this](const ReportResponse& r) {
        if (r.reply && r.reply->status == 404) {
            pairing_.reset();
            pairingCode_.clear();
            return;
        }
        if (const auto p = r.reply && r.reply->success() ? profile::parsePairing(bytesOf(r.body), correctedMs()) : std::nullopt) {
            const bool approvedNow = p->state == "approved" && (!pairing_ || pairing_->state != "approved");
            pairing_ = p;
            if (approvedNow) refreshProfile();
        }
        pollPairing();
    });
}

// At least 2 s apart, only while the device window is open, the network on and the pairing undecided.
void ReportController::pollPairing() {
    pairingTimer_.stop();
    if (!pairing_ || !devicesOpen_ || !networkEnabled()) return;
    if (pairing_->state != "waiting" && pairing_->state != "pending") return;
    if (correctedMs() >= pairing_->expiresAtMs) {
        pairing_->state = "expired";
        notify();
        return;
    }
    pairingTimer_.setSingleShot(true);
    pairingTimer_.setInterval(2500);
    disconnect(&pairingTimer_, nullptr, this, nullptr);
    connect(&pairingTimer_, &QTimer::timeout, this, [this] { checkPairing(); });
    pairingTimer_.start();
}

void ReportController::openDevices() {
    devicesOpen_ = true;
    emit devicesRequested();
    refreshProfile();
}

void ReportController::closeDevices() {
    devicesOpen_ = false;
    pairingTimer_.stop();
}
