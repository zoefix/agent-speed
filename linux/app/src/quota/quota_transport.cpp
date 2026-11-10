#include "quota/quota_transport.hpp"

#include <QDateTime>
#include <QNetworkCookie>
#include <QNetworkCookieJar>
#include <QNetworkProxy>
#include <QNetworkProxyFactory>
#include <QNetworkReply>
#include <QPointer>
#include <QSslConfiguration>
#include <QTimer>
#include <QUrl>

namespace http = agentspeed::quota::http;

namespace {

class NoCookies : public QNetworkCookieJar {
public:
    using QNetworkCookieJar::QNetworkCookieJar;
    QList<QNetworkCookie> cookiesForUrl(const QUrl&) const override { return {}; }
    bool setCookiesFromUrl(const QList<QNetworkCookie>&, const QUrl&) override { return false; }
};

QByteArray userAgent() { return QByteArrayLiteral("AgentSpeed/" AGENTSPEED_VERSION " (Linux)"); }

bool hasHeader(const http::Request& request, std::string_view name) {
    for (const auto& h : request.headers) {
        if (QByteArray::fromStdString(h.name).compare(QByteArray(name.data(), static_cast<qsizetype>(name.size())), Qt::CaseInsensitive) == 0) {
            return true;
        }
    }
    return false;
}

void finish(const http::Completion& done, http::Failure failure, int status = 0) {
    http::Response response;
    response.failure = failure;
    response.status = status;
    response.receivedMs = QDateTime::currentMSecsSinceEpoch();
    done(std::move(response));
}

}  // namespace

QuotaTransport::QuotaTransport(QObject* parent) : QuotaTransport(TestOptions{}, parent) {}

QuotaTransport::QuotaTransport(TestOptions options, QObject* parent) : QObject(parent), test_(std::move(options)) {
    QNetworkProxyFactory::setUseSystemConfiguration(true);
    for (auto* nam : {&nam_, &loopbackNam_}) {
        nam->setCookieJar(new NoCookies(nam));
        nam->setRedirectPolicy(QNetworkRequest::ManualRedirectPolicy);
        nam->setStrictTransportSecurityEnabled(false);
    }
    loopbackNam_.setProxy(QNetworkProxy(QNetworkProxy::NoProxy));
}

bool QuotaTransport::allowed(const http::Request& request) const {
    if (http::permitted(request)) return true;
    return test_.localVendor && !request.loopback && request.url.rfind("https://127.0.0.1:", 0) == 0;
}

void QuotaTransport::send(http::Request request, http::Completion done) {
    const QUrl url(QString::fromStdString(request.url), QUrl::StrictMode);
    if (!allowed(request) || !url.isValid()) {
        finish(done, http::Failure::Refused);
        return;
    }
    const int timeoutMs = request.loopback ? http::kLoopbackTimeoutMs : http::kTimeoutMs;
    QNetworkRequest q(url);
    q.setTransferTimeout(timeoutMs);
    q.setAttribute(QNetworkRequest::RedirectPolicyAttribute, QNetworkRequest::ManualRedirectPolicy);
    q.setAttribute(QNetworkRequest::CacheLoadControlAttribute, QNetworkRequest::AlwaysNetwork);
    q.setAttribute(QNetworkRequest::CacheSaveControlAttribute, false);
    q.setAttribute(QNetworkRequest::CookieLoadControlAttribute, QNetworkRequest::Manual);
    q.setAttribute(QNetworkRequest::CookieSaveControlAttribute, QNetworkRequest::Manual);
    q.setAttribute(QNetworkRequest::AuthenticationReuseAttribute, QNetworkRequest::Manual);
    q.setAttribute(QNetworkRequest::Http2AllowedAttribute, !request.loopback);
    // Qt would otherwise send the system language.
    q.setRawHeader("Accept-Language", "*");
    for (const auto& h : request.headers) q.setRawHeader(QByteArray::fromStdString(h.name), QByteArray::fromStdString(h.value));
    if (!hasHeader(request, "User-Agent")) q.setRawHeader("User-Agent", userAgent());
    if (!test_.trusted.isEmpty()) {
        auto ssl = q.sslConfiguration();
        ssl.addCaCertificates(test_.trusted);
        q.setSslConfiguration(ssl);
    }
    auto& nam = request.loopback ? loopbackNam_ : nam_;
    const QByteArray body = QByteArray::fromStdString(request.body);
    QNetworkReply* reply = request.method == http::Method::Post ? nam.post(q, body) : nam.get(q);
    if (request.loopback) {
        // Antigravity's language server signs its own certificate; only 127.0.0.1 is ever asked this way.
        connect(reply, &QNetworkReply::sslErrors, reply, [reply](const QList<QSslError>&) { reply->ignoreSslErrors(); });
    }
    auto* timer = new QTimer(reply);
    timer->setSingleShot(true);
    connect(timer, &QTimer::timeout, reply, [reply] {
        reply->setProperty("timedOut", true);
        reply->abort();
    });
    timer->start(timeoutMs);
    auto buffer = std::make_shared<QByteArray>();
    connect(reply, &QNetworkReply::readyRead, reply, [reply, buffer] {
        if (!reply->isOpen()) return;
        buffer->append(reply->readAll());
        if (buffer->size() > static_cast<qsizetype>(http::kBodyCap)) {
            reply->setProperty("tooLarge", true);
            reply->abort();
        }
    });
    const auto generation = generation_;
    QPointer<QuotaTransport> self(this);
    connect(reply, &QNetworkReply::finished, this, [self, reply, buffer, done = std::move(done), generation] {
        reply->deleteLater();
        if (!self || generation != self->generation_) return;
        if (!reply->property("tooLarge").toBool() && reply->isOpen()) buffer->append(reply->readAll());
        const int status = reply->attribute(QNetworkRequest::HttpStatusCodeAttribute).toInt();
        const auto host = reply->url().host();
        http::Response response;
        response.status = status;
        response.receivedMs = QDateTime::currentMSecsSinceEpoch();
        if (reply->property("tooLarge").toBool() || buffer->size() > static_cast<qsizetype>(http::kBodyCap)) {
            response.failure = http::Failure::TooLarge;
        } else if (reply->property("timedOut").toBool() || reply->error() == QNetworkReply::TimeoutError ||
                   reply->error() == QNetworkReply::OperationCanceledError) {
            response.failure = http::Failure::Timeout;
        } else if (status >= 300 && status < 400) {
            response.failure = http::Failure::Redirect;
        } else if (status == 0) {
            response.failure = http::Failure::Network;
        }
        if (response.failure == http::Failure::None) {
            response.body = buffer->toStdString();
            const auto date = reply->rawHeader("Date");
            if (!date.isEmpty()) response.dateMs = http::parseHttpDate(date.toStdString());
        }
        if (response.failure != http::Failure::None || status >= 400) qInfo("quota: %s %d", qPrintable(host), status);
        buffer->clear();
        done(std::move(response));
    });
}

void QuotaTransport::abortAll() {
    ++generation_;
    for (auto* nam : {&nam_, &loopbackNam_}) {
        for (auto* reply : nam->findChildren<QNetworkReply*>()) reply->abort();
    }
}
