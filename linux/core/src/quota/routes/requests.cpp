#include "agentspeed/quota/routes/requests.hpp"

#include <cstdio>

namespace agentspeed::quota::requests {

namespace {

constexpr std::string_view kJson = "application/json";

std::string https(std::string_view host, std::string_view path) {
    return "https://" + std::string(host) + std::string(path);
}

http::Header header(std::string_view name, std::string_view value) { return {std::string(name), std::string(value)}; }

http::Header bearer(std::string_view token) { return header("Authorization", "Bearer " + std::string(token)); }

http::Request make(http::Method method, std::string url, std::vector<http::Header> headers, std::string body = {}) {
    http::Request request;
    request.method = method;
    request.url = std::move(url);
    request.headers = std::move(headers);
    request.body = std::move(body);
    return request;
}

}  // namespace

std::string_view zaiRegionId(ZaiRegion region) { return region == ZaiRegion::Global ? "global" : "bigmodel-cn"; }

std::optional<ZaiRegion> zaiRegionFromId(std::string_view id) {
    if (id == "global") return ZaiRegion::Global;
    if (id == "bigmodel-cn") return ZaiRegion::BigModelCn;
    return std::nullopt;
}

std::string_view zaiHost(ZaiRegion region) { return region == ZaiRegion::Global ? hosts::kZai : hosts::kBigModel; }

std::string jsonQuoted(std::string_view text) {
    std::string out = "\"";
    for (const char c : text) {
        const auto byte = static_cast<unsigned char>(c);
        if (c == '"' || c == '\\') {
            out += '\\';
            out += c;
        } else if (byte < 0x20) {
            char escaped[8];
            std::snprintf(escaped, sizeof escaped, "\\u%04x", byte);
            out += escaped;
        } else {
            out += c;
        }
    }
    return out + "\"";
}

http::Request grokBilling(std::string_view token) {
    return make(http::Method::Get, https(hosts::kGrok, "/v1/billing?format=credits"),
                {header("Accept", kJson), bearer(token), header("x-xai-token-auth", "xai-grok-cli")});
}

http::Request kimiUsages(std::string_view token) {
    return make(http::Method::Get, https(hosts::kKimi, "/coding/v1/usages"), {header("Accept", kJson), bearer(token)});
}

http::Request geminiLoad(std::string_view token) {
    return make(http::Method::Post, https(hosts::kGemini, "/v1internal:loadCodeAssist"),
                {bearer(token), header("Content-Type", kJson)},
                R"({"metadata":{"ideType":"GEMINI_CLI","pluginType":"GEMINI"}})");
}

http::Request geminiQuota(std::string_view token, const std::optional<std::string>& project) {
    return make(http::Method::Post, https(hosts::kGemini, "/v1internal:retrieveUserQuota"),
                {bearer(token), header("Content-Type", kJson)},
                project ? "{\"project\":" + jsonQuoted(*project) + "}" : std::string("{}"));
}

http::Request droidLimits(std::string_view key) {
    return make(http::Method::Get, https(hosts::kFactory, "/api/billing/limits"), {header("Accept", kJson), bearer(key)});
}

http::Request zaiLimits(std::string_view key, ZaiRegion region) {
    return make(http::Method::Get, https(zaiHost(region), "/api/monitor/usage/quota/limit"), {bearer(key)});
}

std::optional<http::Request> qoderCredits(std::string_view cookie, std::string_view site) {
    if (site != hosts::kQoder && site != hosts::kQoderCn) return std::nullopt;
    const std::string origin = "https://" + std::string(site);
    return make(http::Method::Get, https(site, "/api/v2/me/usages/big_model_credits"),
                {header("Cookie", cookie), header("Origin", origin), header("Referer", origin + "/"), header("Accept", kJson)});
}

http::Request workBuddySummary(std::string_view cookie, std::string_view browserUserAgent) {
    return make(http::Method::Post, https(hosts::kWorkBuddy, "/billing/meter/get-user-resource-summary"),
                {header("Content-Type", kJson), header("Cookie", cookie), header("User-Agent", browserUserAgent)}, "{}");
}

std::optional<http::Request> antigravity(AntigravityCall call, std::string_view scheme, int port,
                                         const std::optional<std::string>& token) {
    if ((scheme != "http" && scheme != "https") || port < 1 || port > 65535) return std::nullopt;
    const std::string_view method = call == AntigravityCall::Summary ? "RetrieveUserQuotaSummary" : "GetUserStatus";
    std::vector<http::Header> headers = {header("Connect-Protocol-Version", "1"), header("Content-Type", kJson)};
    if (token && !token->empty()) headers.push_back(header("X-Codeium-Csrf-Token", *token));
    auto request = make(http::Method::Post,
                        std::string(scheme) + "://" + std::string(hosts::kLoopback) + ":" + std::to_string(port) +
                            "/exa.language_server_pb.LanguageServerService/" + std::string(method),
                        std::move(headers),
                        call == AntigravityCall::Summary
                            ? std::string(R"({"forceRefresh":true})")
                            : std::string(R"({"metadata":{"ideName":"antigravity","extensionName":"antigravity","ideVersion":"unknown","locale":"en"}})"));
    request.loopback = true;
    return request;
}

}  // namespace agentspeed::quota::requests
