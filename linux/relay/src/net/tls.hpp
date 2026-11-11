#pragma once

#include <memory>
#include <string>
#include <vector>

#include "net/socket.hpp"

// TLS for the quota requests on mbedTLS 3 (REMOTE 11.4): the host's CA certificates, the server's name checked; the
// loopback route alone takes any certificate (Antigravity's language server signs its own).
namespace agentspeed::relay::net {

// REMOTE 11.4: the first that exists is the host's bundle.
const std::vector<std::string>& defaultCaBundles();

// A parsed CA bundle.
class CaChain {
public:
    // The first file of `candidates` that exists; null when none does or it holds no certificate.
    static std::shared_ptr<CaChain> load(const std::vector<std::string>& candidates);
    CaChain();
    ~CaChain();
    CaChain(const CaChain&) = delete;
    CaChain& operator=(const CaChain&) = delete;

    struct Certificates;
    Certificates& certificates() const { return *certificates_; }

private:
    std::unique_ptr<Certificates> certificates_;
};

// The byte stream of one request: a plain connection, or TLS over it.
class Stream {
public:
    virtual ~Stream() = default;
    virtual Io write(std::string_view bytes) = 0;
    virtual Io read(char* buffer, std::size_t capacity, std::size_t& received) = 0;
};

class PlainStream final : public Stream {
public:
    PlainStream(Socket& socket, Deadline deadline, int cancelFd) : socket_(socket), deadline_(deadline), cancelFd_(cancelFd) {}
    Io write(std::string_view bytes) override { return socket_.sendAll(bytes, deadline_, cancelFd_); }
    Io read(char* buffer, std::size_t capacity, std::size_t& received) override {
        return socket_.receive(buffer, capacity, received, deadline_, cancelFd_);
    }

private:
    Socket& socket_;
    Deadline deadline_;
    int cancelFd_;
};

class TlsStream final : public Stream {
public:
    TlsStream(Socket& socket, Deadline deadline, int cancelFd);
    ~TlsStream() override;
    TlsStream(const TlsStream&) = delete;
    TlsStream& operator=(const TlsStream&) = delete;

    // `ca` null: any certificate (loopback only). Otherwise the chain must lead to `ca` and name `host`.
    Io handshake(const std::string& host, const CaChain* ca);
    Io write(std::string_view bytes) override;
    Io read(char* buffer, std::size_t capacity, std::size_t& received) override;

    struct State;

private:
    std::unique_ptr<State> state_;
};

}  // namespace agentspeed::relay::net
