// The classic EC API keeps scalar and point arithmetic short; it is deprecated in OpenSSL 3 but complete.
#define OPENSSL_API_COMPAT 0x10101000L
#include <openssl/bn.h>
#include <openssl/ec.h>
#include <openssl/ecdsa.h>
#include <openssl/evp.h>
#include <openssl/obj_mac.h>
#include <openssl/rand.h>

#include <memory>

#include "agentspeed/report.hpp"

namespace agentspeed::report {

namespace {

struct Free {
    void operator()(EC_KEY* p) const { EC_KEY_free(p); }
    void operator()(EC_POINT* p) const { EC_POINT_free(p); }
    void operator()(BIGNUM* p) const { BN_free(p); }
    void operator()(ECDSA_SIG* p) const { ECDSA_SIG_free(p); }
    void operator()(BN_CTX* p) const { BN_CTX_free(p); }
};
template <typename T>
using Owned = std::unique_ptr<T, Free>;

const EC_GROUP* group() {
    static const EC_GROUP* g = EC_GROUP_new_by_curve_name(NID_X9_62_prime256v1);
    return g;
}

std::string digest(std::string_view message) {
    unsigned char out[EVP_MAX_MD_SIZE];
    unsigned int length = 0;
    EVP_Digest(message.data(), message.size(), out, &length, EVP_sha256(), nullptr);
    return std::string(reinterpret_cast<char*>(out), length);
}

Owned<EC_POINT> pointOf(const PublicKey& key) {
    Owned<BIGNUM> x(BN_bin2bn(key.x.data(), 32, nullptr)), y(BN_bin2bn(key.y.data(), 32, nullptr));
    Owned<EC_POINT> point(EC_POINT_new(group()));
    Owned<BN_CTX> ctx(BN_CTX_new());
    if (!EC_POINT_set_affine_coordinates(group(), point.get(), x.get(), y.get(), ctx.get())) return nullptr;
    if (EC_POINT_is_on_curve(group(), point.get(), ctx.get()) != 1) return nullptr;
    return point;
}

Owned<EC_KEY> keyOf(const PrivateKey& key) {
    Owned<EC_KEY> ec(EC_KEY_new_by_curve_name(NID_X9_62_prime256v1));
    Owned<BIGNUM> d(BN_bin2bn(key.d.data(), 32, nullptr));
    EC_KEY_set_private_key(ec.get(), d.get());
    Owned<EC_POINT> pub(EC_POINT_new(group()));
    Owned<BN_CTX> ctx(BN_CTX_new());
    EC_POINT_mul(group(), pub.get(), d.get(), nullptr, nullptr, ctx.get());
    EC_KEY_set_public_key(ec.get(), pub.get());
    return ec;
}

constexpr char kAlphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

}  // namespace

std::string b64url(std::string_view bytes) {
    std::string out;
    std::uint32_t buffer = 0;
    int bits = 0;
    for (const unsigned char c : bytes) {
        buffer = (buffer << 8) | c;
        bits += 8;
        while (bits >= 6) {
            bits -= 6;
            out += kAlphabet[(buffer >> bits) & 63];
        }
    }
    if (bits > 0) out += kAlphabet[(buffer << (6 - bits)) & 63];
    return out;
}

std::optional<std::string> b64urlDecode(std::string_view text) {
    if (text.size() % 4 == 1) return std::nullopt;
    std::string out;
    std::uint32_t buffer = 0;
    int bits = 0;
    for (const char c : text) {
        const char* at = std::char_traits<char>::find(kAlphabet, 64, c);
        if (!at) return std::nullopt;
        buffer = (buffer << 6) | static_cast<std::uint32_t>(at - kAlphabet);
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            out += static_cast<char>((buffer >> bits) & 0xFF);
        }
    }
    if (bits > 0 && (buffer & ((1U << bits) - 1)) != 0) return std::nullopt;  // non-zero trailing bits
    return out;
}

std::string sha256Hex(std::string_view bytes) {
    static constexpr char kHex[] = "0123456789abcdef";
    std::string out;
    for (const unsigned char c : digest(bytes)) {
        out += kHex[c >> 4];
        out += kHex[c & 15];
    }
    return out;
}

PrivateKey PrivateKey::generate() {
    Owned<EC_KEY> ec(EC_KEY_new_by_curve_name(NID_X9_62_prime256v1));
    EC_KEY_generate_key(ec.get());
    PrivateKey key;
    BN_bn2binpad(EC_KEY_get0_private_key(ec.get()), key.d.data(), 32);
    return key;
}

std::optional<PrivateKey> PrivateKey::fromHex(std::string_view hex) {
    if (hex.size() != 64) return std::nullopt;
    PrivateKey key;
    for (std::size_t i = 0; i < 32; ++i) {
        unsigned value = 0;
        for (std::size_t k = 0; k < 2; ++k) {
            const char c = hex[2 * i + k];
            const int v = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
            if (v < 0) return std::nullopt;
            value = value * 16 + static_cast<unsigned>(v);
        }
        key.d[i] = static_cast<std::uint8_t>(value);
    }
    return key;
}

std::string PrivateKey::hex() const {
    static constexpr char kHex[] = "0123456789abcdef";
    std::string out;
    for (const auto c : d) {
        out += kHex[c >> 4];
        out += kHex[c & 15];
    }
    return out;
}

