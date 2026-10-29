#include <unicode/uchar.h>
#include <unicode/uidna.h>

#include <algorithm>
#include <cctype>

#include "agentspeed/report_service.hpp"
#include "unicode.hpp"

namespace agentspeed::report {

namespace {

constexpr std::size_t kMaxUrl = 2048;
constexpr std::size_t kMaxDiscoveryBytes = 65'536;

bool asciiAlnum(char c) { return std::isalnum(static_cast<unsigned char>(c)) != 0; }
std::string lower(std::string_view text) {
    std::string out(text);
    std::transform(out.begin(), out.end(), out.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return out;
}

// No white space, control or format characters anywhere.
bool cleanText(std::string_view text) {
    for (const auto c : detail::points(text)) {
        if (detail::pythonSpace(c) || u_isUWhiteSpace(static_cast<UChar32>(c))) return false;
        switch (u_charType(static_cast<UChar32>(c))) {
        case U_CONTROL_CHAR:
        case U_FORMAT_CHAR:
        case U_LINE_SEPARATOR:
        case U_PARAGRAPH_SEPARATOR:
        case U_SPACE_SEPARATOR:
        case U_SURROGATE: return false;
        default: break;
        }
        if (c == 0xFFFD) return false;
    }
    return true;
}

bool hasScheme(std::string_view text) {
    const auto separator = text.find("://");
    if (separator == std::string_view::npos || separator == 0 || !std::isalpha(static_cast<unsigned char>(text[0]))) return false;
    return std::all_of(text.begin(), text.begin() + static_cast<std::ptrdiff_t>(separator),
                       [](char c) { return asciiAlnum(c) || c == '+' || c == '-' || c == '.'; });
}

bool validPercentEscapes(std::string_view text) {
    for (std::size_t i = 0; i < text.size(); ++i) {
        if (text[i] != '%') continue;
        if (i + 2 >= text.size() || !std::isxdigit(static_cast<unsigned char>(text[i + 1])) || !std::isxdigit(static_cast<unsigned char>(text[i + 2])))
            return false;
        i += 2;
    }
    return true;
}

bool splitAuthority(std::string_view authority, std::string& host, std::optional<int>& port) {
    host = std::string(authority);
    std::optional<std::string_view> portText;
    if (!authority.empty() && authority.front() == '[') {
        const auto close = authority.find(']');
        if (close == std::string_view::npos) return false;
        host = std::string(authority.substr(0, close + 1));
        const auto after = authority.substr(close + 1);
        if (!after.empty()) {
            if (after[0] != ':') return false;
            portText = after.substr(1);
        }
    } else if (const auto colon = authority.find(':'); colon != std::string_view::npos) {
        host = std::string(authority.substr(0, colon));
        portText = authority.substr(colon + 1);
    }
    if (portText) {
        if (portText->empty() || portText->size() > 5 || !std::all_of(portText->begin(), portText->end(), [](char c) { return c >= '0' && c <= '9'; }))
            return false;
        const int value = std::stoi(std::string(*portText));
        if (value < 1 || value > 65535) return false;
        port = value;
    }
    return !host.empty();
}

// Lower-case ASCII (IDNA A-labels for international names); IPv6 literals stay in brackets.
std::optional<std::string> hostOf(const std::string& raw) {
    if (raw.front() == '[' || std::all_of(raw.begin(), raw.end(), [](unsigned char c) { return c < 0x80; })) return lower(raw);
    UErrorCode status = U_ZERO_ERROR;
    UIDNA* idna = uidna_openUTS46(UIDNA_USE_STD3_RULES | UIDNA_CHECK_BIDI | UIDNA_CHECK_CONTEXTJ | UIDNA_NONTRANSITIONAL_TO_ASCII, &status);
    if (U_FAILURE(status)) return std::nullopt;
    char out[512];
    UIDNAInfo info = UIDNA_INFO_INITIALIZER;
    const auto length = uidna_nameToASCII_UTF8(idna, raw.data(), static_cast<int32_t>(raw.size()), out, sizeof out, &info, &status);
    uidna_close(idna);
    if (U_FAILURE(status) || info.errors != 0 || length <= 0) return std::nullopt;
    return lower(std::string_view(out, static_cast<std::size_t>(length)));
}

// A DNS name of LDH labels (after IDNA), a dotted IPv4 address, or an IPv6 address in brackets.
bool validHost(const std::string& host) {
    if (host.empty() || host.size() > 253) return false;
    if (host.front() == '[') {
        const auto inner = std::string_view(host).substr(1, host.size() - 2);
        return host.back() == ']' && inner.find(':') != std::string_view::npos &&
               std::all_of(inner.begin(), inner.end(), [](char c) { return std::isxdigit(static_cast<unsigned char>(c)) || c == ':' || c == '.'; });
    }
    std::size_t start = 0;
    while (true) {
        const auto dot = host.find('.', start);
        const auto label = std::string_view(host).substr(start, dot == std::string::npos ? std::string::npos : dot - start);
        if (label.empty() || label.size() > 63 || label.front() == '-' || label.back() == '-' ||
            !std::all_of(label.begin(), label.end(), [](char c) { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'; }))
            return false;
        if (dot == std::string::npos) return true;
        start = dot + 1;
    }
}

bool pathCharacter(char c) {
    return asciiAlnum(c) || std::string_view("-._~!$&'()*+,;=:@/%").find(c) != std::string_view::npos;
}

// The path as typed, with characters a path may not carry percent-encoded (UTF-8).
std::string encodePath(std::string_view path) {
    static constexpr char kHex[] = "0123456789ABCDEF";
    std::string out;
    for (const char c : path) {
        if (static_cast<unsigned char>(c) < 0x80 && pathCharacter(c)) {
            out += c;
        } else {
            out += '%';
            out += kHex[static_cast<unsigned char>(c) >> 4];
            out += kHex[static_cast<unsigned char>(c) & 15];
        }
    }
    return out;
}

// . and .. segments (also percent-encoded) are refused so the signed path is the path sent (REPORTING 5.3).
bool hasDotSegment(std::string_view path) {
    std::size_t start = 0;
    while (start <= path.size()) {
        const auto slash = path.find('/', start);
        auto segment = lower(path.substr(start, slash == std::string_view::npos ? std::string_view::npos : slash - start));
        for (std::size_t at; (at = segment.find("%2e")) != std::string::npos;) segment.replace(at, 3, ".");
        if (segment == "." || segment == "..") return true;
        if (slash == std::string_view::npos) break;
        start = slash + 1;
    }
    return false;
}

bool endsWithDomain(std::string_view host, std::string_view domain) {
    return host == domain || (host.size() > domain.size() && host.ends_with(domain) && host[host.size() - domain.size() - 1] == '.');
}

std::string hostOnly(std::string_view url) {
    const auto parsed = ServiceUrl::parseApi(url, true);
    if (!parsed) return {};
    auto host = parsed->host();
    if (!host.empty() && host.front() == '[') host = host.substr(1, host.size() - 2);
    return host;
}

}  // namespace

ServiceUrl::ServiceUrl(std::string scheme, std::string host, std::optional<int> port, std::string path)
    : scheme_(std::move(scheme)), host_(std::move(host)), path_(std::move(path)), port_(port) {
    text_ = scheme_ + "://" + authority() + path_;
}

std::optional<ServiceUrl> ServiceUrl::parse(std::string_view input, UrlProblem* problem) {
    if (problem) *problem = UrlProblem::Invalid;
    while (!input.empty() && std::isspace(static_cast<unsigned char>(input.front()))) input.remove_prefix(1);
    while (!input.empty() && std::isspace(static_cast<unsigned char>(input.back()))) input.remove_suffix(1);
    if (input.empty()) return std::nullopt;
    const auto text = hasScheme(input) ? std::string(input) : "https://" + std::string(input);
    if (!cleanText(text) || text.find('\\') != std::string::npos || !validPercentEscapes(text) || !hasScheme(text)) return std::nullopt;
    const auto separator = text.find("://");
    const auto scheme = lower(std::string_view(text).substr(0, separator));
    const auto rest = std::string_view(text).substr(separator + 3);
    const auto authorityEnd = rest.find_first_of("/?#");
    const auto authority = rest.substr(0, authorityEnd);
    const auto remainder = authorityEnd == std::string_view::npos ? std::string_view() : rest.substr(authorityEnd);
    std::string rawHost;
    std::optional<int> port;
    if (remainder.find_first_of("?#") != std::string_view::npos || authority.find('@') != std::string_view::npos ||
        !splitAuthority(authority, rawHost, port))
        return std::nullopt;
    const auto host = hostOf(rawHost);
    if (!host || !validHost(*host)) return std::nullopt;
    const bool loopback = *host == "127.0.0.1" || *host == "[::1]" || *host == "localhost";
    if (scheme != "https" && !(scheme == "http" && loopback)) {
        if (problem) *problem = UrlProblem::NotHttps;
        return std::nullopt;
    }
    if (port && *port == (scheme == "https" ? 443 : 80)) port.reset();
    auto path = encodePath(remainder);
    while (!path.empty() && path.back() == '/') path.pop_back();
    if ((!path.empty() && path.front() != '/') || hasDotSegment(path)) return std::nullopt;
    ServiceUrl candidate(scheme, *host, port, path);
    if (detail::points(candidate.text()).size() > kMaxUrl) return std::nullopt;
    return candidate;
}

std::optional<ServiceUrl> ServiceUrl::parseApi(std::string_view text, bool discoveryIsLoopbackHttp) {
    if (!hasScheme(text)) return std::nullopt;
    auto api = parse(text);
    if (!api || (api->loopbackHttp() && !discoveryIsLoopbackHttp)) return std::nullopt;
    return api;
}

std::optional<std::string> httpsLink(std::string_view text) {
    if (detail::points(text).size() > kMaxUrl || !cleanText(text) || !text.starts_with("https://")) return std::nullopt;
    const auto rest = text.substr(8);
    const auto end = rest.find_first_of("/?#");
    const auto authority = rest.substr(0, end);
    if (authority.empty() || authority.find('@') != std::string_view::npos || (end != std::string_view::npos && rest[end] != '/')) return std::nullopt;
    return std::string(text);
}

std::optional<Discovery> parseDiscoveryBody(std::string_view body, const ServiceUrl& service, DiscoveryProblem* problem) {
    const auto fail = [&](DiscoveryProblem::Kind kind, std::string field) -> std::optional<Discovery> {
        if (problem) *problem = {kind, std::move(field)};
        return std::nullopt;
    };
    if (body.size() > kMaxDiscoveryBytes) return fail(DiscoveryProblem::Kind::NotSupported, "$");
    json::Document doc;
    if (!doc.parse(body, 64) || !doc.root().isObject()) return fail(DiscoveryProblem::Kind::NotSupported, "$");
    std::string field;
    auto parsed = parseDiscovery(doc.root(), service.discoveryUrl(), &field);
    if (!parsed) return fail(field == "protocol" ? DiscoveryProblem::Kind::NotSupported : DiscoveryProblem::Kind::Invalid, field);
    return parsed;
}

std::string discoveryJson(const Discovery& d) {
    auto service = Json::object({{"name", Json::of(d.name)}});
    if (d.description) service.add("description", Json::of(*d.description));
    if (d.contact) service.add("contact", Json::of(*d.contact));
    if (d.homepage) service.add("homepage", Json::of(*d.homepage));
    service.add("terms", Json::of(*d.terms));
    service.add("privacy", Json::of(*d.privacy));
    if (d.leaderboard) service.add("leaderboard", Json::of(*d.leaderboard));
    if (d.retentionDays) service.add("retentionDays", Json::of(*d.retentionDays));
    std::vector<Json> versions, sections;
    for (const auto v : d.versions) versions.push_back(Json::of(v));
    for (const auto& s : d.sections) sections.push_back(Json::of(s));
    auto apiKey = Json::object({{"mode", Json::of(d.apiKeyMode)}});
    if (d.apiKeyUrl) apiKey.add("url", Json::of(*d.apiKeyUrl));
    auto extensions = Json::object({});
    for (const auto& [name, value] : d.extensions) extensions.add(name, Json::of(value));
    auto document = Json::object({{"protocol", Json::of("agent-speed-report")},
                                  {"versions", Json::array(std::move(versions))},
                                  {"service", std::move(service)},
                                  {"api", Json::of(d.api)},
                                  {"apiKey", std::move(apiKey)},
                                  {"username", Json::object({{"minLength", Json::of(d.username.minLength)},
                                                             {"maxLength", Json::of(d.username.maxLength)},
                                                             {"charset", Json::of(d.username.ascii ? "ascii" : "unicode")},
                                                             {"spaces", Json::of(d.username.spaces)}})},
                                  {"limits", Json::object({{"intervalSeconds", Json::of(d.limits.intervalSeconds)},
                                                           {"heartbeatSeconds", Json::of(d.limits.heartbeatSeconds)},
                                                           {"maxAgeSeconds", Json::of(d.limits.maxAgeSeconds)},
                                                           {"maxBodyBytes", Json::of(d.limits.maxBodyBytes)},
                                                           {"maxModels", Json::of(d.limits.maxModels)}})},
                                  {"sections", Json::array(std::move(sections))},
                                  {"extensions", std::move(extensions)}});
    if (d.websocket) {
        const auto& w = *d.websocket;
        document.add("websocket", Json::object({{"path", Json::of("/v1/reports/ws")},
                                                {"subprotocol", Json::of("agent-speed-report.v1")},
                                                {"minIntervalMs", Json::of(w.minIntervalMs)},
                                                {"maxInFlight", Json::of(w.maxInFlight)},
                                                {"ackTimeoutMs", Json::of(w.ackTimeoutMs)},
                                                {"maxMessageBytes", Json::of(w.maxMessageBytes)},
                                                {"pingIntervalSeconds", Json::of(w.pingIntervalSeconds)},
                                                {"pongTimeoutSeconds", Json::of(w.pongTimeoutSeconds)}}));
    }
    return document.compact();
}

Discovery assumedDiscovery(const ServiceUrl& service, const std::string& name) {
    const auto link = "https://" + service.host() + "/";
    const auto text = Json::object({{"protocol", Json::of("agent-speed-report")},
                                    {"versions", Json::array({Json::of(1)})},
                                    {"service", Json::object({{"name", Json::of(name)}, {"terms", Json::of(link)}, {"privacy", Json::of(link)}})},
                                    {"api", Json::of(service.text())},
                                    {"extensions", Json::object({{"perfScore", Json::of(1)}})}})
                          .compact();
    json::Document doc;
    doc.parse(text);
    return *parseDiscovery(doc.root(), service.discoveryUrl());
}

ConsentPin ConsentPin::of(const Discovery& d) {
    ConsentPin pin{d.name, d.terms.value_or(""), d.privacy.value_or(""), d.api, d.sections, d.retentionDays, d.apiKeyMode};
    std::sort(pin.sections.begin(), pin.sections.end());
    return pin;
}

bool ConsentPin::needsReview(const Discovery& current, bool hasApiKey) const {
    const auto now = of(current);
    if (now.name != name || now.terms != terms || now.privacy != privacy || now.api != api) return true;
    for (const auto& section : now.sections)
        if (std::find(sections.begin(), sections.end(), section) == sections.end()) return true;
    if (retentionDays && now.retentionDays.value_or(INT64_MAX) > *retentionDays) return true;
    return now.apiKeyMode == "required" && apiKeyMode != "required" && !hasApiKey;
}

UsernameRules usernameRules(const Discovery& discovery) { return discovery.username; }

bool invitationCovers(const Discovery& discovery, const ServiceUrl& service, std::string_view presetDomain, std::string_view username) {
    const auto apiHost = hostOnly(discovery.api);
    if (apiHost.empty()) return false;
    const bool hostCovered = endsWithDomain(apiHost, service.host()) ||
                             (!presetDomain.empty() && endsWithDomain(apiHost, presetDomain) && endsWithDomain(service.host(), presetDomain));
    return hostCovered && discovery.apiKeyMode != "required" && !validateUsername(username, discovery.username);
}

std::string registrationBody(std::string_view deviceId, std::string_view username, const PublicKey& key, const ClientInfo& client, bool transfer) {
    json::Document jwkDoc;
    const auto jwkText = jwk(key);
    jwkDoc.parse(jwkText);
    auto body = Json::object({{"schema", Json::of(1)},
                              {"deviceId", Json::of(deviceId)},
                              {"username", Json::of(username)},
                              {"publicKey", Json::object({{"kty", Json::of("EC")},
                                                          {"crv", Json::of("P-256")},
                                                          {"x", Json::of(*jwkDoc.root()["x"].string())},
                                                          {"y", Json::of(*jwkDoc.root()["y"].string())}})},
                              {"client", client.json()}});
    if (transfer) body.add("transfer", Json::of(true));
    return body.compact();
}

std::string renameBody(std::string_view username) { return Json::object({{"username", Json::of(username)}}).compact(); }

std::string rotationBody(const PublicKey& key) {
    json::Document jwkDoc;
    const auto jwkText = jwk(key);
    jwkDoc.parse(jwkText);
    return Json::object({{"publicKey", Json::object({{"kty", Json::of("EC")},
                                                     {"crv", Json::of("P-256")},
                                                     {"x", Json::of(*jwkDoc.root()["x"].string())},
                                                     {"y", Json::of(*jwkDoc.root()["y"].string())}})}})
        .compact();
}

}  // namespace agentspeed::report
