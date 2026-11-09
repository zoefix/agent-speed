#pragma once

#include <QByteArray>
#include <QNetworkAccessManager>
#include <QObject>
#include <QString>
#include <QUrl>
#include <functional>

// How the character library talks to the `characters` release (CHARACTERS 2, NETWORK "Characters"): the catalog with
// its ETag and one pack at a time into a file. Tests replace it with local copies.
class CharacterTransport {
public:
    struct Response {
        int status = 0;
        QByteArray body;
        QString etag;
        bool failed = false;
    };
    using Cancel = std::function<void()>;

    virtual ~CharacterTransport() = default;
    virtual void getCatalog(const QUrl& url, const QString& etag, std::function<void(const Response&)> done) = 0;
    // Downloads `url` into `path` (at most `size` bytes); `done(ok, cancelled)`. Returns a function that cancels it.
    virtual Cancel download(const QUrl& url, const QString& path, qint64 size, std::function<void(double)> progress,
                            std::function<void(bool ok, bool cancelled)> done) = 0;
};

// The network: HTTPS GETs on the release hosts only (every redirect hop checked), no cookies or cache, the catalog
// capped at 1 MiB and a pack at its catalog size, a User-Agent and nothing else identifying.
class NetworkCharacterTransport : public QObject, public CharacterTransport {
    Q_OBJECT
public:
    explicit NetworkCharacterTransport(QByteArray userAgent, QObject* parent = nullptr);
    void getCatalog(const QUrl& url, const QString& etag, std::function<void(const Response&)> done) override;
    Cancel download(const QUrl& url, const QString& path, qint64 size, std::function<void(double)> progress,
                    std::function<void(bool ok, bool cancelled)> done) override;

    // https on github.com, objects.githubusercontent.com or release-assets.githubusercontent.com, port 443, no user.
    static bool permits(const QUrl& url);

private:
    QNetworkRequest request(const QUrl& url, const QByteArray& accept) const;
    QNetworkAccessManager nam_;
    QByteArray userAgent_;
};
