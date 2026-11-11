#pragma once

#include <atomic>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <vector>

// Local servers for the relay's HTTPS client tests (REMOTE 10): an HTTP or HTTPS server on 127.0.0.1 that answers
// with whatever the test says, and a `CONNECT` proxy in front of it. Certificates are made per run with OpenSSL; their
// keys never leave the test process.
namespace relay_test {

struct KeyPair {
    std::string certificatePem;
    std::string keyPem;
};

struct TestPki {
    std::string caPem;    // the trust anchor the client is given
    KeyPair leaf;         // signed by the CA, named for every host given
    KeyPair selfSigned;   // the same names, signed by itself
};
TestPki makePki(const std::vector<std::string>& hosts);

class StubServer {
public:
    // The raw request (head and body) → the raw answer; nullopt keeps the connection open without an answer.
    using Handler = std::function<std::optional<std::string>(const std::string& request)>;

    // `tls` null: plain HTTP.
    StubServer(Handler handler, const KeyPair* tls);
    ~StubServer();
    StubServer(const StubServer&) = delete;
    StubServer& operator=(const StubServer&) = delete;

    int port() const { return port_; }
    std::vector<std::string> requests() const;
    int connections() const { return connections_.load(); }

private:
    struct Tls;
    void run();
    void serve(int fd);

    Handler handler_;
    std::unique_ptr<Tls> tls_;
    int listener_ = -1, port_ = 0;
    int stop_[2] = {-1, -1};
    std::atomic<int> connections_{0};
    mutable std::mutex mutex_;
    std::vector<std::string> requests_;
    std::thread thread_;
};

// A `CONNECT` proxy that tunnels every connection to 127.0.0.1:`target`, or refuses with 403.
class ProxyStub {
public:
    ProxyStub(int target, bool refuse = false);
    ~ProxyStub();
    ProxyStub(const ProxyStub&) = delete;
    ProxyStub& operator=(const ProxyStub&) = delete;

    int port() const { return port_; }
    // The request lines received (`CONNECT host:port HTTP/1.1`).
    std::vector<std::string> connects() const;

private:
    void run();
    void tunnel(int client);

    int target_;
    bool refuse_;
    int listener_ = -1, port_ = 0;
    int stop_[2] = {-1, -1};
    mutable std::mutex mutex_;
    std::vector<std::string> connects_;
    std::thread thread_;
};

// An HTTP answer with `Content-Length`.
std::string answer(int status, const std::string& body, const std::vector<std::string>& headers = {});

}  // namespace relay_test
