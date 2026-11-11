#include "net/proxy.hpp"

#include <cctype>
#include <charconv>
#include <vector>

namespace agentspeed::relay::net {

namespace {

std::string lower(std::string_view text) {
    std::string out(text);
    for (auto& c : out) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return out;
}

std::string_view trimmed(std::string_view text) {
    while (!text.empty() && std::isspace(static_cast<unsigned char>(text.front()))) text.remove_prefix(1);
    while (!text.empty() && std::isspace(static_cast<unsigned char>(text.back()))) text.remove_suffix(1);
    return text;
}

std::optional<int> portOf(std::string_view text) {
    int value = 0;
    const auto [end, ec] = std::from_chars(text.data(), text.data() + text.size(), value);
    if (text.empty() || ec != std::errc() || end != text.data() + text.size() || value < 1 || value > 65535) return std::nullopt;
    return value;
}

std::optional<std::string> variableOf(const Variable& variable, std::string_view lowerName, std::string_view upperName) {
    if (!variable) return std::nullopt;
    for (const auto name : {lowerName, upperName}) {
        if (auto value = variable(name); value && !trimmed(*value).empty()) return std::string(trimmed(*value));
    }
    return std::nullopt;
}

bool hostCharacter(char c) { return std::isalnum(static_cast<unsigned char>(c)) || c == '.' || c == '-' || c == '_'; }

}  // namespace

ProxyRoute proxyFrom(std::string_view value) {
    ProxyRoute unusable{ProxyRoute::Kind::Unusable, {}, 0};
    auto rest = trimmed(value);
    if (const auto scheme = rest.find("://"); scheme != std::string_view::npos) {
        if (lower(rest.substr(0, scheme)) != "http") return unusable;
        rest.remove_prefix(scheme + 3);
    }
    if (!rest.empty() && rest.back() == '/') rest.remove_suffix(1);
    if (rest.empty() || rest.find_first_of("@/?#") != std::string_view::npos) return unusable;
    ProxyRoute out{ProxyRoute::Kind::Proxy, {}, 1080};
    std::string_view host = rest;
    if (rest.front() == '[') {
        const auto close = rest.find(']');
        if (close == std::string_view::npos) return unusable;
        host = rest.substr(1, close - 1);
        const auto after = rest.substr(close + 1);
        if (!after.empty()) {
            if (after.front() != ':') return unusable;
            const auto port = portOf(after.substr(1));
            if (!port) return unusable;
            out.port = *port;
        }
        for (const char c : host) {
            if (!std::isxdigit(static_cast<unsigned char>(c)) && c != ':' && c != '.') return unusable;
        }
    } else {
        const auto colon = rest.rfind(':');
        if (colon != std::string_view::npos) {
            host = rest.substr(0, colon);
            const auto port = portOf(rest.substr(colon + 1));
            if (!port) return unusable;
            out.port = *port;
        }
        for (const char c : host) {
            if (!hostCharacter(c)) return unusable;
        }
    }
    if (host.empty()) return unusable;
    out.host = lower(host);
    return out;
}

bool bypassesProxy(std::string_view noProxy, std::string_view host, int port) {
    const auto name = lower(host);
    std::size_t start = 0;
    while (start <= noProxy.size()) {
        const auto comma = noProxy.find(',', start);
        auto entry = trimmed(noProxy.substr(start, comma == std::string_view::npos ? std::string_view::npos : comma - start));
        start = comma == std::string_view::npos ? noProxy.size() + 1 : comma + 1;
        if (entry.empty()) continue;
        if (entry == "*") return true;
        std::optional<int> entryPort;
        if (const auto colon = entry.rfind(':'); colon != std::string_view::npos && entry.find(':') == colon) {
            entryPort = portOf(entry.substr(colon + 1));
            if (!entryPort) continue;
            entry = entry.substr(0, colon);
        }
        if (entry.substr(0, 2) == "*.") entry.remove_prefix(1);
        if (!entry.empty() && entry.front() == '.') entry.remove_prefix(1);
        if (entry.empty() || (entryPort && *entryPort != port)) continue;
        const auto domain = lower(entry);
        if (name == domain) return true;
        if (name.size() > domain.size() && name.compare(name.size() - domain.size(), domain.size(), domain) == 0 &&
            name[name.size() - domain.size() - 1] == '.') {
            return true;
        }
    }
    return false;
}

ProxyRoute proxyFor(std::string_view host, int port, const Variable& variable) {
    const auto proxy = variableOf(variable, "https_proxy", "HTTPS_PROXY");
    if (!proxy) return {};
    if (const auto noProxy = variableOf(variable, "no_proxy", "NO_PROXY"); noProxy && bypassesProxy(*noProxy, host, port)) return {};
    return proxyFrom(*proxy);
}

std::string connectRequest(std::string_view host, int port, std::string_view userAgent) {
    const auto authority = std::string(host) + ":" + std::to_string(port);
    return "CONNECT " + authority + " HTTP/1.1\r\nHost: " + authority + "\r\nUser-Agent: " + std::string(userAgent) + "\r\n\r\n";
}

}  // namespace agentspeed::relay::net
