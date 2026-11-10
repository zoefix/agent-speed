#pragma once

#include <QList>
#include <QNetworkAccessManager>
#include <QObject>
#include <QSslCertificate>

#include "agentspeed/quota/http.hpp"

// QUOTAS 9.1 Network rules and NETWORK 9 on Qt: only requests `http::permitted` allows (https to the named hosts, or
// the loopback route to 127.0.0.1); the system proxy (none for loopback); no redirect followed (a 3xx is a failure);
// 10 s (loopback 2 s); a 256 KiB body cap; no cookies, cache or authentication reuse; a self-signed certificate only on
// loopback. Logs host and status only, never headers, bodies or credentials. Lives on one thread.
class QuotaTransport : public QObject, public agentspeed::quota::http::Transport {
    Q_OBJECT
public:
    // Tests only: a certificate authority to trust, and `https://127.0.0.1:<port>` treated as a vendor host (full
    // certificate checks, no loopback exemptions).
    struct TestOptions {
        QList<QSslCertificate> trusted;
        bool localVendor = false;
    };

    explicit QuotaTransport(QObject* parent = nullptr);
    QuotaTransport(TestOptions options, QObject* parent = nullptr);

    void send(agentspeed::quota::http::Request request, agentspeed::quota::http::Completion done) override;
    // Every request in flight ends without calling back.
    void abortAll();

private:
    bool allowed(const agentspeed::quota::http::Request& request) const;

    // The system proxy for vendors; none for loopback.
    QNetworkAccessManager nam_, loopbackNam_;
    TestOptions test_;
    quint64 generation_ = 0;
};
