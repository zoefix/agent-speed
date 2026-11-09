#include "characters/character_transport.hpp"

#include <QFile>
#include <QNetworkCookie>
#include <QNetworkCookieJar>
#include <QNetworkProxyFactory>
#include <QNetworkReply>
#include <QPointer>
#include <memory>

#include "agentspeed/character/catalog.hpp"

namespace {

class NoCookies : public QNetworkCookieJar {
public:
    using QNetworkCookieJar::QNetworkCookieJar;
    QList<QNetworkCookie> cookiesForUrl(const QUrl&) const override { return {}; }
    bool setCookiesFromUrl(const QList<QNetworkCookie>&, const QUrl&) override { return false; }
};

void guardRedirects(QNetworkReply* reply) {
    QObject::connect(reply, &QNetworkReply::redirected, reply, [reply](const QUrl& to) {
        if (NetworkCharacterTransport::permits(to)) emit reply->redirectAllowed();
        else reply->abort();
    });
}

}  // namespace

NetworkCharacterTransport::NetworkCharacterTransport(QByteArray userAgent, QObject* parent)
    : QObject(parent), userAgent_(std::move(userAgent)) {
    QNetworkProxyFactory::setUseSystemConfiguration(true);
    nam_.setCookieJar(new NoCookies(&nam_));
    nam_.setRedirectPolicy(QNetworkRequest::UserVerifiedRedirectPolicy);
}

bool NetworkCharacterTransport::permits(const QUrl& url) {
    if (!url.isValid() || !url.userInfo().isEmpty()) return false;
    return agentspeed::character::CharacterCatalog::permits(url.toString(QUrl::FullyEncoded).toStdString());
}

QNetworkRequest NetworkCharacterTransport::request(const QUrl& url, const QByteArray& accept) const {
    QNetworkRequest r(url);
    r.setAttribute(QNetworkRequest::CacheLoadControlAttribute, QNetworkRequest::AlwaysNetwork);
    r.setAttribute(QNetworkRequest::CacheSaveControlAttribute, false);
    r.setRawHeader("User-Agent", userAgent_);
    r.setRawHeader("Accept", accept);
    return r;
}

void NetworkCharacterTransport::getCatalog(const QUrl& url, const QString& etag, std::function<void(const Response&)> done) {
    if (!permits(url)) {
        done(Response{0, {}, {}, true});
        return;
    }
    auto r = request(url, "application/json");
    r.setTransferTimeout(15'000);
    if (!etag.isEmpty()) r.setRawHeader("If-None-Match", etag.toUtf8());
    auto* reply = nam_.get(r);
    guardRedirects(reply);
    const qint64 cap = static_cast<qint64>(agentspeed::character::CharacterCatalog::kMaxCatalogBytes);
    connect(reply, &QNetworkReply::downloadProgress, reply, [reply, cap](qint64 received, qint64 total) {
        if (received > cap || total > cap) reply->abort();
    });
    connect(reply, &QNetworkReply::finished, this, [reply, done] {
        Response response;
        response.status = reply->attribute(QNetworkRequest::HttpStatusCodeAttribute).toInt();
        response.body = reply->readAll();
        response.etag = QString::fromUtf8(reply->rawHeader("ETag"));
        response.failed = reply->error() != QNetworkReply::NoError && response.status != 304;
        reply->deleteLater();
        done(response);
    });
}

CharacterTransport::Cancel NetworkCharacterTransport::download(const QUrl& url, const QString& path, qint64 size,
                                                               std::function<void(double)> progress,
                                                               std::function<void(bool, bool)> done) {
    auto file = std::make_shared<QFile>(path);
    if (!permits(url) || !file->open(QIODevice::WriteOnly | QIODevice::Truncate)) {
        done(false, false);
        return [] {};
    }
    auto r = request(url, "application/octet-stream");
    r.setTransferTimeout(60'000);
    auto* reply = nam_.get(r);
    guardRedirects(reply);
    auto cancelled = std::make_shared<bool>(false);
    connect(reply, &QNetworkReply::readyRead, reply, [reply, file, size] {
        file->write(reply->readAll());
        if (file->size() > size) reply->abort();
    });
    connect(reply, &QNetworkReply::downloadProgress, reply, [progress, size](qint64 received, qint64) {
        if (size > 0) progress(std::min(1.0, static_cast<double>(received) / static_cast<double>(size)));
    });
    connect(reply, &QNetworkReply::finished, this, [reply, file, done, cancelled, size] {
        file->write(reply->readAll());
        file->close();
        const bool ok = reply->error() == QNetworkReply::NoError && file->size() == size;
        reply->deleteLater();
        done(ok, *cancelled);
    });
    QPointer<QNetworkReply> guard(reply);
    return [guard, cancelled] {
        *cancelled = true;
        if (guard) guard->abort();
    };
}
