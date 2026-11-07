#include <openssl/crypto.h>

#include <algorithm>

#include "agentspeed/lan_protocol.hpp"
#include "agentspeed/text.hpp"

namespace agentspeed::lan {

namespace {

int hexValue(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

}  // namespace

std::string normalizeKey(std::string_view text) {
    std::string out;
    for (const char c : text) {
        if (c == '-' || c == ' ') continue;
        out += c >= 'a' && c <= 'z' ? static_cast<char>(c - 32) : c;
    }
    return out;
}

bool keyMatches(const std::optional<std::string>& given, std::string_view stored) {
    if (!given) return false;
    const auto candidate = normalizeKey(*given);
    if (candidate.size() != stored.size() || stored.empty()) return false;
    return CRYPTO_memcmp(candidate.data(), stored.data(), stored.size()) == 0;
}

// Python's urllib.parse.unquote: %XX sequences become bytes, read as UTF-8 with replacement; `+` stays.
std::string percentDecode(std::string_view text) {
    std::string bytes;
    for (std::size_t i = 0; i < text.size(); ++i) {
        if (text[i] == '%' && i + 2 < text.size()) {
            const int hi = hexValue(text[i + 1]), lo = hexValue(text[i + 2]);
            if (hi >= 0 && lo >= 0) {
                bytes += static_cast<char>(hi * 16 + lo);
                i += 2;
                continue;
            }
        }
        bytes += text[i];
    }
    return text::sanitizeUtf8(bytes);
}

std::optional<std::string> requestKey(const Head& head) {
    if (const auto* auth = head.header("authorization"); auth && !auth->empty()) {
        const auto space = auth->find(' ');
        auto scheme = auth->substr(0, space);
        std::transform(scheme.begin(), scheme.end(), scheme.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        const auto token = space == std::string::npos ? std::string() : text::strip(std::string_view(*auth).substr(space + 1));
        if (scheme == "bearer" && !token.empty()) return token;
    }
    const auto question = head.target.find('?');
    if (question == std::string::npos) return std::nullopt;
    const std::string_view query = std::string_view(head.target).substr(question + 1);
    for (std::size_t start = 0; start <= query.size();) {
        const auto amp = query.find('&', start);
        const auto part = query.substr(start, amp == std::string_view::npos ? std::string_view::npos : amp - start);
        if (const auto eq = part.find('='); eq != std::string_view::npos && part.substr(0, eq) == "key") return percentDecode(part.substr(eq + 1));
        if (amp == std::string_view::npos) break;
        start = amp + 1;
    }
    return std::nullopt;
}

std::optional<std::int64_t> Lockout::locked(std::string_view address, std::int64_t nowMs) const {
    for (const auto& e : entries_) {
        if (e.address != address) continue;
        if (e.until && nowMs < *e.until) return (*e.until - nowMs + 999) / 1000;
        return std::nullopt;
    }
    return std::nullopt;
}

void Lockout::fail(std::string_view address, std::int64_t nowMs) {
    Entry entry{std::string(address), {}, std::nullopt};
    if (const auto it = std::find_if(entries_.begin(), entries_.end(), [&](const Entry& e) { return e.address == address; }); it != entries_.end()) {
        entry = std::move(*it);
        entries_.erase(it);
    }
    std::erase_if(entry.failures, [&](std::int64_t t) { return nowMs - t >= kWindowMs; });
    entry.failures.push_back(nowMs);
    if (entry.failures.size() >= static_cast<std::size_t>(kFailures)) {
        entry.until = nowMs + kLockMs;
        entry.failures.clear();
    }
    entries_.push_back(std::move(entry));
    while (entries_.size() > kAddresses) entries_.erase(entries_.begin());
}

void Lockout::succeed(std::string_view address) {
    std::erase_if(entries_, [&](const Entry& e) { return e.address == address; });
}

}  // namespace agentspeed::lan
