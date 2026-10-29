#include <algorithm>
#include <cctype>
#include <chrono>

#include "agentspeed/report.hpp"

namespace agentspeed::report {

namespace {

struct UrlParts {
    std::string_view scheme, netloc, path, query;
};

UrlParts split(std::string_view url) {
    UrlParts parts;
    if (const auto colon = url.find("://"); colon != std::string_view::npos) {
        parts.scheme = url.substr(0, colon);
        url.remove_prefix(colon + 3);
        const auto end = url.find_first_of("/?#");
        parts.netloc = url.substr(0, end);
        url = end == std::string_view::npos ? std::string_view() : url.substr(end);
    }
    if (const auto hash = url.find('#'); hash != std::string_view::npos) url = url.substr(0, hash);
    if (const auto question = url.find('?'); question != std::string_view::npos) {
        parts.query = url.substr(question + 1);
        url = url.substr(0, question);
    }
    parts.path = url;
    return parts;
}

bool hexLower(char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); }
bool b64Char(char c) { return std::isalnum(static_cast<unsigned char>(c)) || c == '-' || c == '_'; }

bool isUuid(std::string_view s) {
    if (s.size() != 36) return false;
    for (std::size_t i = 0; i < 36; ++i) {
        const bool dash = i == 8 || i == 13 || i == 18 || i == 23;
        if (dash ? s[i] != '-' : !hexLower(s[i])) return false;
    }
    return s[14] == '4' && (s[19] == '8' || s[19] == '9' || s[19] == 'a' || s[19] == 'b');
}

bool isTimestamp(std::string_view s) {
    return s.size() == 10 && s[0] >= '1' && s[0] <= '9' && std::all_of(s.begin(), s.end(), [](char c) { return c >= '0' && c <= '9'; });
}

bool isB64(std::string_view s, std::size_t length, std::size_t bytes) {
    if (s.size() != length || !std::all_of(s.begin(), s.end(), b64Char)) return false;
    const auto decoded = b64urlDecode(s);
    return decoded && decoded->size() == bytes;
}

std::string header(const std::map<std::string, std::string>& headers, const std::string& name) {
    const auto it = headers.find(name);
    return it == headers.end() ? std::string() : it->second;
}

}  // namespace

std::string authorityOf(std::string_view url) {
    const auto parts = split(url);
    auto netloc = parts.netloc;
    if (const auto at = netloc.rfind('@'); at != std::string_view::npos) netloc.remove_prefix(at + 1);
    std::string_view host = netloc, port;
    if (!netloc.empty() && netloc.front() == '[') {
        const auto close = netloc.find(']');
        host = netloc.substr(1, close == std::string_view::npos ? std::string_view::npos : close - 1);
        if (close != std::string_view::npos && close + 1 < netloc.size() && netloc[close + 1] == ':') port = netloc.substr(close + 2);
    } else if (const auto colon = netloc.rfind(':'); colon != std::string_view::npos) {
        host = netloc.substr(0, colon);
        port = netloc.substr(colon + 1);
    }
    std::string out(host);
    std::transform(out.begin(), out.end(), out.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    if (out.find(':') != std::string::npos) out = "[" + out + "]";
    const std::string_view fallback = parts.scheme == "https" || parts.scheme == "wss" ? "443" : parts.scheme == "http" || parts.scheme == "ws" ? "80" : "";
    std::string_view digits = port;
    while (digits.size() > 1 && digits.front() == '0') digits.remove_prefix(1);
    if (!digits.empty() && digits != fallback) out += ":" + std::string(digits);
    return out;
}

std::string pathOf(std::string_view url) {
    const auto parts = split(url);
    std::string out = parts.path.empty() ? std::string("/") : std::string(parts.path);
    if (!parts.query.empty()) out += "?" + std::string(parts.query);
    return out;
}

std::string canonicalString(std::string_view method, std::string_view authority, std::string_view path, std::string_view timestamp,
                            std::string_view nonce, std::string_view deviceId, std::string_view body) {
    std::string out(kLabel);
    for (const auto part : {method, authority, path, timestamp, nonce, deviceId}) {
        out += '\n';
        out += part;
    }
    out += '\n';
    out += sha256Hex(body);
    return out;
}

Headers signedHeaders(const PrivateKey& key, std::string_view method, std::string_view url, std::string_view deviceId,
                      std::string_view body, const SignOptions& options) {
    const auto seconds = options.timestamp > 0
                             ? options.timestamp
                             : std::chrono::duration_cast<std::chrono::seconds>(std::chrono::system_clock::now().time_since_epoch()).count();
    const auto timestamp = std::to_string(seconds);
    const auto nonce = options.nonce.empty() ? newNonce() : options.nonce;
    const auto canonical = canonicalString(method, authorityOf(url), pathOf(url), timestamp, nonce, deviceId, body);
    Headers headers{{"User-Agent", "AgentSpeed/" + options.version + " (" + options.platform + ")"}, {"Accept", "application/json"}};
    if (!body.empty()) headers.emplace_back("Content-Type", "application/json");
    headers.emplace_back("X-AgentSpeed-Device", std::string(deviceId));
    headers.emplace_back("X-AgentSpeed-Timestamp", timestamp);
    headers.emplace_back("X-AgentSpeed-Nonce", nonce);
    headers.emplace_back("X-AgentSpeed-Signature", b64url(sign(key, canonical)));
    if (options.newKey) headers.emplace_back("X-AgentSpeed-New-Key-Signature", b64url(sign(*options.newKey, canonical)));
    if (!options.apiKey.empty()) headers.emplace_back("Authorization", "Bearer " + options.apiKey);
    return headers;
}

Verdict verifyRequest(const PublicKey& key, std::string_view method, std::string_view authority, std::string_view path,
                      const std::map<std::string, std::string>& headers, std::string_view body, std::string_view name) {
    const auto device = header(headers, "x-agentspeed-device");
    const auto timestamp = header(headers, "x-agentspeed-timestamp");
    const auto nonce = header(headers, "x-agentspeed-nonce");
    const auto signature = header(headers, std::string(name));
    if (!isUuid(device) || !isTimestamp(timestamp) || !isB64(nonce, 22, 16) || !isB64(header(headers, "x-agentspeed-signature"), 86, 64) ||
        !isB64(signature, 86, 64))
        return Verdict::Malformed;
    const auto canonical = canonicalString(method, authority, path, timestamp, nonce, device, body);
    return verify(key, canonical, *b64urlDecode(signature)) ? Verdict::Valid : Verdict::Invalid;
}

}  // namespace agentspeed::report
