#include "net/tls.hpp"

#include <mbedtls/ctr_drbg.h>
#include <mbedtls/entropy.h>
#include <mbedtls/net_sockets.h>
#include <mbedtls/ssl.h>
#include <mbedtls/x509_crt.h>
#include <psa/crypto.h>
#include <sys/stat.h>

#include <mutex>

namespace agentspeed::relay::net {

struct CaChain::Certificates {
    mbedtls_x509_crt chain;
};

namespace {

constexpr unsigned char kPersonalization[] = "agent-speed-relay";

bool regularFile(const std::string& path) {
    struct stat info {};
    return ::stat(path.c_str(), &info) == 0 && S_ISREG(info.st_mode);
}

// TLS 1.3 in mbedTLS 3 runs on PSA crypto, which needs one initialisation per process.
bool psaReady() {
    static std::once_flag once;
    static bool ready = false;
    std::call_once(once, [] { ready = psa_crypto_init() == PSA_SUCCESS; });
    return ready;
}

}  // namespace

const std::vector<std::string>& defaultCaBundles() {
    static const std::vector<std::string> kBundles = {
        "/etc/ssl/certs/ca-certificates.crt",
        "/etc/pki/tls/certs/ca-bundle.crt",
        "/etc/ssl/ca-bundle.pem",
        "/etc/ssl/cert.pem",
        "/etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem",
    };
    return kBundles;
}

CaChain::CaChain() : certificates_(std::make_unique<Certificates>()) { mbedtls_x509_crt_init(&certificates_->chain); }

CaChain::~CaChain() { mbedtls_x509_crt_free(&certificates_->chain); }

std::shared_ptr<CaChain> CaChain::load(const std::vector<std::string>& candidates) {
    for (const auto& path : candidates) {
        if (!regularFile(path)) continue;
        auto chain = std::make_shared<CaChain>();
        // A positive result counts certificates that could not be read; the others are kept.
        const int parsed = mbedtls_x509_crt_parse_file(&chain->certificates_->chain, path.c_str());
        if (parsed < 0 || chain->certificates_->chain.raw.len == 0) return nullptr;
        return chain;
    }
    return nullptr;
}

struct TlsStream::State {
    Socket& socket;
    Deadline deadline;
    int cancelFd;
    Io failure = Io::Error;
    mbedtls_entropy_context entropy;
    mbedtls_ctr_drbg_context drbg;
    mbedtls_ssl_config config;
    mbedtls_ssl_context ssl;

    State(Socket& s, Deadline d, int c) : socket(s), deadline(d), cancelFd(c) {
        mbedtls_entropy_init(&entropy);
        mbedtls_ctr_drbg_init(&drbg);
        mbedtls_ssl_config_init(&config);
        mbedtls_ssl_init(&ssl);
    }
    ~State() {
        mbedtls_ssl_free(&ssl);
        mbedtls_ssl_config_free(&config);
        mbedtls_ctr_drbg_free(&drbg);
        mbedtls_entropy_free(&entropy);
    }

    static int send(void* context, const unsigned char* bytes, std::size_t length) {
        auto& s = *static_cast<State*>(context);
        const auto result = s.socket.sendAll(std::string_view(reinterpret_cast<const char*>(bytes), length), s.deadline, s.cancelFd);
        if (result == Io::Ok) return static_cast<int>(length);
        s.failure = result;
        return MBEDTLS_ERR_NET_SEND_FAILED;
    }

    static int receive(void* context, unsigned char* buffer, std::size_t capacity) {
        auto& s = *static_cast<State*>(context);
        std::size_t received = 0;
        const auto result = s.socket.receive(reinterpret_cast<char*>(buffer), capacity, received, s.deadline, s.cancelFd);
        if (result == Io::Ok) return static_cast<int>(received);
        if (result == Io::Closed) return 0;
        s.failure = result;
        return MBEDTLS_ERR_NET_RECV_FAILED;
    }

    bool retry(int code) const {
        return code == MBEDTLS_ERR_SSL_WANT_READ || code == MBEDTLS_ERR_SSL_WANT_WRITE ||
               code == MBEDTLS_ERR_SSL_RECEIVED_NEW_SESSION_TICKET;
    }

    Io failed() const { return failure == Io::Ok || failure == Io::Closed ? Io::Error : failure; }
};

TlsStream::TlsStream(Socket& socket, Deadline deadline, int cancelFd) : state_(std::make_unique<State>(socket, deadline, cancelFd)) {}

TlsStream::~TlsStream() = default;

Io TlsStream::handshake(const std::string& host, const CaChain* ca) {
    auto& s = *state_;
    if (!psaReady()) return Io::Error;
    if (mbedtls_ctr_drbg_seed(&s.drbg, mbedtls_entropy_func, &s.entropy, kPersonalization, sizeof kPersonalization - 1) != 0 ||
        mbedtls_ssl_config_defaults(&s.config, MBEDTLS_SSL_IS_CLIENT, MBEDTLS_SSL_TRANSPORT_STREAM, MBEDTLS_SSL_PRESET_DEFAULT) != 0) {
        return Io::Error;
    }
    mbedtls_ssl_conf_rng(&s.config, mbedtls_ctr_drbg_random, &s.drbg);
    if (ca) {
        mbedtls_ssl_conf_authmode(&s.config, MBEDTLS_SSL_VERIFY_REQUIRED);
        mbedtls_ssl_conf_ca_chain(&s.config, &ca->certificates().chain, nullptr);
    } else {
        mbedtls_ssl_conf_authmode(&s.config, MBEDTLS_SSL_VERIFY_NONE);
    }
    if (mbedtls_ssl_setup(&s.ssl, &s.config) != 0) return Io::Error;
    // The name is checked against the certificate and sent as SNI; the loopback address needs neither.
    if (ca && mbedtls_ssl_set_hostname(&s.ssl, host.c_str()) != 0) return Io::Error;
    mbedtls_ssl_set_bio(&s.ssl, &s, State::send, State::receive, nullptr);
    while (true) {
        const int code = mbedtls_ssl_handshake(&s.ssl);
        if (code == 0) return Io::Ok;
        if (!s.retry(code)) return s.failed();
    }
}

Io TlsStream::write(std::string_view bytes) {
    auto& s = *state_;
    while (!bytes.empty()) {
        const int code = mbedtls_ssl_write(&s.ssl, reinterpret_cast<const unsigned char*>(bytes.data()), bytes.size());
        if (code > 0) {
            bytes.remove_prefix(static_cast<std::size_t>(code));
        } else if (!s.retry(code)) {
            return s.failed();
        }
    }
    return Io::Ok;
}

Io TlsStream::read(char* buffer, std::size_t capacity, std::size_t& received) {
    auto& s = *state_;
    received = 0;
    while (true) {
        const int code = mbedtls_ssl_read(&s.ssl, reinterpret_cast<unsigned char*>(buffer), capacity);
        if (code > 0) {
            received = static_cast<std::size_t>(code);
            return Io::Ok;
        }
        // A close without close_notify ends the stream too: the message framing says whether the answer is whole.
        if (code == 0 || code == MBEDTLS_ERR_SSL_PEER_CLOSE_NOTIFY || code == MBEDTLS_ERR_SSL_CONN_EOF) return Io::Closed;
        if (!s.retry(code)) return s.failed();
    }
}

}  // namespace agentspeed::relay::net
