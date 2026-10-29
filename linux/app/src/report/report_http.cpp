#include "report/report_http.hpp"

#include <QDateTime>
#include <QNetworkCookie>
#include <QNetworkCookieJar>
#include <QNetworkProxyFactory>
#include <QNetworkReply>
#include <QPointer>
#include <QTimer>

namespace {

constexpr qint64 kDiscoveryCap = 65'536;
constexpr qint64 kApiCap = 16'384;
constexpr int kDiscoveryTimeoutMs = 15'000;
constexpr int kApiTimeoutMs = 10'000;
constexpr int kMaxRedirects = 3;

class NoCookies : public QNetworkCookieJar {
public:
    using QNetworkCookieJar::QNetworkCookieJar;
    QList<QNetworkCookie> cookiesForUrl(const QUrl&) const override { return {}; }
    bool setCookiesFromUrl(const QList<QNetworkCookie>&, const QUrl&) override { return false; }
};

bool unreachable(QNetworkReply::NetworkError error) {
    switch (error) {
    case QNetworkReply::ConnectionRefusedError:
    case QNetworkReply::RemoteHostClosedError:
    case QNetworkReply::HostNotFoundError:
    case QNetworkReply::TimeoutError:
    case QNetworkReply::OperationCanceledError:
    case QNetworkReply::TemporaryNetworkFailureError:
    case QNetworkReply::NetworkSessionFailedError:
    case QNetworkReply::UnknownNetworkError:
    case QNetworkReply::ProxyConnectionRefusedError:
    case QNetworkReply::ProxyConnectionClosedError:
    case QNetworkReply::ProxyNotFoundError:
    case QNetworkReply::ProxyTimeoutError: return true;
    default: return false;
    }
}

QNetworkRequest baseRequest(const QUrl& url, int timeoutMs) {
    QNetworkRequest request(url);
    request.setTransferTimeout(timeoutMs);
    request.setAttribute(QNetworkRequest::CacheLoadControlAttribute, QNetworkRequest::AlwaysNetwork);
    request.setAttribute(QNetworkRequest::CacheSaveControlAttribute, false);
    request.setAttribute(QNetworkRequest::CookieLoadControlAttribute, QNetworkRequest::Manual);
    request.setAttribute(QNetworkRequest::CookieSaveControlAttribute, QNetworkRequest::Manual);
    request.setAttribute(QNetworkRequest::AuthenticationReuseAttribute, QNetworkRequest::Manual);
    // Qt would otherwise send the system language.
    request.setRawHeader("Accept-Language", "*");
    return request;
}

// Aborts a reply when its total time or body size is exceeded; `flag` tells why afterwards.
void guard(QNetworkReply* reply, int timeoutMs, qint64 cap) {
    auto* timer = new QTimer(reply);
    timer->setSingleShot(true);
    QObject::connect(timer, &QTimer::timeout, reply, [reply] {
        reply->setProperty("timedOut", true);
        reply->abort();
    });
    timer->start(timeoutMs);
    QObject::connect(reply, &QNetworkReply::downloadProgress, reply, [reply, cap](qint64 received, qint64 total) {
        if (received > cap || total > cap) {
            reply->setProperty("tooBig", true);
            reply->abort();
        }
    });
}

std::map<std::string, std::string> headersOf(const QNetworkReply* reply) {
    std::map<std::string, std::string> out;
    for (const auto& [name, value] : reply->rawHeaderPairs()) out[name.toLower().toStdString()] = value.toStdString();
    return out;
}

}  // namespace

ReportHttp::ReportHttp(QObject* parent) : QObject(parent) {
    QNetworkProxyFactory::setUseSystemConfiguration(true);
    nam_.setCookieJar(new NoCookies(&nam_));
    nam_.setRedirectPolicy(QNetworkRequest::ManualRedirectPolicy);
    nam_.setStrictTransportSecurityEnabled(false);
}

