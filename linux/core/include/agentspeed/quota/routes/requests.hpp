#pragma once

#include <optional>
#include <string>
#include <string_view>

#include "agentspeed/quota/http.hpp"

// QUOTAS 9.2-9.9: each vendor request with exactly the headers its section lists. The transport adds
// `User-Agent: AgentSpeed/<version> (Linux)`; WorkBuddy carries the browser's own.
namespace agentspeed::quota::requests {

namespace hosts {
constexpr std::string_view kGrok = "cli-chat-proxy.grok.com";
constexpr std::string_view kKimi = "api.kimi.com";
constexpr std::string_view kGemini = "cloudcode-pa.googleapis.com";
constexpr std::string_view kFactory = "api.factory.ai";
constexpr std::string_view kZai = "api.z.ai";
constexpr std::string_view kBigModel = "open.bigmodel.cn";
constexpr std::string_view kQoder = "qoder.com";
constexpr std::string_view kQoderCn = "qoder.com.cn";
constexpr std::string_view kWorkBuddy = "www.workbuddy.cn";
constexpr std::string_view kLoopback = "127.0.0.1";
}  // namespace hosts

// QUOTAS 9.8 `quotaZcodeRegion`.
enum class ZaiRegion : unsigned char { Global, BigModelCn };
std::string_view zaiRegionId(ZaiRegion region);  // `global`, `bigmodel-cn`
std::optional<ZaiRegion> zaiRegionFromId(std::string_view id);
std::string_view zaiHost(ZaiRegion region);

http::Request grokBilling(std::string_view token);
http::Request kimiUsages(std::string_view token);
http::Request geminiLoad(std::string_view token);
// `{"project": "<id>"}`, or `{}` without one.
http::Request geminiQuota(std::string_view token, const std::optional<std::string>& project);
http::Request droidLimits(std::string_view key);
http::Request zaiLimits(std::string_view key, ZaiRegion region);
// `site` is `qoder.com` or `qoder.com.cn`, where the cookies came from; nullopt for any other.
std::optional<http::Request> qoderCredits(std::string_view cookie, std::string_view site);
http::Request workBuddySummary(std::string_view cookie, std::string_view browserUserAgent);

enum class AntigravityCall : unsigned char { Summary, Status };
// Loopback only: `scheme` http or https, a port 1-65535; the token header is left out without a token.
std::optional<http::Request> antigravity(AntigravityCall call, std::string_view scheme, int port,
                                         const std::optional<std::string>& token);

// A JSON string literal with the quotes.
std::string jsonQuoted(std::string_view text);

}  // namespace agentspeed::quota::requests
