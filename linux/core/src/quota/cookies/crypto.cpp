#include "agentspeed/quota/cookies/crypto.hpp"

#include <openssl/evp.h>

#include <cstdint>
#include <memory>

#include "agentspeed/quota/routes/fingerprint.hpp"

namespace agentspeed::quota::cookies {

namespace {

constexpr std::string_view kSalt = "saltysalt";
constexpr std::size_t kHashBytes = 32;

struct CipherFree {
    void operator()(EVP_CIPHER_CTX* ctx) const { EVP_CIPHER_CTX_free(ctx); }
};
using Cipher = std::unique_ptr<EVP_CIPHER_CTX, CipherFree>;

const unsigned char* bytes(std::string_view text) { return reinterpret_cast<const unsigned char*>(text.data()); }
unsigned char* bytes(std::string& text) { return reinterpret_cast<unsigned char*>(text.data()); }

bool validUtf8(std::string_view text) {
    std::size_t i = 0;
    while (i < text.size()) {
        const auto c = static_cast<unsigned char>(text[i]);
        std::size_t extra = 0;
        std::uint32_t point = 0;
        if (c < 0x80) {
            ++i;
            continue;
        } else if ((c & 0xE0) == 0xC0) {
            extra = 1;
            point = c & 0x1Fu;
        } else if ((c & 0xF0) == 0xE0) {
            extra = 2;
            point = c & 0x0Fu;
        } else if ((c & 0xF8) == 0xF0) {
            extra = 3;
            point = c & 0x07u;
        } else {
            return false;
        }
        if (i + extra >= text.size()) return false;
        for (std::size_t k = 1; k <= extra; ++k) {
            const auto next = static_cast<unsigned char>(text[i + k]);
            if ((next & 0xC0) != 0x80) return false;
            point = point << 6 | (next & 0x3Fu);
        }
        const std::uint32_t minimum = extra == 1 ? 0x80 : extra == 2 ? 0x800 : 0x10000;
        if (point < minimum || point > 0x10FFFF || (point >= 0xD800 && point <= 0xDFFF)) return false;
        i += extra + 1;
    }
    return true;
}

// Drops (and checks) the host hash, then insists on UTF-8.
std::optional<std::string> plaintext(std::string& plain, int metaVersion, std::optional<std::string_view> host) {
    if (metaVersion >= kHostHashVersion) {
        if (plain.size() < kHashBytes) {
            wipe(plain);
            return std::nullopt;
        }
        if (host) {
            auto expected = sha256(*host);
            const bool same = plain.compare(0, kHashBytes, expected) == 0;
            wipe(expected);
            if (!same) {
                wipe(plain);
                return std::nullopt;
            }
        }
        std::string value = plain.substr(kHashBytes);
        wipe(plain);
        plain = std::move(value);
    }
    if (!validUtf8(plain)) {
        wipe(plain);
        return std::nullopt;
    }
    return std::move(plain);
}

}  // namespace

bool isVersioned(std::string_view blob) {
    return blob.size() >= 3 && blob[0] == 'v' && blob[1] == '1' && (blob[2] == '0' || blob[2] == '1');
}

std::optional<std::string> cbcKey(std::string_view password, int iterations) {
    if (iterations <= 0) return std::nullopt;
    std::string key(16, '\0');
    if (PKCS5_PBKDF2_HMAC(password.data(), static_cast<int>(password.size()), bytes(kSalt), static_cast<int>(kSalt.size()),
                          iterations, EVP_sha1(), static_cast<int>(key.size()), bytes(key)) != 1) {
        wipe(key);
        return std::nullopt;
    }
    return key;
}

std::optional<std::string> decryptCbc(std::string_view blob, std::string_view key, int metaVersion,
                                      std::optional<std::string_view> host) {
    if (blob.size() <= 3 || !isVersioned(blob) || key.size() != 16) return std::nullopt;
    const auto body = blob.substr(3);
    if (body.empty() || body.size() % 16 != 0 || body.size() > (1u << 20)) return std::nullopt;
    const std::string iv(16, ' ');
    Cipher ctx(EVP_CIPHER_CTX_new());
    if (!ctx || EVP_DecryptInit_ex(ctx.get(), EVP_aes_128_cbc(), nullptr, bytes(key), bytes(iv)) != 1) return std::nullopt;
    std::string plain(body.size() + 16, '\0');
    int written = 0, tail = 0;
    if (EVP_DecryptUpdate(ctx.get(), bytes(plain), &written, bytes(body), static_cast<int>(body.size())) != 1 ||
        EVP_DecryptFinal_ex(ctx.get(), bytes(plain) + written, &tail) != 1) {
        wipe(plain);
        return std::nullopt;
    }
    plain.resize(static_cast<std::size_t>(written + tail));
    return plaintext(plain, metaVersion, host);
}

std::optional<std::string> decryptGcm(std::string_view blob, std::string_view key, int metaVersion,
                                      std::optional<std::string_view> host) {
    constexpr std::size_t kNonce = 12, kTag = 16;
    if (blob.size() < 3 + kNonce + kTag || !isVersioned(blob) || key.size() != 32) return std::nullopt;
    const auto nonce = blob.substr(3, kNonce);
    const auto cipher = blob.substr(3 + kNonce, blob.size() - 3 - kNonce - kTag);
    std::string tag(blob.substr(blob.size() - kTag));
    Cipher ctx(EVP_CIPHER_CTX_new());
    if (!ctx || EVP_DecryptInit_ex(ctx.get(), EVP_aes_256_gcm(), nullptr, nullptr, nullptr) != 1 ||
        EVP_CIPHER_CTX_ctrl(ctx.get(), EVP_CTRL_GCM_SET_IVLEN, static_cast<int>(kNonce), nullptr) != 1 ||
        EVP_DecryptInit_ex(ctx.get(), nullptr, nullptr, bytes(key), bytes(nonce)) != 1) {
        return std::nullopt;
    }
    std::string plain(cipher.size() + 16, '\0');
    int written = 0, tail = 0;
    if (EVP_DecryptUpdate(ctx.get(), bytes(plain), &written, bytes(cipher), static_cast<int>(cipher.size())) != 1 ||
        EVP_CIPHER_CTX_ctrl(ctx.get(), EVP_CTRL_GCM_SET_TAG, static_cast<int>(kTag), tag.data()) != 1 ||
        EVP_DecryptFinal_ex(ctx.get(), bytes(plain) + written, &tail) != 1) {
        wipe(plain);
        return std::nullopt;
    }
    plain.resize(static_cast<std::size_t>(written + tail));
    return plaintext(plain, metaVersion, host);
}

}  // namespace agentspeed::quota::cookies
