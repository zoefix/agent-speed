#include <algorithm>
#include <cctype>
#include <ctime>
#include <set>

#include "agentspeed/json.hpp"
#include "agentspeed/report_profile.hpp"
#include "agentspeed/report_service.hpp"

namespace agentspeed::report::profile {

namespace {

bool isUuid(std::string_view s) {
    if (s.size() != 36) return false;
    for (std::size_t i = 0; i < 36; ++i) {
        const bool dash = i == 8 || i == 13 || i == 18 || i == 23;
        if (dash ? s[i] != '-' : !((s[i] >= '0' && s[i] <= '9') || (s[i] >= 'a' && s[i] <= 'f'))) return false;
    }
    return s[14] == '4' && std::string_view("89ab").find(s[19]) != std::string_view::npos;
}

std::optional<Device> deviceOf(const json::Value& v, bool requireFlags) {
    if (!v.isObject()) return std::nullopt;
    const auto id = v["deviceId"].string();
    const auto platform = v["platform"].string();
    if (!id || !isUuid(*id) || !platform || (*platform != "macOS" && *platform != "Windows" && *platform != "Linux")) return std::nullopt;
    if (requireFlags && (!v["isCurrent"].isBool() || !v["online"].isBool())) return std::nullopt;
    return Device{*id, *platform, v["isCurrent"].isTrue(), v["online"].isTrue()};
}

std::uint32_t be32(std::string_view b, std::size_t at) {
    return (static_cast<std::uint32_t>(static_cast<unsigned char>(b[at])) << 24) | (static_cast<std::uint32_t>(static_cast<unsigned char>(b[at + 1])) << 16) |
           (static_cast<std::uint32_t>(static_cast<unsigned char>(b[at + 2])) << 8) | static_cast<std::uint32_t>(static_cast<unsigned char>(b[at + 3]));
}

std::uint32_t be16(std::string_view b, std::size_t at) {
    return (static_cast<std::uint32_t>(static_cast<unsigned char>(b[at])) << 8) | static_cast<std::uint32_t>(static_cast<unsigned char>(b[at + 1]));
}

bool dimensions(std::int64_t w, std::int64_t h) { return w > 0 && w <= 8192 && h > 0 && h <= 8192 && w * h <= 16'000'000; }

std::optional<ImageSize> readPng(std::string_view b) {
    if (be32(b, 8) != 13 || b.substr(12, 4) != "IHDR") return std::nullopt;
    const auto width = static_cast<std::int64_t>(be32(b, 16)), height = static_cast<std::int64_t>(be32(b, 20));
    if (width > INT32_MAX || height > INT32_MAX || !dimensions(width, height)) return std::nullopt;
    std::size_t at = 8;
    bool image = false;
    while (at + 12 <= b.size()) {
        const auto length = be32(b, at);
        if (length > b.size() - at - 12) return std::nullopt;
        const auto kind = b.substr(at + 4, 4);
        if (kind == "acTL" || kind == "fcTL" || kind == "fdAT" || (at > 8 && kind == "IHDR")) return std::nullopt;
        if (kind == "IDAT") image = true;
        at += length + 12;
        if (kind == "IEND") {
            if (length == 0 && image && at == b.size()) return ImageSize{static_cast<int>(width), static_cast<int>(height), true};
            return std::nullopt;
        }
    }
    return std::nullopt;
}

}  // namespace

std::optional<Profile> parseProfile(std::string_view body, const UsernameRules& rules) {
    if (body.size() > kMaxJsonBytes) return std::nullopt;
    json::Document doc;
    if (!doc.parse(body, 32) || !doc.root().isObject()) return std::nullopt;
    const auto root = doc.root();
    const auto user = root["sharedUserId"];
    if (!user.valid() || (!user.isNull() && !(user.string() && isUuid(*user.string())))) return std::nullopt;
    const auto name = root["displayName"].string();
    if (!name || validateUsername(*name, rules)) return std::nullopt;
    const auto devices = root["devices"];
    if (!devices.isArray() || devices.size() > 32) return std::nullopt;
    Profile out;
    if (!user.isNull()) out.sharedUserId = user.string();
    out.displayName = *name;
    std::set<std::string> seen;
    int current = 0;
    for (const auto& item : devices.elements()) {
        const auto device = deviceOf(item, true);
        if (!device || !seen.insert(device->deviceId).second) return std::nullopt;
        current += device->isCurrent ? 1 : 0;
        out.devices.push_back(*device);
    }
    if (current > 1) return std::nullopt;
    const auto image = root["avatar"];
    if (!image.valid()) return std::nullopt;
    if (!image.isNull()) {
        const auto url = image["url"].string();
        const auto sha = image["sha256"].string();
        if (!image.isObject() || !url || !httpsLink(*url) || !sha || sha->size() != 64 ||
            !std::all_of(sha->begin(), sha->end(), [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); }) ||
            !image["contentType"].equals("image/png") || image["width"].truncated() != kAvatarSize || image["height"].truncated() != kAvatarSize)
            return std::nullopt;
        out.avatar = Avatar{*url, *sha};
    }
    return out;
}

