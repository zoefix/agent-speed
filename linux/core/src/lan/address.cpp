#include <arpa/inet.h>

#include <algorithm>
#include <cstring>

#include "agentspeed/lan_address.hpp"
#include "agentspeed/text.hpp"

namespace agentspeed::lan {

namespace {

std::optional<std::array<std::uint8_t, 4>> ipv4(std::string_view text) {
    std::array<std::uint8_t, 4> out{};
    const std::string copy(text);
    if (inet_pton(AF_INET, copy.c_str(), out.data()) != 1) return std::nullopt;
    return out;
}

std::optional<std::array<std::uint8_t, 16>> ipv6(std::string_view text) {
    std::array<std::uint8_t, 16> out{};
    const std::string copy(text.substr(0, text.find('%')));
    if (inet_pton(AF_INET6, copy.c_str(), out.data()) != 1) return std::nullopt;
    return out;
}

bool labelOk(std::string_view label) {
    const auto edge = [](char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'; };
    if (label.empty() || label.size() > 63 || !edge(label.front()) || !edge(label.back())) return false;
    return std::all_of(label.begin(), label.end(), [&](char c) { return edge(c) || c == '-'; });
}

bool allDigits(std::string_view text) {
    return !text.empty() && std::all_of(text.begin(), text.end(), [](char c) { return c >= '0' && c <= '9'; });
}

// A host as written: `[IPv6]`, an IPv4 address or a DNS name.
std::variant<TargetAddress, std::string> hostOf(std::string_view text) {
    TargetAddress out;
    if (!text.empty() && text.front() == '[') {
        if (text.back() != ']' || text.size() < 2) return std::string("host");
        const auto inner = text.substr(1, text.size() - 2);
        if (!ipv6(inner)) return std::string("host");
        out.host = std::string(inner);
        out.literal = true;
        return out;
    }
    if (text.empty() || text.size() > 253) return std::string("host");
    std::vector<std::string_view> labels;
    if (text == ".") {
        labels.push_back({});
    } else {
        auto rest = text;
        while (!rest.empty() && rest.back() == '.') rest.remove_suffix(1);
        std::size_t start = 0;
        while (true) {
            const auto dot = rest.find('.', start);
            labels.push_back(rest.substr(start, dot == std::string_view::npos ? std::string_view::npos : dot - start));
            if (dot == std::string_view::npos) break;
            start = dot + 1;
        }
    }
    if (std::all_of(labels.begin(), labels.end(), allDigits)) {
        if (!ipv4(text)) return std::string("host");
        out.host = std::string(text);
        out.literal = true;
        return out;
    }
    if (!std::all_of(labels.begin(), labels.end(), labelOk)) return std::string("host");
    out.host = std::string(text);
    return out;
}

std::optional<int> portOf(std::string_view text, std::optional<int> fallback) {
    if (text.empty() && fallback) return fallback;
    if (!allDigits(text) || text.size() > 5 || (text.size() > 1 && text.front() == '0')) return std::nullopt;
    const int value = std::stoi(std::string(text));
    return value >= 1 && value <= 65535 ? std::optional<int>(value) : std::nullopt;
}

// The host and the port text; nullopt: no port; "!": a port that cannot be one.
std::pair<std::string_view, std::optional<std::string_view>> splitHostPort(std::string_view authority) {
    static constexpr std::string_view kBad = "!";
    if (!authority.empty() && authority.front() == '[') {
        const auto end = authority.find(']');
        if (end == std::string_view::npos) return {authority, std::nullopt};
        const auto rest = authority.substr(end + 1);
        if (rest.empty()) return {authority, std::nullopt};
        if (rest.front() == ':') return {authority.substr(0, end + 1), rest.substr(1)};
        return {authority, kBad};
    }
    if (std::count(authority.begin(), authority.end(), ':') > 1) return {authority, kBad};
    const auto colon = authority.find(':');
    if (colon == std::string_view::npos) return {authority, std::nullopt};
    return {authority.substr(0, colon), authority.substr(colon + 1)};
}

}  // namespace

bool isLocalIPv4(const std::array<std::uint8_t, 4>& ip, bool udp) {
    if (udp && ip == std::array<std::uint8_t, 4>{255, 255, 255, 255}) return true;
    return ip[0] == 10 || (ip[0] == 172 && (ip[1] & 0xF0) == 16) || (ip[0] == 192 && ip[1] == 168) || (ip[0] == 169 && ip[1] == 254) ||
           ip[0] == 127;
}

bool isLocalIPv6(const std::array<std::uint8_t, 16>& ip, bool udp) {
    static constexpr std::array<std::uint8_t, 12> kMapped = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF};
    if (std::equal(kMapped.begin(), kMapped.end(), ip.begin())) return isLocalIPv4({ip[12], ip[13], ip[14], ip[15]}, udp);
    static constexpr std::array<std::uint8_t, 16> kLoopback = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1};
    return ip == kLoopback || (ip[0] == 0xFE && (ip[1] & 0xC0) == 0x80) || (ip[0] & 0xFE) == 0xFC;
}

