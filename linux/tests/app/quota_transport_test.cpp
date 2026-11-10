#include <gtest/gtest.h>
#include <openssl/evp.h>
#include <openssl/pem.h>
#include <openssl/x509.h>
#include <openssl/x509v3.h>

#include <QCoreApplication>
#include <QElapsedTimer>
#include <QSslKey>
#include <QSslServer>
#include <QSslSocket>
#include <QTcpServer>
#include <QTcpSocket>
#include <functional>
#include <memory>

#include "quota/quota_transport.hpp"

// QUOTAS 9.1 Network rules on Qt Network, against local stub servers: exactly the headers given plus the User-Agent,
// no cookies, no redirect followed, the body cap, the loopback timeout, a self-signed certificate on loopback only and
// full certificate checks for a vendor.
namespace {

namespace http = agentspeed::quota::http;

bool pump(const std::function<bool()>& done, int ms = 5000) {
    QElapsedTimer timer;
    timer.start();
    while (!done()) {
        if (timer.elapsed() > ms) return false;
        QCoreApplication::processEvents(QEventLoop::AllEvents, 10);
    }
    return true;
}

// A key and certificate for 127.0.0.1, signed by `issuer` (self-signed without one).
struct Identity {
    QSslKey key;
    QSslCertificate certificate;
    EVP_PKEY* pkey = nullptr;
    X509* x509 = nullptr;
    ~Identity() {
        X509_free(x509);
        EVP_PKEY_free(pkey);
    }
};

QByteArray pem(X509* cert) {
    BIO* bio = BIO_new(BIO_s_mem());
    PEM_write_bio_X509(bio, cert);
    char* data = nullptr;
    const long size = BIO_get_mem_data(bio, &data);
    QByteArray out(data, static_cast<qsizetype>(size));
    BIO_free(bio);
    return out;
}

QByteArray pem(EVP_PKEY* key) {
    BIO* bio = BIO_new(BIO_s_mem());
    PEM_write_bio_PrivateKey(bio, key, nullptr, nullptr, 0, nullptr, nullptr);
    char* data = nullptr;
    const long size = BIO_get_mem_data(bio, &data);
    QByteArray out(data, static_cast<qsizetype>(size));
    BIO_free(bio);
    return out;
}

void extension(X509* cert, X509* issuer, int nid, const char* value) {
    X509V3_CTX ctx;
    X509V3_set_ctx(&ctx, issuer, cert, nullptr, nullptr, 0);
    X509_EXTENSION* ext = X509V3_EXT_conf_nid(nullptr, &ctx, nid, value);
    X509_add_ext(cert, ext, -1);
    X509_EXTENSION_free(ext);
}

std::unique_ptr<Identity> identity(const char* commonName, bool ca, const Identity* issuer) {
    auto out = std::make_unique<Identity>();
    out->pkey = EVP_EC_gen("P-256");
    out->x509 = X509_new();
    X509_set_version(out->x509, 2);
    static long serial = 1;
    ASN1_INTEGER_set(X509_get_serialNumber(out->x509), serial++);
    X509_gmtime_adj(X509_getm_notBefore(out->x509), -3600);
    X509_gmtime_adj(X509_getm_notAfter(out->x509), 3600 * 24);
    X509_set_pubkey(out->x509, out->pkey);
    X509_NAME* name = X509_get_subject_name(out->x509);
    X509_NAME_add_entry_by_txt(name, "CN", MBSTRING_ASC, reinterpret_cast<const unsigned char*>(commonName), -1, -1, 0);
    X509_set_issuer_name(out->x509, issuer ? X509_get_subject_name(issuer->x509) : name);
    X509* signer = issuer ? issuer->x509 : out->x509;
    if (ca) {
        extension(out->x509, signer, NID_basic_constraints, "critical,CA:TRUE");
        extension(out->x509, signer, NID_key_usage, "critical,keyCertSign,cRLSign");
    } else {
        extension(out->x509, signer, NID_subject_alt_name, "IP:127.0.0.1");
        extension(out->x509, signer, NID_ext_key_usage, "serverAuth");
    }
    X509_sign(out->x509, issuer ? issuer->pkey : out->pkey, EVP_sha256());
    out->certificate = QSslCertificate(pem(out->x509), QSsl::Pem);
    out->key = QSslKey(pem(out->pkey), QSsl::Ec, QSsl::Pem);
    return out;
}

// One HTTP/1.1 answer per connection; keeps every request it read.
class Stub : public QObject {
public:
    QByteArray status = "200 OK";
    QList<QByteArray> headers{"Content-Type: application/json", "Date: Tue, 10 Nov 2026 19:26:13 GMT"};
    QByteArray body = R"({"ok":true})";
    bool silent = false;
    QList<QByteArray> requests;

