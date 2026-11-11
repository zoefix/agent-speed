#include "net/url.hpp"

#include <cctype>
#include <charconv>

namespace agentspeed::relay::net {

namespace {

bool hostCharacter(char c) { return std::isalnum(static_cast<unsigned char>(c)) || c == '.' || c == '-'; }

bool targetCharacter(char c) {
    const auto u = static_cast<unsigned char>(c);
    return u > 0x20 && u < 0x7F && c != '#';
}

}  // namespace

std::optional<Url> parseUrl(std::string_view url) {
    Url out;
    std::string_view rest;
    if (url.substr(0, 8) == "https://") {
        rest = url.substr(8);
    } else if (url.substr(0, 7) == "http://") {
        out.tls = false;
        out.port = 80;
        rest = url.substr(7);
    } else {
        return std::nullopt;
    }
    const auto slash = rest.find('/');
    const auto authority = rest.substr(0, slash);
    out.target = slash == std::string_view::npos ? "/" : std::string(rest.substr(slash));
    const auto colon = authority.find(':');
    const auto host = authority.substr(0, colon);
    if (host.empty() || host.size() > 253) return std::nullopt;
    for (const char c : host) {
        if (!hostCharacter(c)) return std::nullopt;
        out.host += static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    }
    if (colon != std::string_view::npos) {
        const auto port = authority.substr(colon + 1);
        int value = 0;
        const auto [end, ec] = std::from_chars(port.data(), port.data() + port.size(), value);
        if (port.empty() || ec != std::errc() || end != port.data() + port.size() || value < 1 || value > 65535) return std::nullopt;
        out.port = value;
    }
    for (const char c : out.target) {
        if (!targetCharacter(c)) return std::nullopt;
    }
    return out;
}

}  // namespace agentspeed::relay::net
