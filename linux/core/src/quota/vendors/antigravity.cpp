#include <algorithm>
#include <array>
#include <cctype>

#include "agentspeed/quota/vendors/parsers.hpp"
#include "agentspeed/quota/vendors/values.hpp"

// QUOTAS 9.4: the language server's quota summary, the IDE server's user status and agy's usage report.
namespace agentspeed::quota::vendors {

namespace {

std::string lower(std::string text) {
    for (char& c : text) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return text;
}

bool containsAny(const std::string& text, std::initializer_list<std::string_view> words) {
    return std::any_of(words.begin(), words.end(), [&](std::string_view w) { return text.find(w) != std::string::npos; });
}

// agy's report names members in snake case; the camel-case member wins where both appear.
json::Value member(const json::Value& object, std::string_view camel, std::string_view snake, bool snakeCase) {
    const auto value = object[camel];
    return value.valid() || !snakeCase ? value : object[snake];
}

std::optional<Kind> bucketKind(const json::Value& bucket, bool snake) {
    std::string joined = text(bucket["window"]) + " " + text(member(bucket, "bucketId", "id", snake)) + " " +
                         text(member(bucket, "displayName", "name", snake));
    joined = lower(std::move(joined));
    std::replace(joined.begin(), joined.end(), '_', '-');
    if (containsAny(joined, {"weekly", "week", "7d"})) return Kind::SevenDay;
    if (containsAny(joined, {"5h", "5-hour", "five-hour", "five hour", "session"})) return Kind::FiveHour;
    return std::nullopt;
}

std::optional<double> remainingFraction(const json::Value& bucket, bool snake) {
    const auto direct = member(bucket, "remainingFraction", "remaining_fraction", snake);
    if (auto n = strictNumber(direct)) return n;
    if (direct.isObject() && direct["case"].equals("remainingFraction")) {
        if (auto n = strictNumber(direct["value"])) return n;
    }
    const auto nested = bucket["remaining"];
    if (nested.isObject()) return strictNumber(nested["remainingFraction"]);
    return std::nullopt;
}

// The summary's rules; `snake`: agy's report, whose own groups (not those under `response`) are in snake case.
std::vector<Window> summaryWindows(const json::Value& body, bool snake) {
    const bool nested = body["response"].isObject();
    const auto root = nested ? body["response"] : body;
    snake = snake && !nested;
    std::vector<Window> out;
    for (const auto& group : root["groups"].elements()) {
        if (!group.isObject()) continue;
        const auto name = antigravityGroupName(member(group, "displayName", "name", snake));
        for (const auto& bucket : group["buckets"].elements()) {
            if (!bucket.isObject() || bucket["disabled"].isTrue()) continue;
            const auto kind = bucketKind(bucket, snake);
            const auto remaining = remainingFraction(bucket, snake);
            if (!kind || !remaining) continue;
            out.push_back(makeWindow(*kind, (1 - *remaining) * 100, timeMs(member(bucket, "resetTime", "reset_time", snake)), name));
        }
    }
    return out;
}

}  // namespace

std::optional<std::string> antigravityGroupName(const json::Value& displayName) {
    const auto display = displayName.string().value_or("");
    const auto folded = lower(display);
    if (folded.find("gemini") != std::string::npos && folded.find("argon") != std::string::npos) return "Gemini Argon";
    if (folded.find("gemini") != std::string::npos) return "Gemini";
    if (containsAny(folded, {"claude", "gpt", "third"})) return "Claude/GPT";
    if (display.empty()) return std::nullopt;
    return display;
}

std::vector<Window> antigravitySummaryWindows(const json::Value& body) { return summaryWindows(body, false); }

std::optional<std::vector<Window>> antigravityCliWindows(std::string_view output) {
    json::Document doc;
    if (!doc.parse(output) || !doc.root().isObject()) return std::nullopt;
    const auto root = doc.root();
    const auto command = root["command"];
    if (!root["status"].equals("SUCCESS") || !command.isObject() || !command["name"].equals("usage") || !command["data"].isObject()) {
        return std::nullopt;
    }
    auto windows = summaryWindows(command["data"], true);
    if (windows.empty()) return std::nullopt;
    return windows;
}

std::vector<Window> antigravityStatusWindows(const json::Value& body) {
    const auto configs = body["userStatus"]["cascadeModelConfigData"]["clientModelConfigs"];
    std::vector<Window> out;
    for (const auto& config : configs.elements()) {
        const auto info = config["quotaInfo"];
        if (!info.isObject()) continue;
        const auto remaining = strictNumber(info["remainingFraction"]);
        if (!remaining) continue;
        out.push_back(makeWindow(Kind::FiveHour, (1 - *remaining) * 100, timeMs(info["resetTime"]),
                                 config["label"].nonEmptyString()));
    }
    return out;
}

}  // namespace agentspeed::quota::vendors
