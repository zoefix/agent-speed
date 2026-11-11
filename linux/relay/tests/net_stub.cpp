#include "net_stub.hpp"

#include <arpa/inet.h>
#include <fcntl.h>
#include <mbedtls/ctr_drbg.h>
#include <mbedtls/entropy.h>
#include <mbedtls/net_sockets.h>
#include <mbedtls/pk.h>
#include <mbedtls/ssl.h>
#include <mbedtls/x509_crt.h>
#include <netinet/in.h>
#include <openssl/evp.h>
#include <openssl/pem.h>
#include <openssl/x509.h>
#include <openssl/x509v3.h>
#include <poll.h>
#include <psa/crypto.h>
#include <strings.h>
#include <sys/socket.h>
#include <unistd.h>

#include <cerrno>
#include <cstring>
#include <stdexcept>

namespace relay_test {

namespace {

// ---- Certificates (OpenSSL) ----

struct Made {
    EVP_PKEY* key = nullptr;
    X509* cert = nullptr;
    ~Made() {
        EVP_PKEY_free(key);
        X509_free(cert);
    }
};

void extension(X509* cert, X509* issuer, int nid, const std::string& value) {
    X509V3_CTX context;
    X509V3_set_ctx(&context, issuer, cert, nullptr, nullptr, 0);
    X509_EXTENSION* ext = X509V3_EXT_conf_nid(nullptr, &context, nid, value.c_str());
    if (!ext) throw std::runtime_error("extension");
    X509_add_ext(cert, ext, -1);
    X509_EXTENSION_free(ext);
}

// `issuer` null: self-signed.
void make(Made& out, const std::string& commonName, const Made* issuer, bool ca, const std::string& altNames, long serial) {
    out.key = EVP_EC_gen("P-256");
    out.cert = X509_new();
    X509_set_version(out.cert, 2);
    ASN1_INTEGER_set(X509_get_serialNumber(out.cert), serial);
    X509_gmtime_adj(X509_getm_notBefore(out.cert), -3600);
    X509_gmtime_adj(X509_getm_notAfter(out.cert), 30L * 86400);
    X509_set_pubkey(out.cert, out.key);
    X509_NAME* name = X509_get_subject_name(out.cert);
    X509_NAME_add_entry_by_txt(name, "CN", MBSTRING_ASC, reinterpret_cast<const unsigned char*>(commonName.c_str()), -1, -1, 0);
    X509_set_issuer_name(out.cert, issuer ? X509_get_subject_name(issuer->cert) : name);
    X509* signer = issuer ? issuer->cert : out.cert;
    if (ca) {
        extension(out.cert, signer, NID_basic_constraints, "critical,CA:TRUE");
        extension(out.cert, signer, NID_key_usage, "critical,keyCertSign,cRLSign");
    } else {
        extension(out.cert, signer, NID_basic_constraints, "critical,CA:FALSE");
        extension(out.cert, signer, NID_subject_alt_name, altNames);
    }
    if (X509_sign(out.cert, issuer ? issuer->key : out.key, EVP_sha256()) == 0) throw std::runtime_error("sign");
}

std::string pemOf(X509* cert) {
    BIO* bio = BIO_new(BIO_s_mem());
    PEM_write_bio_X509(bio, cert);
    char* data = nullptr;
    const long length = BIO_get_mem_data(bio, &data);
    std::string out(data, static_cast<std::size_t>(length));
    BIO_free(bio);
    return out;
}

std::string pemOf(EVP_PKEY* key) {
    BIO* bio = BIO_new(BIO_s_mem());
    PEM_write_bio_PrivateKey(bio, key, nullptr, nullptr, 0, nullptr, nullptr);
    char* data = nullptr;
    const long length = BIO_get_mem_data(bio, &data);
    std::string out(data, static_cast<std::size_t>(length));
    BIO_free(bio);
    return out;
}

// ---- Sockets ----

int listenLocal(int& port) {
    const int fd = ::socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    const int one = 1;
    ::setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (::bind(fd, reinterpret_cast<sockaddr*>(&address), sizeof address) != 0 || ::listen(fd, 16) != 0) throw std::runtime_error("listen");
    socklen_t length = sizeof address;
    ::getsockname(fd, reinterpret_cast<sockaddr*>(&address), &length);
    port = ntohs(address.sin_port);
    return fd;
}

// Waits for `fd` or the stop pipe; false when stopping.
bool waitFor(int fd, int stop, short events = POLLIN) {
    pollfd watch[2] = {{fd, events, 0}, {stop, POLLIN, 0}};
    while (::poll(watch, 2, -1) < 0) {
        if (errno != EINTR) return false;
    }
    return watch[1].revents == 0;
}

void stopThread(int stop[2], std::thread& thread) {
    const char byte = 0;
    [[maybe_unused]] const auto written = ::write(stop[1], &byte, 1);
    if (thread.joinable()) thread.join();
    ::close(stop[0]);
    ::close(stop[1]);
}

std::size_t requestEnd(const std::string& bytes) {
    const auto head = bytes.find("\r\n\r\n");
    if (head == std::string::npos) return std::string::npos;
    std::size_t length = 0;
    for (std::size_t pos = 0; pos < head;) {
        const auto next = bytes.find("\r\n", pos);
        const auto line = bytes.substr(pos, next - pos);
        if (strncasecmp(line.c_str(), "content-length:", 15) == 0) length = std::stoul(line.substr(15));
        pos = next + 2;
    }
    return bytes.size() >= head + 4 + length ? head + 4 + length : std::string::npos;
}

int sendAll(int fd, const std::string& bytes) {
    std::size_t sent = 0;
    while (sent < bytes.size()) {
        const auto n = ::send(fd, bytes.data() + sent, bytes.size() - sent, MSG_NOSIGNAL);
        if (n <= 0) return -1;
        sent += static_cast<std::size_t>(n);
    }
    return 0;
}

int bioSend(void* context, const unsigned char* bytes, std::size_t length) {
    const auto n = ::send(*static_cast<int*>(context), bytes, length, MSG_NOSIGNAL);
    return n < 0 ? MBEDTLS_ERR_NET_SEND_FAILED : static_cast<int>(n);
}

int bioReceive(void* context, unsigned char* buffer, std::size_t length) {
    const auto n = ::recv(*static_cast<int*>(context), buffer, length, 0);
    return n < 0 ? MBEDTLS_ERR_NET_RECV_FAILED : static_cast<int>(n);
}

}  // namespace

TestPki makePki(const std::vector<std::string>& hosts) {
    std::string names;
    for (const auto& host : hosts) names += (names.empty() ? "DNS:" : ",DNS:") + host;
    Made ca, leaf, self;
    make(ca, "Agent Speed Test CA", nullptr, true, {}, 1);
    make(leaf, hosts.front(), &ca, false, names, 2);
    make(self, hosts.front(), nullptr, false, names, 3);
    return {pemOf(ca.cert), {pemOf(leaf.cert), pemOf(leaf.key)}, {pemOf(self.cert), pemOf(self.key)}};
}

std::string answer(int status, const std::string& body, const std::vector<std::string>& headers) {
    std::string out = "HTTP/1.1 " + std::to_string(status) + " Stub\r\nContent-Length: " + std::to_string(body.size()) + "\r\n";
    for (const auto& h : headers) out += h + "\r\n";
    return out + "Connection: close\r\n\r\n" + body;
}

// ---- The server ----

struct StubServer::Tls {
    mbedtls_entropy_context entropy;
    mbedtls_ctr_drbg_context drbg;
    mbedtls_x509_crt certificate;
    mbedtls_pk_context key;
    mbedtls_ssl_config config;

