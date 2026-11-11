#include "quota_lines.hpp"

#include <algorithm>
#include <cmath>

#include "json_out.hpp"
#include "protocol.hpp"

namespace agentspeed::relay {

namespace {

constexpr std::size_t kSettingsPathBytes = 4096;

// The code point at `i` of valid UTF-8, and its length.
char32_t codePointAt(std::string_view text, std::size_t i, std::size_t& length) {
    const auto lead = static_cast<unsigned char>(text[i]);
    if (lead < 0x80) {
        length = 1;
        return lead;
    }
    length = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : 2;
    char32_t cp = lead & (0x3F >> (length - 1));
    for (std::size_t k = 1; k < length; ++k) cp = (cp << 6) | (static_cast<unsigned char>(text[i + k]) & 0x3F);
    return cp;
}

bool shown(char32_t cp) {
    const bool control = cp < 0x20 || (cp >= 0x7F && cp <= 0x9F);
    const bool separator = cp == 0x2028 || cp == 0x2029;
    const bool bidi = cp == 0x061C || cp == 0x200E || cp == 0x200F || (cp >= 0x202A && cp <= 0x202E) || (cp >= 0x2066 && cp <= 0x2069);
    return !control && !separator && !bidi;
}

void writeWindow(JsonOut& j, const quota::Window& w) {
    j.beginObject();
    j.key("kind").string(quota::kindId(w.kind));
    j.key("name").optionalString(w.name);
    j.key("minutes").optionalInteger(w.minutes);
    j.key("usedPercent").number(w.usedPercent);
    j.key("resetsAtMs").optionalInteger(w.resetsAtMs);
    j.endObject();
}

}  // namespace

std::optional<std::string> displayText(std::string_view text, std::size_t maxCodePoints) {
    const auto clean = sanitizeUtf8(text);
    std::string out;
    std::size_t count = 0;
    for (std::size_t i = 0; i < clean.size() && count < maxCodePoints;) {
        std::size_t length = 1;
        const auto cp = codePointAt(clean, i, length);
        if (shown(cp)) {
            out.append(clean, i, length);
            ++count;
        }
        i += length;
    }
    if (out.empty()) return std::nullopt;
    return out;
}

std::vector<quota::Window> sendableWindows(const std::vector<quota::Window>& windows) {
    std::vector<quota::Window> out;
    for (auto w : windows) {
        if (!std::isfinite(w.usedPercent)) continue;
        w.usedPercent = std::clamp(w.usedPercent, 0.0, remote::quotas::kMaxUsedPercent);
        if (w.minutes && (*w.minutes < 1 || *w.minutes > remote::quotas::kMaxMinutes)) w.minutes.reset();
        if (w.kind == quota::Kind::Other && !w.minutes) continue;
        if (w.resetsAtMs && !validTime(*w.resetsAtMs)) w.resetsAtMs.reset();
        if (w.name) w.name = displayText(*w.name, remote::quotas::kNameCharacters);
        out.push_back(std::move(w));
    }
    if (out.size() > remote::quotas::kWindows) {
        out = quota::ordered(out);
        out.resize(remote::quotas::kWindows);
    }
    return out;
}

std::string quotaLine(const remote::HostQuota& report) {
    const auto& snapshot = report.snapshot;
    if (!validTime(snapshot.observedMs) || !remote::quotas::hostReads(snapshot.source)) return {};
    JsonOut j;
    j.beginObject().key("t").string("quota").key("q").beginObject();
    j.key("source").string(sourceId(snapshot.source));
    j.key("observedMs").integer(snapshot.observedMs);
    j.key("windows").beginArray();
    for (const auto& w : sendableWindows(snapshot.windows)) writeWindow(j, w);
    j.endArray();
    j.key("running").boolean(report.running);
    j.key("signedIn").boolean(report.signedIn);
    j.key("status").string(remote::hostQuotaStatusId(report.status));
    return j.endObject().endObject().take();
}

std::string quotaRemovedLine(SourceKind source) {
    JsonOut j;
    return j.beginObject().key("t").string("quotaRemoved").key("source").string(sourceId(source)).endObject().take();
}

std::string quotaBridgeLine(remote::HostQuotaBridge::State state, std::string_view settingsPath) {
    auto path = displayText(settingsPath, kSettingsPathBytes).value_or("");
    if (path.size() > kSettingsPathBytes || path != settingsPath) path.clear();
    JsonOut j;
    j.beginObject().key("t").string("quotaBridge").key("state").string(remote::hostBridgeStateId(state));
    return j.key("settingsPath").string(path).endObject().take();
}

}  // namespace agentspeed::relay
