#include <openssl/evp.h>
#include <openssl/hmac.h>

#include "agentspeed/lan_frame.hpp"

namespace agentspeed::lan {

std::string signature(std::string_view key, std::string_view body) {
    unsigned char digest[EVP_MAX_MD_SIZE];
    unsigned int length = 0;
    HMAC(EVP_sha256(), key.data(), static_cast<int>(key.size()), reinterpret_cast<const unsigned char*>(body.data()), body.size(), digest,
         &length);
    static constexpr char kHex[] = "0123456789abcdef";
    std::string out = "sha256=";
    for (unsigned int i = 0; i < length; ++i) {
        out += kHex[digest[i] >> 4];
        out += kHex[digest[i] & 15];
    }
    return out;
}

Datagram datagram(const Frame& frame, const std::optional<std::string>& key) {
    Datagram out;
    const auto make = [&](bool withSources, bool withHistory) {
        auto body = frame.json(withSources, withHistory);
        if (key) body += "\n" + signature(*key, body);
        return body;
    };
    out.bytes = make(true, true);
    if (out.bytes.size() <= kDatagramLimit) return out;
    out.historyOmitted = true;
    out.bytes = make(true, false);
    if (out.bytes.size() <= kDatagramLimit) return out;
    out.sourcesOmitted = true;
    out.bytes = make(false, false);
    return out;
}

}  // namespace agentspeed::lan