    explicit Tls(const KeyPair& pair) {
        mbedtls_entropy_init(&entropy);
        mbedtls_ctr_drbg_init(&drbg);
        mbedtls_x509_crt_init(&certificate);
        mbedtls_pk_init(&key);
        mbedtls_ssl_config_init(&config);
        if (psa_crypto_init() != PSA_SUCCESS || mbedtls_ctr_drbg_seed(&drbg, mbedtls_entropy_func, &entropy, nullptr, 0) != 0 ||
            mbedtls_x509_crt_parse(&certificate, reinterpret_cast<const unsigned char*>(pair.certificatePem.c_str()),
                                   pair.certificatePem.size() + 1) != 0 ||
            mbedtls_pk_parse_key(&key, reinterpret_cast<const unsigned char*>(pair.keyPem.c_str()), pair.keyPem.size() + 1, nullptr, 0,
                                 mbedtls_ctr_drbg_random, &drbg) != 0 ||
            mbedtls_ssl_config_defaults(&config, MBEDTLS_SSL_IS_SERVER, MBEDTLS_SSL_TRANSPORT_STREAM, MBEDTLS_SSL_PRESET_DEFAULT) != 0) {
            throw std::runtime_error("tls server");
        }
        mbedtls_ssl_conf_rng(&config, mbedtls_ctr_drbg_random, &drbg);
        if (mbedtls_ssl_conf_own_cert(&config, &certificate, &key) != 0) throw std::runtime_error("own cert");
    }
    ~Tls() {
        mbedtls_ssl_config_free(&config);
        mbedtls_pk_free(&key);
        mbedtls_x509_crt_free(&certificate);
        mbedtls_ctr_drbg_free(&drbg);
        mbedtls_entropy_free(&entropy);
    }
};

StubServer::StubServer(Handler handler, const KeyPair* tls) : handler_(std::move(handler)) {
    if (tls) tls_ = std::make_unique<Tls>(*tls);
    listener_ = listenLocal(port_);
    if (::pipe2(stop_, O_CLOEXEC) != 0) throw std::runtime_error("pipe");
    thread_ = std::thread([this] { run(); });
}

StubServer::~StubServer() {
    stopThread(stop_, thread_);
    ::close(listener_);
}

std::vector<std::string> StubServer::requests() const {
    std::lock_guard lock(mutex_);
    return requests_;
}

void StubServer::run() {
    while (waitFor(listener_, stop_[0])) {
        const int fd = ::accept4(listener_, nullptr, nullptr, SOCK_CLOEXEC);
        if (fd < 0) continue;
        ++connections_;
        serve(fd);
        ::close(fd);
    }
}

void StubServer::serve(int fd) {
    mbedtls_ssl_context ssl;
    mbedtls_ssl_init(&ssl);
    int socket = fd;
    const auto read = [&](char* buffer, std::size_t capacity) -> long {
        if ((!tls_ || mbedtls_ssl_get_bytes_avail(&ssl) == 0) && !waitFor(fd, stop_[0])) return -1;
        if (!tls_) return ::recv(fd, buffer, capacity, 0);
        while (true) {
            const int n = mbedtls_ssl_read(&ssl, reinterpret_cast<unsigned char*>(buffer), capacity);
            if (n == MBEDTLS_ERR_SSL_WANT_READ || n == MBEDTLS_ERR_SSL_WANT_WRITE) continue;
            return n < 0 ? -1 : n;
        }
    };
    const auto write = [&](const std::string& bytes) {
        if (!tls_) return sendAll(fd, bytes);
        std::size_t sent = 0;
        while (sent < bytes.size()) {
            const int n = mbedtls_ssl_write(&ssl, reinterpret_cast<const unsigned char*>(bytes.data() + sent), bytes.size() - sent);
            if (n == MBEDTLS_ERR_SSL_WANT_READ || n == MBEDTLS_ERR_SSL_WANT_WRITE) continue;
            if (n <= 0) return -1;
            sent += static_cast<std::size_t>(n);
        }
        return 0;
    };
    if (tls_) {
        if (mbedtls_ssl_setup(&ssl, &tls_->config) != 0) {
            mbedtls_ssl_free(&ssl);
            return;
        }
        mbedtls_ssl_set_bio(&ssl, &socket, bioSend, bioReceive, nullptr);
        int code = 0;
        while ((code = mbedtls_ssl_handshake(&ssl)) != 0) {
            if (code != MBEDTLS_ERR_SSL_WANT_READ && code != MBEDTLS_ERR_SSL_WANT_WRITE) {
                mbedtls_ssl_free(&ssl);
                return;
            }
        }
    }
    std::string request;
    char buffer[4096];
    while (requestEnd(request) == std::string::npos) {
        const auto n = read(buffer, sizeof buffer);
        if (n <= 0) {
            mbedtls_ssl_free(&ssl);
            return;
        }
        request.append(buffer, static_cast<std::size_t>(n));
    }
    {
        std::lock_guard lock(mutex_);
        requests_.push_back(request);
    }
    if (const auto response = handler_(request)) {
        write(*response);
        if (tls_) mbedtls_ssl_close_notify(&ssl);
    } else {
        waitFor(fd, stop_[0]);
    }
    mbedtls_ssl_free(&ssl);
}

// ---- The proxy ----

ProxyStub::ProxyStub(int target, bool refuse) : target_(target), refuse_(refuse) {
    listener_ = listenLocal(port_);
    if (::pipe2(stop_, O_CLOEXEC) != 0) throw std::runtime_error("pipe");
    thread_ = std::thread([this] { run(); });
}

ProxyStub::~ProxyStub() {
    stopThread(stop_, thread_);
    ::close(listener_);
}

std::vector<std::string> ProxyStub::connects() const {
    std::lock_guard lock(mutex_);
    return connects_;
}

void ProxyStub::run() {
    while (waitFor(listener_, stop_[0])) {
        const int fd = ::accept4(listener_, nullptr, nullptr, SOCK_CLOEXEC);
        if (fd < 0) continue;
        tunnel(fd);
        ::close(fd);
    }
}

void ProxyStub::tunnel(int client) {
    std::string head;
    char buffer[16384];
    while (head.find("\r\n\r\n") == std::string::npos) {
        if (!waitFor(client, stop_[0])) return;
        const auto n = ::recv(client, buffer, sizeof buffer, 0);
        if (n <= 0) return;
        head.append(buffer, static_cast<std::size_t>(n));
    }
    {
        std::lock_guard lock(mutex_);
        connects_.push_back(head.substr(0, head.find("\r\n")));
    }
    if (refuse_) {
        sendAll(client, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n");
        return;
    }
    const int upstream = ::socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons(static_cast<std::uint16_t>(target_));
    if (::connect(upstream, reinterpret_cast<sockaddr*>(&address), sizeof address) != 0) {
        ::close(upstream);
        return;
    }
    sendAll(client, "HTTP/1.1 200 Connection established\r\n\r\n");
    pollfd watch[3] = {{client, POLLIN, 0}, {upstream, POLLIN, 0}, {stop_[0], POLLIN, 0}};
    bool open = true;
    while (open && ::poll(watch, 3, -1) > 0 && watch[2].revents == 0) {
        for (int side = 0; side < 2 && open; ++side) {
            if (watch[side].revents == 0) continue;
            const auto n = ::recv(watch[side].fd, buffer, sizeof buffer, 0);
            open = n > 0 && sendAll(watch[1 - side].fd, std::string(buffer, static_cast<std::size_t>(n))) == 0;
        }
    }
    ::close(upstream);
}

}  // namespace relay_test