void ReportHttp::discover(const agentspeed::report::ServiceUrl& service, std::function<void(ReportFetch)> done) {
    const QUrl url(QString::fromStdString(service.discoveryUrl()));
    auto request = baseRequest(url, kDiscoveryTimeoutMs);
    request.setAttribute(QNetworkRequest::RedirectPolicyAttribute, QNetworkRequest::UserVerifiedRedirectPolicy);
    request.setRawHeader("User-Agent", "AgentSpeed/" AGENTSPEED_VERSION " (Linux)");
    request.setRawHeader("Accept", "application/json");
    auto* reply = nam_.get(request);
    guard(reply, kDiscoveryTimeoutMs, kDiscoveryCap);
    const bool loopback = service.loopbackHttp();
    connect(reply, &QNetworkReply::redirected, reply, [reply, loopback](const QUrl& to) {
        const int count = reply->property("redirects").toInt() + 1;
        reply->setProperty("redirects", count);
        const bool allowed = to.scheme() == QLatin1String("https") || (loopback && to.scheme() == QLatin1String("http") && to.host() == reply->url().host());
        if (!allowed) {
            reply->setProperty("insecureRedirect", true);
            reply->abort();
        } else if (count > kMaxRedirects) {
            reply->setProperty("tooManyRedirects", true);
            reply->abort();
        } else {
            emit reply->redirectAllowed();
        }
    });
    const auto generation = generation_;
    QPointer<ReportHttp> self(this);
    connect(reply, &QNetworkReply::finished, this, [self, reply, service, done = std::move(done), generation] {
        reply->deleteLater();
        if (!self || generation != self->generation_) return;
        const int status = reply->attribute(QNetworkRequest::HttpStatusCodeAttribute).toInt();
        const auto body = reply->readAll();
        ReportFetch fetch;
        using Kind = ReportFetch::Kind;
        if (reply->property("insecureRedirect").toBool()) fetch.kind = Kind::Insecure;
        else if (reply->property("tooManyRedirects").toBool()) fetch.kind = Kind::Unavailable;
        else if (reply->property("tooBig").toBool()) fetch.kind = Kind::NotSupported;
        else if (reply->property("timedOut").toBool()) fetch.kind = Kind::Unreachable;
        else if (status == 410) fetch.kind = Kind::Gone;
        else if (status == 429 || status >= 500 || (status >= 300 && status < 400)) fetch.kind = Kind::Unavailable;
        else if (status >= 400) fetch.kind = Kind::NotSupported;
        else if (status >= 200 && status < 300) {
            agentspeed::report::DiscoveryProblem problem;
            fetch.discovery = agentspeed::report::parseDiscoveryBody(std::string_view(body.constData(), static_cast<std::size_t>(body.size())), service, &problem);
            fetch.kind = fetch.discovery ? Kind::Ok : problem.kind == agentspeed::report::DiscoveryProblem::Kind::NotSupported ? Kind::NotSupported : Kind::Invalid;
            if (!fetch.discovery) qInfo("report: discovery of %s not accepted (field %s)", qPrintable(reply->url().host()), problem.field.c_str());
        } else if (reply->error() == QNetworkReply::SslHandshakeFailedError) fetch.kind = Kind::Insecure;
        else if (unreachable(reply->error())) fetch.kind = Kind::Unreachable;
        else fetch.kind = Kind::Unavailable;
        qInfo("report: GET %s%s %d, %lld bytes", qPrintable(reply->url().host()), qPrintable(reply->url().path()), status,
              static_cast<long long>(body.size()));
        done(std::move(fetch));
    });
}

void ReportHttp::send(const QByteArray& method, const std::string& url, const std::string& body, const agentspeed::report::Headers& headers,
                      std::function<void(ReportResponse)> done, qint64 cap) {
    const QUrl target(QString::fromStdString(url));
    auto request = baseRequest(target, kApiTimeoutMs);
    for (const auto& [name, value] : headers) request.setRawHeader(QByteArray::fromStdString(name), QByteArray::fromStdString(value));
    const auto data = QByteArray::fromStdString(body);
    auto* reply = nam_.sendCustomRequest(request, method, data);
    guard(reply, kApiTimeoutMs, std::max(cap, kApiCap));
    const auto generation = generation_;
    const auto started = QDateTime::currentMSecsSinceEpoch();
    QPointer<ReportHttp> self(this);
    connect(reply, &QNetworkReply::finished, this, [self, reply, method, done = std::move(done), generation, started, sent = data.size()] {
        reply->deleteLater();
        if (!self || generation != self->generation_) return;
        const int status = reply->attribute(QNetworkRequest::HttpStatusCodeAttribute).toInt();
        const auto bytes = reply->readAll();
        ReportResponse response;
        response.body = bytes;
        if (reply->property("tooBig").toBool()) {
            response.failure = ReportFailure::Response;
        } else if (status > 0 && !reply->property("timedOut").toBool()) {
            response.reply = agentspeed::report::Reply::parse(status, headersOf(reply), std::string_view(bytes.constData(), static_cast<std::size_t>(bytes.size())),
                                                              QDateTime::currentSecsSinceEpoch());
        } else if (reply->error() == QNetworkReply::SslHandshakeFailedError) {
            response.failure = ReportFailure::Insecure;
        } else if (reply->property("timedOut").toBool() || unreachable(reply->error())) {
            response.failure = ReportFailure::Unreachable;
        } else {
            response.failure = ReportFailure::Unavailable;
        }
        const auto code = response.reply && response.reply->code ? QString::fromStdString(*response.reply->code) : QString();
        qInfo("report: %s %s%s %d %s, %lld/%lld bytes, %lld ms", method.constData(), qPrintable(reply->url().host()), qPrintable(reply->url().path()),
              status, qPrintable(code), static_cast<long long>(sent), static_cast<long long>(bytes.size()),
              static_cast<long long>(QDateTime::currentMSecsSinceEpoch() - started));
        if (response.reply && response.reply->logMessage) qInfo("report: service message: %s", response.reply->logMessage->c_str());
        done(std::move(response));
    });
}

void ReportHttp::abortAll() {
    ++generation_;
    for (auto* reply : nam_.findChildren<QNetworkReply*>()) reply->abort();
}