bool isLocal(std::string_view address, bool udp) {
    const auto bare = address.substr(0, address.find('%'));
    if (bare.find(':') == std::string_view::npos) {
        const auto v4 = ipv4(bare);
        return v4 && isLocalIPv4(*v4, udp);
    }
    const auto v6 = ipv6(bare);
    return v6 && isLocalIPv6(*v6, udp);
}

std::variant<TargetAddress, std::string> parseTarget(TargetKind kind, std::string_view address) {
    const auto stripped = text::strip(address);
    if (stripped.empty()) return std::string("empty");
    if (stripped.size() != address.size()) return std::string("spaces");
    if (std::any_of(address.begin(), address.end(), [](char c) { return static_cast<unsigned char>(c) <= 0x20 || c == 0x7F; })) {
        return std::string("spaces");
    }
    if (kind == TargetKind::Udp) {
        const auto [hostText, portText] = splitHostPort(address);
        if (!portText || *portText == "!") return std::string("port");
        auto host = hostOf(hostText);
        if (std::holds_alternative<std::string>(host)) return host;
        const auto port = portOf(*portText, std::nullopt);
        if (!port) return std::string("port");
        auto out = std::get<TargetAddress>(std::move(host));
        out.port = *port;
        return out;
    }
    std::string lower(address);
    std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    if (lower.starts_with("https://")) return std::string("https");
    if (!lower.starts_with("http://")) return std::string("scheme");
    const auto rest = address.substr(7);
    if (rest.find('#') != std::string_view::npos) return std::string("fragment");
    const auto cut = std::min(rest.find('/'), rest.find('?'));
    const auto authority = rest.substr(0, cut == std::string_view::npos ? rest.size() : cut);
    auto target = std::string(cut == std::string_view::npos ? std::string_view() : rest.substr(cut));
    if (authority.find('@') != std::string_view::npos) return std::string("userinfo");
    const auto [hostText, portText] = splitHostPort(authority);
    if (portText && *portText == "!") return std::string("port");
    auto host = hostOf(hostText);
    if (std::holds_alternative<std::string>(host)) return host;
    const auto port = portText && portText->empty() ? std::nullopt : portOf(portText.value_or(""), 80);
    if (!port) return std::string("port");
    if (target.empty() || target.front() == '?') target = "/" + target;
    auto out = std::get<TargetAddress>(std::move(host));
    out.port = *port;
    out.hostHeader = std::string(authority);
    out.target = std::move(target);
    return out;
}

bool validTarget(TargetKind kind, std::string_view address) { return std::holds_alternative<TargetAddress>(parseTarget(kind, address)); }

bool isBroadcast(const TargetAddress& address) {
    if (!address.literal || address.host.find(':') != std::string::npos) return false;
    return address.host.ends_with(".255");
}

Resolution resolveCheck(const std::vector<std::string>& addresses, bool udp) {
    if (addresses.empty()) return Resolution::NotFound;
    return std::all_of(addresses.begin(), addresses.end(), [&](const std::string& a) { return isLocal(a, udp); }) ? Resolution::Ok
                                                                                                                 : Resolution::NotLocal;
}

}  // namespace agentspeed::lan
