#pragma once

#include <QNetworkAccessManager>
#include <QObject>
#include <functional>
#include <optional>

#include "agentspeed/report_service.hpp"

// Why a request failed before the service answered (DESIGN 11: report.error.*).
enum class ReportFailure { Unreachable, Insecure, Unavailable, Response, Key };

// The outcome of a discovery fetch (DESIGN 11, Settings → API item 2).
struct ReportFetch {
    enum class Kind { Ok, NotSupported, Invalid, Unreachable, Insecure, Unavailable, Gone } kind = Kind::Unavailable;
    std::optional<agentspeed::report::Discovery> discovery;
};

struct ReportResponse {
    std::optional<agentspeed::report::Reply> reply;  // nullopt: a transport failure
    ReportFailure failure = ReportFailure::Unreachable;
};

// REPORTING 2.3 on Qt: HTTPS with the system's trust and proxy, no cookies, no answered authentication challenge, at
// most 3 https redirects for discovery and none for the API, a total timeout and a body cap. Logs method, host, path,
// status and sizes only — never bodies, signatures or keys.
class ReportHttp : public QObject {
    Q_OBJECT
public:
    explicit ReportHttp(QObject* parent = nullptr);
    void discover(const agentspeed::report::ServiceUrl& service, std::function<void(ReportFetch)> done);
    void send(const QByteArray& method, const std::string& url, const std::string& body, const agentspeed::report::Headers& headers,
              std::function<void(ReportResponse)> done);
    // Every request in flight ends without calling back (forget, quit).
    void abortAll();

private:
    QNetworkAccessManager nam_;
    quint64 generation_ = 0;
};