    explicit Stub(const Identity* tls = nullptr) {
        if (tls) {
            auto* server = new QSslServer(this);
            QSslConfiguration config = QSslConfiguration::defaultConfiguration();
            config.setLocalCertificate(tls->certificate);
            config.setPrivateKey(tls->key);
            config.setPeerVerifyMode(QSslSocket::VerifyNone);
            server->setSslConfiguration(config);
            server_ = server;
        } else {
            server_ = new QTcpServer(this);
        }
        connect(server_, &QTcpServer::pendingConnectionAvailable, this, [this] {
            while (auto* socket = server_->nextPendingConnection()) serve(socket);
        });
        server_->listen(QHostAddress::LocalHost);
    }
    quint16 port() const { return server_->serverPort(); }
    QString url(const char* scheme, const char* path = "/x") const {
        return QStringLiteral("%1://127.0.0.1:%2%3").arg(QLatin1String(scheme)).arg(port()).arg(QLatin1String(path));
    }

private:
    void serve(QTcpSocket* socket) {
        auto buffer = std::make_shared<QByteArray>();
        const auto read = [this, socket, buffer] {
            buffer->append(socket->readAll());
            const auto end = buffer->indexOf("\r\n\r\n");
            if (end < 0) return;
            const auto head = buffer->left(end).toLower();
            qsizetype length = 0;
            if (const auto at = head.indexOf("content-length:"); at >= 0) length = head.mid(at + 15, head.indexOf("\r\n", at) - at - 15).trimmed().toLongLong();
            if (buffer->size() < end + 4 + length) return;
            requests.push_back(*buffer);
            buffer->clear();
            if (silent) return;
            QByteArray answer = "HTTP/1.1 " + status + "\r\n";
            for (const auto& h : headers) answer += h + "\r\n";
            answer += "Content-Length: " + QByteArray::number(body.size()) + "\r\nConnection: close\r\n\r\n" + body;
            socket->write(answer);
            socket->disconnectFromHost();
        };
        connect(socket, &QTcpSocket::readyRead, socket, read);
        if (socket->bytesAvailable() > 0) read();
    }
    QTcpServer* server_ = nullptr;
};

http::Response send(QuotaTransport& transport, http::Request request, int ms = 8000) {
    std::optional<http::Response> got;
    transport.send(std::move(request), [&](http::Response r) { got = std::move(r); });
    EXPECT_TRUE(pump([&] { return got.has_value(); }, ms));
    return got.value_or(http::Response{});
}

http::Request loopback(const QString& url, http::Method method = http::Method::Post) {
    http::Request r;
    r.method = method;
    r.url = url.toStdString();
    r.loopback = true;
    r.headers = {{"Connect-Protocol-Version", "1"}, {"Content-Type", "application/json"}, {"X-Codeium-Csrf-Token", "csrf"}};
    r.body = R"({"forceRefresh": true})";
    return r;
}

QList<QByteArray> headerNames(const QByteArray& request) {
    QList<QByteArray> names;
    const auto lines = request.left(request.indexOf("\r\n\r\n")).split('\n');
    for (qsizetype i = 1; i < lines.size(); ++i) names.push_back(lines[i].left(lines[i].indexOf(':')).trimmed().toLower());
    std::sort(names.begin(), names.end());
    return names;
}

TEST(QuotaTransport, LoopbackSendsExactlyTheGivenHeadersAndTheUserAgent) {
    Stub stub;
    QuotaTransport transport;
    const auto r = send(transport, loopback(stub.url("http", "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary")));
    EXPECT_EQ(r.failure, http::Failure::None);
    EXPECT_EQ(r.status, 200);
    EXPECT_EQ(r.body, R"({"ok":true})");
    EXPECT_EQ(r.dateMs, 1794338773000);
    EXPECT_GT(r.receivedMs, 0);
    ASSERT_EQ(stub.requests.size(), 1);
    // Qt writes header names in lower case.
    const auto raw = stub.requests.front().toLower();
    EXPECT_TRUE(raw.startsWith("post /exa.language_server_pb.languageserverservice/retrieveuserquotasummary http/1.1\r\n")) << raw.constData();
    EXPECT_TRUE(raw.contains(QByteArray("\r\nuser-agent: agentspeed/" AGENTSPEED_VERSION " (linux)\r\n")));
    EXPECT_TRUE(raw.contains("\r\nx-codeium-csrf-token: csrf\r\n"));
    EXPECT_TRUE(raw.contains("\r\naccept-language: *\r\n"));
    EXPECT_TRUE(raw.endsWith(R"({"forcerefresh": true})"));
    // Only the transport's own framing besides the route's headers: no cookie, no authorization, no language.
    const QList<QByteArray> expected{"accept-encoding", "accept-language", "connect-protocol-version", "connection", "content-length",
                                     "content-type", "host", "user-agent", "x-codeium-csrf-token"};
    EXPECT_EQ(headerNames(raw), expected) << raw.constData();
}

TEST(QuotaTransport, AGivenUserAgentReplacesOurs) {
    Stub stub;
    QuotaTransport transport;
    auto request = loopback(stub.url("http"));
    request.headers.push_back({"User-Agent", "Mozilla/5.0 (X11; Linux x86_64)"});
    send(transport, request);
    ASSERT_EQ(stub.requests.size(), 1);
    EXPECT_TRUE(stub.requests.front().toLower().contains("\r\nuser-agent: mozilla/5.0 (x11; linux x86_64)\r\n"));
    EXPECT_FALSE(stub.requests.front().toLower().contains("agentspeed/"));
}

TEST(QuotaTransport, RefusesWhatTheRulesForbidWithoutSending) {
    Stub stub;
    QuotaTransport transport;
    for (const auto* url : {"http://api.kimi.com/coding/v1/usages", "https://api.anthropic.com/api/oauth/usage", "https://claude.ai/api/x",
                            "https://example.com/", "ftp://api.kimi.com/x"}) {
        http::Request r;
        r.url = url;
        EXPECT_EQ(send(transport, r).failure, http::Failure::Refused) << url;
    }
    // Loopback requests only to 127.0.0.1; a vendor request never to it.
    auto r = loopback(QStringLiteral("http://localhost:%1/x").arg(stub.port()));
    EXPECT_EQ(send(transport, r).failure, http::Failure::Refused);
    r = loopback(stub.url("https"));
    r.loopback = false;
    EXPECT_EQ(send(transport, r).failure, http::Failure::Refused);
    EXPECT_TRUE(stub.requests.isEmpty());
}

TEST(QuotaTransport, NeverFollowsARedirect) {
    Stub stub;
    stub.status = "302 Found";
    stub.headers = {QByteArray("Location: ") + stub.url("http", "/elsewhere").toUtf8()};
    QuotaTransport transport;
    EXPECT_EQ(send(transport, loopback(stub.url("http"))).failure, http::Failure::Redirect);
    pump([] { return false; }, 300);
    EXPECT_EQ(stub.requests.size(), 1);
}

TEST(QuotaTransport, CapsTheBody) {
    Stub stub;
    stub.body = QByteArray(static_cast<qsizetype>(http::kBodyCap) + 1, 'x');
    QuotaTransport transport;
    const auto r = send(transport, loopback(stub.url("http")));
    EXPECT_EQ(r.failure, http::Failure::TooLarge);
    EXPECT_TRUE(r.body.empty());
    stub.body = QByteArray(static_cast<qsizetype>(http::kBodyCap), 'x');
    EXPECT_EQ(send(transport, loopback(stub.url("http"))).failure, http::Failure::None);
}

TEST(QuotaTransport, LoopbackGivesUpAfterTwoSeconds) {
    Stub stub;
    stub.silent = true;
    QuotaTransport transport;
    QElapsedTimer timer;
    timer.start();
    EXPECT_EQ(send(transport, loopback(stub.url("http")), 6000).failure, http::Failure::Timeout);
    EXPECT_GE(timer.elapsed(), 1800);
    EXPECT_LT(timer.elapsed(), 4000);
}

TEST(QuotaTransport, StoresNoCookie) {
    Stub stub;
    stub.headers.push_back("Set-Cookie: session=abc; Path=/");
    QuotaTransport transport;
    send(transport, loopback(stub.url("http")));
    send(transport, loopback(stub.url("http")));
    ASSERT_EQ(stub.requests.size(), 2);
    EXPECT_FALSE(stub.requests[1].toLower().contains("\r\ncookie:"));
}

TEST(QuotaTransport, LoopbackAcceptsASelfSignedCertificate) {
    if (!QSslSocket::supportsSsl()) GTEST_SKIP() << "no TLS backend";
    const auto self = identity("127.0.0.1", false, nullptr);
    Stub stub(self.get());
    QuotaTransport transport;
    const auto r = send(transport, loopback(stub.url("https")));
    EXPECT_EQ(r.failure, http::Failure::None);
    EXPECT_EQ(r.status, 200);
}

TEST(QuotaTransport, AVendorCertificateMustBeTrusted) {
    if (!QSslSocket::supportsSsl()) GTEST_SKIP() << "no TLS backend";
    const auto ca = identity("Agent Speed Test CA", true, nullptr);
    const auto server = identity("127.0.0.1", false, ca.get());
    const auto self = identity("127.0.0.1", false, nullptr);
    http::Request request;
    request.headers = {{"Accept", "application/json"}, {"Authorization", "Bearer test-token"}};
    {
        Stub stub(self.get());
        QuotaTransport transport(QuotaTransport::TestOptions{{ca->certificate}, true});
        request.url = stub.url("https", "/coding/v1/usages").toStdString();
        EXPECT_EQ(send(transport, request).failure, http::Failure::Network);
        EXPECT_TRUE(stub.requests.isEmpty());
    }
    Stub stub(server.get());
    QuotaTransport transport(QuotaTransport::TestOptions{{ca->certificate}, true});
    request.url = stub.url("https", "/coding/v1/usages").toStdString();
    const auto r = send(transport, request);
    EXPECT_EQ(r.failure, http::Failure::None);
    ASSERT_EQ(stub.requests.size(), 1);
    EXPECT_TRUE(stub.requests.front().startsWith("GET /coding/v1/usages HTTP/1.1\r\n")) << stub.requests.front().constData();
    EXPECT_TRUE(stub.requests.front().toLower().contains("\r\nauthorization: bearer test-token\r\n"));
}

}  // namespace
