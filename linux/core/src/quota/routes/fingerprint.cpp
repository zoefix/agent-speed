#include "agentspeed/quota/routes/fingerprint.hpp"

#include <openssl/crypto.h>
#include <openssl/evp.h>

namespace agentspeed::quota {

std::string sha256(std::string_view bytes) {
    unsigned char digest[EVP_MAX_MD_SIZE];
    unsigned int length = 0;
    if (EVP_Digest(bytes.data(), bytes.size(), digest, &length, EVP_sha256(), nullptr) != 1) return {};
    return std::string(reinterpret_cast<const char*>(digest), length);
}

Fingerprint fingerprintOf(std::string_view credential) {
    const auto digest = sha256(credential);
    std::uint64_t value = 0;
    for (std::size_t i = 0; i < 8 && i < digest.size(); ++i) value = value << 8 | static_cast<unsigned char>(digest[i]);
    return Fingerprint{value};
}

void wipe(std::string& secret) {
    if (!secret.empty()) OPENSSL_cleanse(secret.data(), secret.size());
    secret.clear();
}

bool headerSafe(std::string_view value) {
    if (value.empty() || value.size() > 8192) return false;
    for (const char c : value) {
        const auto byte = static_cast<unsigned char>(c);
        if (byte < 0x20 || byte > 0x7E) return false;
    }
    return true;
}

}  // namespace agentspeed::quota
