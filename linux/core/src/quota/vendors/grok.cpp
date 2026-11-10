#include <cctype>

#include "agentspeed/quota/vendors/parsers.hpp"
#include "agentspeed/quota/vendors/values.hpp"

// QUOTAS 9.2: Grok Build's billing config, from its log line or the live answer.
namespace agentspeed::quota::vendors {

namespace {

constexpr std::string_view kLogMessage = "billing: fetched credits config";
constexpr std::size_t kMaxLineBytes = 1 << 20;

std::int64_t floorDiv(std::int64_t a, std::int64_t b) {
    const std::int64_t q = a / b;
    return (a % b != 0 && ((a < 0) != (b < 0))) ? q - 1 : q;
}

std::string upper(std::string text) {
    for (char& c : text) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
    return text;
}

}  // namespace

std::vector<Window> grokWindows(const json::Value& config) {
    if (!config.isObject()) return {};
    const auto period = config["currentPeriod"].isObject() ? config["currentPeriod"] : json::Value();
    const Kind kind = upper(text(period["type"])).find("MONTH") != std::string::npos ? Kind::Month : Kind::SevenDay;
    auto used = number(config["creditUsagePercent"]);
    if (!used) {
        if (!config["isUnifiedBillingUser"].isTrue() || !truthy(period["start"]) || !truthy(period["end"])) return {};
        used = 0.0;
    }
    const auto end = timeMs(period["end"]);
    auto resets = end;
    if (!resets || *resets == 0) resets = timeMs(config["billingPeriodEnd"]);
    std::optional<std::int64_t> minutes;
    const auto start = timeMs(period["start"]);
    if (kind == Kind::Month && start && end) minutes = floorDiv(*end - *start, 60'000);
    return {makeWindow(kind, *used, resets, std::nullopt, minutes)};
}

std::optional<GrokLogConfig> grokFromLog(const std::vector<std::string_view>& lines) {
    std::optional<GrokLogConfig> best;
    for (const auto line : lines) {
        if (line.size() >= kMaxLineBytes) continue;
        json::Document doc;
        if (!doc.parse(line)) continue;
        const auto root = doc.root();
        if (!root.isObject() || !root["msg"].equals(kLogMessage)) continue;
        const auto ctx = root["ctx"];
        const auto ts = timeMs(root["ts"]);
        if (!ctx.isObject() || !ctx["config"].isObject() || !ts) continue;
        if (!best || *ts > best->observedMs) best = GrokLogConfig{*ts, grokWindows(ctx["config"])};
    }
    return best;
}

std::optional<std::vector<Window>> grokFromResponse(const json::Value& body) {
    if (!body.isObject() || !body["config"].isObject()) return std::nullopt;
    return grokWindows(body["config"]);
}

}  // namespace agentspeed::quota::vendors
