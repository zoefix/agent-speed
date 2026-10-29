#include <algorithm>
#include <array>
#include <tuple>

#include "agentspeed/report.hpp"
#include "unicode.hpp"

namespace agentspeed::report {

namespace {

constexpr std::array kSections = {"sources", "models", "cost", "traffic"};
constexpr std::array kExtensions = {"modelHarnessRequests", "executionModes", "profileAvatar", "sharedIdentity", "perfScore",
                                    "taskScore", "reportWebSocket"};
constexpr std::string_view kWsPath = "/v1/reports/ws";
constexpr std::string_view kSubprotocol = "agent-speed-report.v1";

struct Invalid {
    std::string field;
};

enum class Kind { String, Object, Array, Integer, Boolean };

bool typeOk(const json::Value& value, Kind kind) {
    switch (kind) {
    case Kind::String: return value.isString();
    case Kind::Object: return value.isObject();
    case Kind::Array: return value.isArray();
    case Kind::Integer: return value.isNumber() && value.isIntegerLiteral();
    case Kind::Boolean: return value.isBool();
    }
    return false;
}

json::Value member(const json::Value& container, std::string_view key, Kind kind, const std::string& field, bool required = false) {
    const auto value = container.isObject() ? container[key] : json::Value();
    if (!value.valid()) {
        if (required) throw Invalid{field};
        return {};
    }
    if (!typeOk(value, kind)) throw Invalid{field};
    return value;
}

std::int64_t clamp(const json::Value& value, std::int64_t low, std::int64_t high) {
    return std::clamp(value.truncated().value_or(0), low, high);
}

bool noSpace(std::u32string_view text, std::u32string_view banned = U"") {
    return std::none_of(text.begin(), text.end(), [&](char32_t c) { return detail::pythonSpace(c) || banned.find(c) != std::u32string_view::npos; });
}

// ^https://[^\s/?#@]+(/[^\s]*)?$ with maxLength 2048
bool httpsUrl(std::string_view url) {
    const auto text = detail::points(url);
    if (text.size() > 2048 || !text.starts_with(U"https://")) return false;
    const auto rest = std::u32string_view(text).substr(8);
    const auto slash = rest.find(U'/');
    const auto host = rest.substr(0, slash);
    return !host.empty() && noSpace(host, U"?#@") && (slash == std::u32string_view::npos || noSpace(rest.substr(slash)));
}

bool loopbackAuthority(std::u32string_view authority) {
    for (const std::u32string_view host : {U"127.0.0.1", U"localhost", U"[::1]"}) {
        if (!authority.starts_with(host)) continue;
        const auto port = authority.substr(host.size());
        if (port.empty()) return true;
        return port.size() >= 2 && port.size() <= 6 && port[0] == U':' &&
               std::all_of(port.begin() + 1, port.end(), [](char32_t c) { return c >= U'0' && c <= U'9'; });
    }
    return false;
}

// ^(https://[^\s/?#@]+|http://(127\.0\.0\.1|localhost|\[::1\])(:[0-9]{1,5})?)(/[^\s?#]*)?$ with maxLength 2048
bool apiUrl(std::string_view url) {
    const auto text = detail::points(url);
    if (text.size() > 2048) return false;
    const bool secure = text.starts_with(U"https://");
    if (!secure && !text.starts_with(U"http://")) return false;
    const auto rest = std::u32string_view(text).substr(secure ? 8 : 7);
    const auto slash = rest.find(U'/');
    const auto authority = rest.substr(0, slash);
    const bool hostOk = secure ? !authority.empty() && noSpace(authority, U"/?#@") : loopbackAuthority(authority);
    return hostOk && (slash == std::u32string_view::npos || noSpace(rest.substr(slash), U"?#"));
}

// ^http://(127\.0\.0\.1|localhost|\[::1\])(:[0-9]{1,5})?(/|$)
bool loopbackHttp(std::string_view url) {
    const auto text = detail::points(url);
    if (!text.starts_with(U"http://")) return false;
    const auto rest = std::u32string_view(text).substr(7);
    return loopbackAuthority(rest.substr(0, rest.find(U'/')));
}

bool supported(const json::Value& extensions, std::string_view name) {
    const auto value = extensions.isObject() ? extensions[name] : json::Value();
    return value.isNumber() && value.raw() == "1";
}

std::optional<StreamSettings> streamSettings(const json::Value& document) {
    if (!supported(document["extensions"], "reportWebSocket")) return std::nullopt;
    const auto given = document["websocket"];
    if (!given.isObject() || !given["path"].equals(kWsPath) || !given["subprotocol"].equals(kSubprotocol)) return std::nullopt;
    StreamSettings settings;
    const std::array ranges = {std::tuple{"minIntervalMs", 10, 1000, &settings.minIntervalMs},
                               std::tuple{"maxInFlight", 1, 64, &settings.maxInFlight},
                               std::tuple{"ackTimeoutMs", 500, 10000, &settings.ackTimeoutMs},
                               std::tuple{"maxMessageBytes", 32768, 65536, &settings.maxMessageBytes},
                               std::tuple{"pingIntervalSeconds", 10, 60, &settings.pingIntervalSeconds},
                               std::tuple{"pongTimeoutSeconds", 5, 30, &settings.pongTimeoutSeconds}};
    for (const auto& [key, low, high, target] : ranges) {
        const auto value = given[key];
        if (!value.valid()) continue;
        if (!typeOk(value, Kind::Integer)) return std::nullopt;
        const auto number = value.truncated().value_or(-1);
        if (number < low || number > high) return std::nullopt;
        *target = static_cast<int>(number);
    }
    if (settings.pongTimeoutSeconds >= settings.pingIntervalSeconds) return std::nullopt;
    return settings;
}

Discovery parse(const json::Value& document, std::string_view fetchedFrom) {
    if (!document.isObject()) throw Invalid{"$"};
    if (!member(document, "protocol", Kind::String, "protocol", true).equals("agent-speed-report")) throw Invalid{"protocol"};
    const auto versions = member(document, "versions", Kind::Array, "versions", true);
    Discovery result;
    for (const auto& version : versions.elements()) {
        if (!typeOk(version, Kind::Integer)) throw Invalid{"versions"};
        result.versions.push_back(version.truncated().value_or(0));
    }
    if (result.versions.empty()) throw Invalid{"versions"};

    const auto service = member(document, "service", Kind::Object, "service", true);
    result.name = detail::plainText(*member(service, "name", Kind::String, "service.name", true).string(), 40);
    if (result.name.empty()) throw Invalid{"service.name"};
    for (const auto& [key, limit, target] : {std::tuple{"description", 200, &result.description}, std::tuple{"contact", 254, &result.contact}}) {
        const auto text = member(service, key, Kind::String, std::string("service.") + key);
        if (!text.valid()) continue;
        auto plain = detail::plainText(*text.string(), static_cast<std::size_t>(limit));
        if (!plain.empty()) *target = std::move(plain);
    }
    for (const auto& [key, target] : {std::pair{"terms", &result.terms}, std::pair{"privacy", &result.privacy}}) {
        const auto link = *member(service, key, Kind::String, std::string("service.") + key, true).string();
        if (!httpsUrl(link)) throw Invalid{std::string("service.") + key};
        *target = link;
    }
    for (const auto& [key, target] : {std::pair{"homepage", &result.homepage}, std::pair{"leaderboard", &result.leaderboard}}) {
        const auto link = member(service, key, Kind::String, std::string("service.") + key);
        if (link.valid() && httpsUrl(*link.string())) *target = *link.string();
    }
    if (const auto retention = member(service, "retentionDays", Kind::Integer, "service.retentionDays"); retention.valid())
        result.retentionDays = clamp(retention, 0, 3650);

    const auto api = *member(document, "api", Kind::String, "api", true).string();
    if (!apiUrl(api) || (api.starts_with("http://") && !loopbackHttp(fetchedFrom))) throw Invalid{"api"};
    const auto separator = api.find("://");
    const auto rest = std::string_view(api).substr(separator + 3);
    const auto slash = rest.find('/');
    std::string authority(rest.substr(0, slash));
    std::transform(authority.begin(), authority.end(), authority.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    result.api = api.substr(0, separator + 3) + authority + std::string(slash == std::string_view::npos ? std::string_view() : rest.substr(slash));
    if (result.api.ends_with('/')) result.api.pop_back();

    const auto apiKey = member(document, "apiKey", Kind::Object, "apiKey");
    if (const auto mode = member(apiKey, "mode", Kind::String, "apiKey.mode"); mode.valid()) {
        const auto text = *mode.string();
        if (text != "none" && text != "optional" && text != "required") throw Invalid{"apiKey.mode"};
        result.apiKeyMode = text;
    }
    if (const auto url = member(apiKey, "url", Kind::String, "apiKey.url"); url.valid() && httpsUrl(*url.string())) result.apiKeyUrl = *url.string();

    const auto username = member(document, "username", Kind::Object, "username");
    if (const auto value = member(username, "minLength", Kind::Integer, "username.minLength"); value.valid())
        result.username.minLength = static_cast<int>(clamp(value, 1, 12));
    if (const auto value = member(username, "maxLength", Kind::Integer, "username.maxLength"); value.valid())
        result.username.maxLength = static_cast<int>(clamp(value, 12, 32));
    if (const auto charset = member(username, "charset", Kind::String, "username.charset"); charset.valid()) {
        if (!charset.equals("unicode") && !charset.equals("ascii")) throw Invalid{"username.charset"};
        result.username.ascii = charset.equals("ascii");
    }
    if (const auto spaces = member(username, "spaces", Kind::Boolean, "username.spaces"); spaces.valid()) result.username.spaces = spaces.isTrue();

    const auto limits = member(document, "limits", Kind::Object, "limits");
    auto& l = result.limits;
    for (const auto& [key, low, high, target] : {std::tuple{"intervalSeconds", 5, 300, &l.intervalSeconds},
                                                 std::tuple{"heartbeatSeconds", 30, 900, &l.heartbeatSeconds},
                                                 std::tuple{"maxAgeSeconds", 10, 120, &l.maxAgeSeconds},
                                                 std::tuple{"maxBodyBytes", 4096, 65536, &l.maxBodyBytes},
                                                 std::tuple{"maxModels", 0, 16, &l.maxModels}}) {
        if (const auto value = member(limits, key, Kind::Integer, std::string("limits.") + key); value.valid())
            *target = static_cast<int>(clamp(value, low, high));
    }
    l.heartbeatSeconds = std::max(l.heartbeatSeconds, l.intervalSeconds);

    const auto sections = member(document, "sections", Kind::Array, "sections");
    std::vector<std::string> named;
    for (const auto& name : sections.elements()) {
        if (!name.isString()) throw Invalid{"sections"};
        named.push_back(*name.string());
    }
    for (const auto* name : kSections)
        if (!sections.valid() || std::find(named.begin(), named.end(), name) != named.end()) result.sections.emplace_back(name);
    for (const auto* name : kExtensions)
        if (supported(document["extensions"], name)) result.extensions[name] = 1;
    result.websocket = streamSettings(document);
    return result;
}

}  // namespace

std::optional<Discovery> parseDiscovery(const json::Value& document, std::string_view fetchedFrom, std::string* field) {
    try {
        return parse(document, fetchedFrom);
    } catch (const Invalid& error) {
        if (field) *field = error.field;
        return std::nullopt;
    }
}

std::string websocketUrl(std::string_view api) {
    const auto separator = api.find("://");
    const auto scheme = api.substr(0, separator);
    return std::string(scheme == "https" ? "wss" : "ws") + std::string(api.substr(separator)) + std::string(kWsPath);
}

}  // namespace agentspeed::report
