#include "net/https_client.hpp"

#include <chrono>
#include <memory>

#include "agentspeed/quota/desktop_entry.hpp"
#include "agentspeed/quota/routes/fingerprint.hpp"
#include "net/http_wire.hpp"
#include "net/socket.hpp"
#include "net/tls.hpp"
#include "net/url.hpp"

namespace agentspeed::relay::net {

namespace {

using quota::http::Failure;

constexpr std::size_t kReadChunk = 16 * 1024;

std::int64_t systemMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}

Failure failureOf(Io io) { return io == Io::Timeout ? Failure::Timeout : Failure::Network; }

// The proxy's answer to `CONNECT`: 2xx opens the tunnel; any bytes after its head belong to the tunnel.
Io openTunnel(Socket& socket, const Url& url, const ClientOptions& options, Deadline deadline, int cancelFd, bool& refused) {
    refused = false;
    if (const auto sent = socket.sendAll(connectRequest(url.host, url.port, options.userAgent), deadline, cancelFd); sent != Io::Ok) {
        return sent;
    }
    std::string buffer;
    char chunk[1024];
    while (true) {
        std::size_t received = 0;
        const auto io = socket.receive(chunk, sizeof chunk, received, deadline, cancelFd);
        if (io != Io::Ok) return io == Io::Closed ? Io::Error : io;
        buffer.append(chunk, received);
        bool malformed = false;
        const auto status = connectAnswer(buffer, malformed);
        if (malformed) return Io::Error;
        if (!status) continue;
        if (*status < 200 || *status > 299) {
            refused = true;
            return Io::Error;
        }
        if (!buffer.empty()) socket.unread(std::move(buffer));
        return Io::Ok;
    }
}

}  // namespace

quota::http::Response fetch(const quota::http::Request& request, const ClientOptions& options, int cancelFd) {
    quota::http::Response response;
    const auto finish = [&](Failure failure) {
        response.failure = failure;
        response.receivedMs = options.clock ? options.clock() : systemMs();
        if (failure != Failure::None) response.body.clear();
        return std::move(response);
    };
    const auto url = parseUrl(request.url);
    if (!url || !quota::http::permitted(request)) return finish(Failure::Refused);
    auto bytes = requestBytes(request, *url, options.userAgent);
    if (!bytes) return finish(Failure::Refused);
    struct Wiped {
        std::string& text;
        ~Wiped() { quota::wipe(text); }
    } wiped{*bytes};

    std::shared_ptr<CaChain> ca;
    if (url->tls && !request.loopback) {
        ca = CaChain::load(options.caBundles.empty() ? defaultCaBundles() : options.caBundles);
        if (!ca) return finish(Failure::NoCertificates);
    }
    const auto timeout = std::chrono::milliseconds(request.loopback ? options.loopbackTimeoutMs : options.timeoutMs);
    const auto deadline = std::chrono::steady_clock::now() + timeout;

    Socket socket;
    const auto route = request.loopback ? ProxyRoute{} : proxyFor(url->host, url->port, options.variable);
    if (route.kind == ProxyRoute::Kind::Unusable) return finish(Failure::Network);
    if (route.kind == ProxyRoute::Kind::Proxy) {
        if (const auto io = socket.connect(route.host, route.port, deadline, cancelFd); io != Io::Ok) return finish(failureOf(io));
        bool refused = false;
        if (const auto io = openTunnel(socket, *url, options, deadline, cancelFd, refused); io != Io::Ok) return finish(failureOf(io));
    } else {
        auto target = !request.loopback && options.connectTo ? options.connectTo(url->host, url->port) : std::nullopt;
        const auto host = target ? target->first : url->host;
        const auto port = target ? target->second : url->port;
        if (const auto io = socket.connect(host, port, deadline, cancelFd); io != Io::Ok) return finish(failureOf(io));
    }

    std::unique_ptr<Stream> stream;
    if (url->tls) {
        auto tls = std::make_unique<TlsStream>(socket, deadline, cancelFd);
        if (const auto io = tls->handshake(url->host, ca.get()); io != Io::Ok) return finish(failureOf(io));
        stream = std::move(tls);
    } else {
        stream = std::make_unique<PlainStream>(socket, deadline, cancelFd);
    }
    if (const auto io = stream->write(*bytes); io != Io::Ok) return finish(failureOf(io));

    ResponseReader reader(options.bodyCap);
    std::string chunk(kReadChunk, '\0');
    while (!reader.done() && !reader.failed()) {
        std::size_t received = 0;
        const auto io = stream->read(chunk.data(), chunk.size(), received);
        if (io == Io::Ok) {
            reader.feed(std::string_view(chunk.data(), received));
        } else if (io == Io::Closed) {
            reader.end();
        } else {
            return finish(failureOf(io));
        }
    }
    response.status = reader.status();
    if (reader.failed()) return finish(reader.failure());
    auto body = std::move(reader.body());
    if (const auto& encoding = reader.contentEncoding()) {
        const auto coding = quota::desktop::codingOf(encoding);
        if (!coding) return finish(Failure::Network);
        if (*coding != quota::desktop::Coding::Identity) {
            auto decoded = quota::desktop::decodeBody(body, *coding, options.bodyCap);
            if (!decoded) return finish(Failure::Network);
            body = std::move(*decoded);
        }
    }
    if (const auto& date = reader.date()) response.dateMs = quota::http::parseHttpDate(*date);
    response.body = std::move(body);
    return finish(Failure::None);
}

}  // namespace agentspeed::relay::net