std::optional<Pairing> parsePairing(std::string_view body, std::int64_t nowMs) {
    if (body.size() > kMaxJsonBytes) return std::nullopt;
    json::Document doc;
    if (!doc.parse(body, 32) || !doc.root().isObject()) return std::nullopt;
    const auto root = doc.root();
    const auto id = root["id"].string();
    const auto state = root["state"].string();
    const auto expires = root["expiresAt"].string();
    static const std::set<std::string> kStates = {"waiting", "pending", "approved", "cancelled", "expired"};
    if (!id || !isUuid(*id) || !state || !kStates.contains(*state) || !expires || expires->size() != 24) return std::nullopt;
    std::tm tm{};
    const auto& e = *expires;
    if (e[4] != '-' || e[7] != '-' || e[10] != 'T' || e[13] != ':' || e[16] != ':' || e[19] != '.' || e[23] != 'Z') return std::nullopt;
    for (const auto at : {0, 1, 2, 3, 5, 6, 8, 9, 11, 12, 14, 15, 17, 18, 20, 21, 22})
        if (!std::isdigit(static_cast<unsigned char>(e[static_cast<std::size_t>(at)]))) return std::nullopt;
    tm.tm_year = std::stoi(e.substr(0, 4)) - 1900;
    tm.tm_mon = std::stoi(e.substr(5, 2)) - 1;
    tm.tm_mday = std::stoi(e.substr(8, 2));
    tm.tm_hour = std::stoi(e.substr(11, 2));
    tm.tm_min = std::stoi(e.substr(14, 2));
    tm.tm_sec = std::stoi(e.substr(17, 2));
    const auto expiresAt = static_cast<std::int64_t>(timegm(&tm)) * 1000 + std::stoi(e.substr(20, 3));
    if (expiresAt - nowMs > 11 * 60'000) return std::nullopt;
    Pairing out{*id, *state, expiresAt, std::nullopt, std::nullopt};
    if (const auto c = root["claimant"]; c.valid() && !c.isNull()) {
        out.claimant = deviceOf(c, false);
        if (!out.claimant) return std::nullopt;
    }
    if (const auto code = root["code"].string()) {
        if (!normalizeCode(*code)) return std::nullopt;
        out.code = normalizeCode(*code);
    }
    if (out.state == "pending" && !out.claimant) return std::nullopt;
    return out;
}

std::optional<std::string> normalizeCode(std::string_view code) {
    if (code.size() > 32) return std::nullopt;
    std::string value;
    for (const char c : code)
        if (c != '-') value += static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
    static constexpr std::string_view kAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    if (value.size() != 12 || !std::all_of(value.begin(), value.end(), [](char c) { return kAlphabet.find(c) != std::string_view::npos; }))
        return std::nullopt;
    return value;
}

std::string displayCode(std::string_view normalized) {
    if (normalized.size() != 12) return std::string(normalized);
    return std::string(normalized.substr(0, 4)) + "-" + std::string(normalized.substr(4, 4)) + "-" + std::string(normalized.substr(8, 4));
}

std::optional<ImageSize> imageHeader(std::string_view b) {
    if (b.size() > kMaxInputBytes) return std::nullopt;
    if (b.size() >= 33 && b.substr(0, 8) == std::string_view("\x89PNG\r\n\x1a\n", 8)) return readPng(b);
    if (b.size() < 4 || static_cast<unsigned char>(b[0]) != 0xFF || static_cast<unsigned char>(b[1]) != 0xD8) return std::nullopt;
    std::size_t at = 2;
    while (at + 3 < b.size()) {
        if (static_cast<unsigned char>(b[at++]) != 0xFF) return std::nullopt;
        while (at < b.size() && static_cast<unsigned char>(b[at]) == 0xFF) ++at;
        if (at >= b.size()) return std::nullopt;
        const auto marker = static_cast<unsigned char>(b[at++]);
        if (marker == 0xD9 || marker == 0xDA || at + 2 > b.size()) return std::nullopt;
        if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
        const auto length = be16(b, at);
        if (length < 2 || at + length > b.size()) return std::nullopt;
        if (marker == 0xC0 || marker == 0xC1 || marker == 0xC2) {
            if (length < 8) return std::nullopt;
            const auto height = be16(b, at + 3), width = be16(b, at + 5);
            if (!dimensions(width, height)) return std::nullopt;
            return ImageSize{static_cast<int>(width), static_cast<int>(height), false};
        }
        at += length;
    }
    return std::nullopt;
}

bool isUpload(std::string_view png) {
    if (png.size() > kMaxPngBytes) return false;
    const auto header = imageHeader(png);
    return header && header->png && header->width == kAvatarSize && header->height == kAvatarSize;
}

}  // namespace agentspeed::report::profile
