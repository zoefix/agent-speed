#include <set>

#include "agentspeed/quota/desktop_entry.hpp"
#include "agentspeed/quota/files.hpp"
#include "agentspeed/quota/http.hpp"
#include "agentspeed/rfc3339.hpp"
#include "agentspeed/text.hpp"

// QUOTAS 4.2 response info and 4.3 body: the usage entry of a cache file and its windows.
namespace agentspeed::quota::desktop {

namespace {

// ISO 8601 with an offset (up to 6 fractional digits); anything else is an unknown reset.
std::optional<std::int64_t> resetMs(const json::Value& value) {
    const auto text = value.string();
    return text ? parseTimestamp(*text) : std::nullopt;
}

// Python's casefold for the names Desktop sends: ASCII letters fold, others compare as they are.
std::string folded(const std::string& name) {
    std::string out = name;
    for (auto& c : out) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return out;
}

}  // namespace

std::vector<Window> usageWindows(const json::Value& body) {
    std::vector<Window> found;
    const std::pair<const char*, Kind> plain[] = {{"five_hour", Kind::FiveHour}, {"seven_day", Kind::SevenDay}};
    for (const auto& [field, kind] : plain) {
        const auto window = body[field];
        if (!window.isObject()) continue;
        if (const auto used = window["utilization"].finiteNumber()) found.push_back(makeWindow(kind, *used, resetMs(window["resets_at"])));
    }
    std::set<std::string> names;
    if (const auto limits = body["limits"]; limits.isArray()) {
        for (const auto& item : limits.elements()) {
            if (!item.isObject() || !item["kind"].equals("weekly_scoped")) continue;
            const auto used = item["percent"].finiteNumber();
            if (!used) continue;
            const auto scope = item["scope"];
            const auto model = scope.isObject() ? scope["model"] : json::Value();
            const auto name = model.isObject() ? model["display_name"].string() : std::nullopt;
            if (!name || !text::hasVisibleText(*name) || !names.insert(folded(*name)).second) continue;
            found.push_back(makeWindow(Kind::ModelWeek, *used, resetMs(item["resets_at"]), *name));
        }
    }
    const std::pair<const char*, const char*> models[] = {{"seven_day_opus", "Opus"}, {"seven_day_sonnet", "Sonnet"}};
    for (const auto& [field, name] : models) {
        const auto window = body[field];
        if (!window.isObject()) continue;
        const auto used = window["utilization"].finiteNumber();
        if (!used || !names.insert(folded(name)).second) continue;
        found.push_back(makeWindow(Kind::ModelWeek, *used, resetMs(window["resets_at"]), std::string(name)));
    }
    return found;
}

std::optional<UsageEntry> parseUsageEntry(std::string_view bytes, std::int64_t mtimeMs) {
    const auto entry = parseCacheEntry(bytes);
    if (!entry) return std::nullopt;
    auto url = urlOfKey(entry->key);
    if (!isUsageUrl(url)) return std::nullopt;
    const auto info = parseResponseInfo(entry->responseInfo);
    if (!info || info->status != 200) return std::nullopt;
    const auto coding = codingOf(info->header("content-encoding"));
    if (!coding) return std::nullopt;
    const auto decoded = decodeBody(entry->body, *coding, kBodyLimit);
    if (!decoded || !files::validUtf8(*decoded)) return std::nullopt;
    json::Document doc;
    if (!doc.parse(*decoded) || !doc.root().isObject()) return std::nullopt;
    std::int64_t observed = mtimeMs;
    if (const auto date = info->header("date")) observed = http::parseHttpDate(*date).value_or(mtimeMs);
    return UsageEntry{std::move(url), observed, usageWindows(doc.root())};
}

const FoundEntry* newestEntry(const std::vector<FoundEntry>& entries) {
    const FoundEntry* best = nullptr;
    for (const auto& found : entries) {
        if (!best || found.entry.observedMs > best->entry.observedMs ||
            (found.entry.observedMs == best->entry.observedMs && found.mtimeNs > best->mtimeNs)) {
            best = &found;
        }
    }
    return best;
}

}  // namespace agentspeed::quota::desktop