PublicKey PrivateKey::publicKey() const {
    const auto ec = keyOf(*this);
    Owned<BIGNUM> x(BN_new()), y(BN_new());
    Owned<BN_CTX> ctx(BN_CTX_new());
    EC_POINT_get_affine_coordinates(group(), EC_KEY_get0_public_key(ec.get()), x.get(), y.get(), ctx.get());
    PublicKey pub;
    BN_bn2binpad(x.get(), pub.x.data(), 32);
    BN_bn2binpad(y.get(), pub.y.data(), 32);
    return pub;
}

std::string sign(const PrivateKey& key, std::string_view message) {
    const auto ec = keyOf(key);
    const auto hash = digest(message);
    Owned<ECDSA_SIG> sig(ECDSA_do_sign(reinterpret_cast<const unsigned char*>(hash.data()), static_cast<int>(hash.size()), ec.get()));
    if (!sig) return {};
    const BIGNUM *r = nullptr, *s = nullptr;
    ECDSA_SIG_get0(sig.get(), &r, &s);
    std::string out(64, '\0');
    BN_bn2binpad(r, reinterpret_cast<unsigned char*>(out.data()), 32);
    BN_bn2binpad(s, reinterpret_cast<unsigned char*>(out.data()) + 32, 32);
    return out;
}

bool verify(const PublicKey& key, std::string_view message, std::string_view signature) {
    if (signature.size() != 64) return false;
    auto point = pointOf(key);
    if (!point) return false;
    Owned<EC_KEY> ec(EC_KEY_new_by_curve_name(NID_X9_62_prime256v1));
    EC_KEY_set_public_key(ec.get(), point.get());
    Owned<ECDSA_SIG> sig(ECDSA_SIG_new());
    auto* r = BN_bin2bn(reinterpret_cast<const unsigned char*>(signature.data()), 32, nullptr);
    auto* s = BN_bin2bn(reinterpret_cast<const unsigned char*>(signature.data()) + 32, 32, nullptr);
    ECDSA_SIG_set0(sig.get(), r, s);
    const auto hash = digest(message);
    return ECDSA_do_verify(reinterpret_cast<const unsigned char*>(hash.data()), static_cast<int>(hash.size()), sig.get(), ec.get()) == 1;
}

std::string jwk(const PublicKey& key) {
    const auto x = b64url(std::string_view(reinterpret_cast<const char*>(key.x.data()), 32));
    const auto y = b64url(std::string_view(reinterpret_cast<const char*>(key.y.data()), 32));
    return R"({"kty":"EC","crv":"P-256","x":")" + x + R"(","y":")" + y + R"("})";
}

std::optional<PublicKey> parseJwk(const json::Value& value) {
    if (!value.isObject() || !value["kty"].equals("EC") || !value["crv"].equals("P-256") || value["d"].valid()) return std::nullopt;
    const auto x = value["x"].string(), y = value["y"].string();
    if (!x || !y) return std::nullopt;
    const auto bx = b64urlDecode(*x), by = b64urlDecode(*y);
    if (!bx || !by || bx->size() != 32 || by->size() != 32) return std::nullopt;
    PublicKey key;
    std::copy(bx->begin(), bx->end(), key.x.begin());
    std::copy(by->begin(), by->end(), key.y.begin());
    // Coordinates must be below p and on the curve.
    Owned<BIGNUM> p(BN_new()), bnx(BN_bin2bn(key.x.data(), 32, nullptr)), bny(BN_bin2bn(key.y.data(), 32, nullptr));
    EC_GROUP_get_curve(group(), p.get(), nullptr, nullptr, nullptr);
    if (BN_cmp(bnx.get(), p.get()) >= 0 || BN_cmp(bny.get(), p.get()) >= 0) return std::nullopt;
    if (!pointOf(key)) return std::nullopt;
    return key;
}

std::string spki(const PublicKey& key) {
    static const unsigned char kPrefix[] = {0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01,
                                            0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04};
    std::string out(reinterpret_cast<const char*>(kPrefix), sizeof kPrefix);
    out.append(reinterpret_cast<const char*>(key.x.data()), 32);
    out.append(reinterpret_cast<const char*>(key.y.data()), 32);
    return out;
}

std::string thumbprint(const PublicKey& key) {
    const auto x = b64url(std::string_view(reinterpret_cast<const char*>(key.x.data()), 32));
    const auto y = b64url(std::string_view(reinterpret_cast<const char*>(key.y.data()), 32));
    return b64url(digest(R"({"crv":"P-256","kty":"EC","x":")" + x + R"(","y":")" + y + R"("})"));
}

std::string newNonce() {
    unsigned char bytes[16];
    RAND_bytes(bytes, sizeof bytes);
    return b64url(std::string_view(reinterpret_cast<char*>(bytes), sizeof bytes));
}

std::string newDeviceId() {
    unsigned char b[16];
    RAND_bytes(b, sizeof b);
    b[6] = static_cast<unsigned char>((b[6] & 0x0F) | 0x40);
    b[8] = static_cast<unsigned char>((b[8] & 0x3F) | 0x80);
    static constexpr char kHex[] = "0123456789abcdef";
    std::string out;
    for (int i = 0; i < 16; ++i) {
        if (i == 4 || i == 6 || i == 8 || i == 10) out += '-';
        out += kHex[b[i] >> 4];
        out += kHex[b[i] & 15];
    }
    return out;
}

}  // namespace agentspeed::report
