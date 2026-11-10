#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/window.hpp"
#include "agentspeed/tz.hpp"

// QUOTAS 9.2-9.9: each vendor's answer (or local record) to windows. Pure functions; `body` is the parsed JSON root.
namespace agentspeed::quota::vendors {

// 9.2 Grok Build: the newest `billing: fetched credits config` line of the log, with its `ts`.
struct GrokLogConfig {
    std::int64_t observedMs = 0;
    std::vector<Window> windows;
};
std::optional<GrokLogConfig> grokFromLog(const std::vector<std::string_view>& lines);
// The billing config (`ctx.config` of the log, `config` of the live answer) to its one window, or none.
std::vector<Window> grokWindows(const json::Value& config);
// The live answer: nullopt when it has no `config` object.
std::optional<std::vector<Window>> grokFromResponse(const json::Value& body);

// 9.3 Devin: the rows of `ItemTable` the query found (key, JSON value); the greatest `endTimestamp` wins.
struct DevinRow {
    std::string key;
    std::string value;
};
std::vector<Window> devinWindows(const std::vector<DevinRow>& rows);
// A launch folder `YYYYMMDDTHHMMSS` read as wall time in `zone`.
std::optional<std::int64_t> devinLaunchMs(std::string_view name, const TimeZone& zone);

// 9.4 Antigravity: `RetrieveUserQuotaSummary` and the IDE server's `GetUserStatus`.
std::optional<std::string> antigravityGroupName(const json::Value& displayName);
std::vector<Window> antigravitySummaryWindows(const json::Value& body);
std::vector<Window> antigravityStatusWindows(const json::Value& body);

// 9.5 Kimi Code: the current `usages` shape, else the legacy `limits` / `usage` one.
std::vector<Window> kimiWindows(const json::Value& body);

// 9.6 Gemini CLI: `loadCodeAssist`'s project, `retrieveUserQuota`'s buckets by model family.
std::optional<std::string> geminiProject(const json::Value& body);
std::string geminiFamily(std::string_view modelId);
std::vector<Window> geminiWindows(const json::Value& body);

// 9.7 Factory Droid: the `FACTORY_API_KEY=` line of `~/.factory/.env`, and the limits (resets need `nowMs`).
std::optional<std::string> droidKeyFromEnvFile(std::string_view text);
std::vector<Window> droidWindows(const json::Value& body, std::int64_t nowMs);

// 9.8 ZCode (z.ai / BigModel): nullopt unless `success == true` and `code == 200`.
std::optional<std::vector<Window>> zaiWindows(const json::Value& body, std::int64_t nowMs);

// 9.9 Qoder's big-model credits (one `credits` window) and WorkBuddy's packages (nullopt unless `code == 0`).
std::vector<Window> qoderWindows(const json::Value& body);
std::optional<std::vector<Window>> workBuddyWindows(const json::Value& body);

// 9.9 The user agent of the browser the cookies came from (WorkBuddy).
std::string chromeUserAgent(std::string_view platformToken, int major);
std::string firefoxUserAgent(std::string_view platformToken, std::string_view version);

}  // namespace agentspeed::quota::vendors
