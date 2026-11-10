#include "agentspeed/quota/cookies/header.hpp"

#include "agentspeed/quota/routes/fingerprint.hpp"

namespace agentspeed::quota::cookies {

namespace {

bool control(unsigned char c) { return c < 0x20 || c == 0x7F; }

bool endsWith(std::string_view text, std::string_view suffix) {
    return text.size() >= suffix.size() && text.substr(text.size() - suffix.size()) == suffix;
}

}  // namespace

bool hostMatches(std::string_view host, std::string_view site) {
    if (site.empty()) return false;
    std::string_view bare = host;
    while (!bare.empty() && bare.front() == '.') bare.remove_prefix(1);
    const std::string dotted = "." + std::string(site);
    return bare == site || endsWith(bare, dotted) || host == dotted;
}

bool live(const Cookie& cookie, std::int64_t nowS) { return cookie.expiresS == 0 || cookie.expiresS > nowS; }

std::vector<Cookie> usable(const std::vector<Cookie>& cookies, std::string_view site, std::int64_t nowS) {
    std::vector<Cookie> out;
    for (const auto& cookie : cookies) {
        if (hostMatches(cookie.host, site) && live(cookie, nowS)) out.push_back(cookie);
    }
    return out;
}

bool sendable(const Cookie& cookie) {
    if (cookie.name.empty()) return false;
    for (const char c : cookie.name) {
        if (control(static_cast<unsigned char>(c)) || c == ';' || c == '=') return false;
    }
    for (const char c : cookie.value) {
        if (control(static_cast<unsigned char>(c))) return false;
    }
    return true;
}

std::optional<std::string> joinHeader(const std::vector<Cookie>& cookies, std::string_view site, std::int64_t nowS) {
    std::string header;
    for (const auto& cookie : cookies) {
        if (!hostMatches(cookie.host, site) || !live(cookie, nowS) || !sendable(cookie)) continue;
        if (!header.empty()) header += "; ";
        header += cookie.name;
        header += '=';
        header += cookie.value;
    }
    if (header.empty()) return std::nullopt;
    return header;
}

void wipe(std::vector<Cookie>& cookies) {
    for (auto& cookie : cookies) {
        quota::wipe(cookie.value);
        quota::wipe(cookie.encrypted);
    }
    cookies.clear();
}

}  // namespace agentspeed::quota::cookies
