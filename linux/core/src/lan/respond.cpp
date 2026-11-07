#include <set>

#include "agentspeed/json.hpp"
#include "agentspeed/lan_protocol.hpp"
#include "agentspeed/lan_websocket.hpp"
#include "agentspeed/report_json.hpp"
#include "agentspeed/text.hpp"

namespace agentspeed::lan {

namespace {

// A comma-separated header value as lowercase tokens.
std::set<std::string> tokens(const std::string* value) {
    std::set<std::string> out;
    if (!value) return out;
    std::string_view rest(*value);
    while (true) {
        const auto comma = rest.find(',');
        auto token = text::strip(rest.substr(0, comma));
        for (auto& c : token) c = static_cast<char>(c >= 'A' && c <= 'Z' ? c + 32 : c);
        if (!token.empty()) out.insert(std::move(token));
        if (comma == std::string_view::npos) break;
        rest.remove_prefix(comma + 1);
    }
    return out;
}

Response reply(int status, std::optional<std::string> error = std::nullopt, std::vector<std::pair<std::string, std::string>> headers = {}) {
    Response r;
    r.status = status;
    r.error = std::move(error);
    r.headers = std::move(headers);
    return r;
}

}  // namespace

std::string_view reason(int status) {
    switch (status) {
    case 101: return "Switching Protocols";
    case 200: return "OK";
    case 400: return "Bad Request";
    case 401: return "Unauthorized";
    case 404: return "Not Found";
    case 405: return "Method Not Allowed";
    case 426: return "Upgrade Required";
    case 429: return "Too Many Requests";
    case 503: return "Service Unavailable";
    default: return "Error";
    }
}

std::string errorBody(std::string_view code) {
    return report::Json::object({{"schema", report::Json::of(1)}, {"type", report::Json::of("error")}, {"error", report::Json::of(code)}})
        .compact();
}

Response respond(const Head& head, std::string_view peer, std::int64_t nowMs, std::string_view key, bool hasFrame, Lockout& lockout) {
    if (head.malformed) return reply(400, "badRequest");
    if (const auto left = lockout.locked(peer, nowMs)) return reply(429, "tooManyAttempts", {{"Retry-After", std::to_string(*left)}});
    if (head.method != "GET") return reply(405, "methodNotAllowed", {{"Allow", "GET"}});
    const auto path = head.path();
    if (path != "/v1/live" && path != "/v1/stream") return reply(404, "notFound");
    if (!keyMatches(requestKey(head), key)) {
        lockout.fail(peer, nowMs);
        return reply(401, "unauthorized", {{"WWW-Authenticate", R"(Bearer realm="agent-speed")"}});
    }
    lockout.succeed(peer);
    auto out = [&] {
        if (path == "/v1/live") return hasFrame ? reply(200) : reply(503, "starting");
        if (!tokens(head.header("upgrade")).contains("websocket") || !tokens(head.header("connection")).contains("upgrade")) {
            return reply(426, "upgradeRequired", {{"Upgrade", "websocket"}});
        }
        const auto* version = head.header("sec-websocket-version");
        const auto* wsKey = head.header("sec-websocket-key");
        if (!version || *version != "13" || !wsKey || !ws::validKey(*wsKey)) return reply(400, "badRequest", {{"Sec-WebSocket-Version", "13"}});
        return reply(101, std::nullopt, {{"Upgrade", "websocket"}, {"Connection", "Upgrade"}, {"Sec-WebSocket-Accept", ws::accept(*wsKey)}});
    }();
    out.authorized = true;
    return out;
}

std::string responseBytes(const Response& response, std::string_view frame) {
    std::string out = "HTTP/1.1 " + std::to_string(response.status) + " " + std::string(reason(response.status)) + "\r\n";
    if (response.status == 101) {
        for (const auto& [name, value] : response.headers) out += name + ": " + value + "\r\n";
        return out + "\r\n";
    }
    const auto body = response.error ? errorBody(*response.error) : std::string(frame);
    out += "Content-Type: application/json; charset=utf-8\r\nContent-Length: " + std::to_string(body.size()) +
           "\r\nCache-Control: no-store\r\nConnection: close\r\n";
    for (const auto& [name, value] : response.headers) out += name + ": " + value + "\r\n";
    return out + "\r\n" + body;
}

std::optional<std::string> hello(std::string_view line, std::string_view peer, std::int64_t nowMs, std::string_view key, Lockout& lockout) {
    if (lockout.locked(peer, nowMs)) return "tooManyAttempts";
    json::Document doc;
    if (!doc.parse(line)) return "badRequest";
    const auto root = doc.root();
    const auto given = root["key"].string();
    if (!root.isObject() || !root["type"].equals("hello") || !root["type"].isString() || !given) return "badRequest";
    if (!keyMatches(given, key)) {
        lockout.fail(peer, nowMs);
        return "unauthorized";
    }
    lockout.succeed(peer);
    return std::nullopt;
}

}  // namespace agentspeed::lan
