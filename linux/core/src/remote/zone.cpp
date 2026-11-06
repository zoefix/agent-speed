#include <unistd.h>

#include <algorithm>
#include <cstdlib>
#include <ctime>
#include <fstream>
#include <iterator>

#include "agentspeed/remote_protocol.hpp"
#include "agentspeed/tz.hpp"

// REMOTE 5.3 `start.zone`: the app's zone id with its TZif bytes, so the relay labels days as this computer does.
namespace agentspeed::remote {

namespace {

constexpr std::size_t kMaxTzifBytes = 1u << 20;
constexpr std::string_view kZoneinfo = "/usr/share/zoneinfo/";

std::optional<std::string> readFile(const std::string& path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) return std::nullopt;
    std::string data;
    data.resize(kMaxTzifBytes + 1);
    in.read(data.data(), static_cast<std::streamsize>(data.size()));
    data.resize(static_cast<std::size_t>(in.gcount()));
    if (data.size() > kMaxTzifBytes) return std::nullopt;
    return data;
}

std::optional<std::string> zoneinfo(const std::string& id) {
    if (!Zone::validId(id)) return std::nullopt;
    auto data = readFile(std::string(kZoneinfo) + id);
    if (!data || !Zone::isTzif(*data)) return std::nullopt;
    return data;
}

std::optional<std::string> linkedId() {
    char buffer[4096];
    const auto n = ::readlink("/etc/localtime", buffer, sizeof buffer - 1);
    if (n <= 0) return std::nullopt;
    const std::string target(buffer, static_cast<std::size_t>(n));
    const auto at = target.find("zoneinfo/");
    if (at == std::string::npos) return std::nullopt;
    return target.substr(at + 9);
}

std::optional<std::string> configuredId() {
    std::ifstream in("/etc/timezone");
    std::string id;
    if (!in || !std::getline(in, id)) return std::nullopt;
    while (!id.empty() && (id.back() == ' ' || id.back() == '\r')) id.pop_back();
    return id;
}

}  // namespace

bool Zone::validId(std::string_view id) {
    if (id.empty() || id.size() > 64 || id.front() == '/' || id.find("..") != std::string_view::npos) return false;
    return std::all_of(id.begin(), id.end(), [](char c) {
        return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-' || c == '/' ||
               c == '+';
    });
}

bool Zone::isTzif(std::string_view data) { return data.size() >= 44 && data.substr(0, 4) == "TZif"; }

std::string utcTzif() {
    std::string bytes = "TZif";
    bytes.append(16, '\0');
    for (const std::uint32_t count : {0u, 0u, 0u, 0u, 1u, 4u}) {
        for (int shift = 24; shift >= 0; shift -= 8) bytes += static_cast<char>((count >> shift) & 0xFF);
    }
    bytes.append(6, '\0');
    bytes += std::string("UTC", 3);
    bytes += '\0';
    return bytes;
}

Zone currentZone(const std::optional<std::string>& preferredId) {
    std::vector<std::string> ids;
    if (preferredId) ids.push_back(*preferredId);
    if (const char* tz = std::getenv("TZ"); tz && *tz) ids.emplace_back(tz[0] == ':' ? tz + 1 : tz);
    if (auto id = linkedId()) ids.push_back(*id);
    if (auto id = configuredId()) ids.push_back(*id);
    for (const auto& id : ids) {
        if (auto data = zoneinfo(id)) return {id, std::move(*data)};
    }
    // A zone known only by /etc/localtime: its whole-hour offset as the Etc/GMT∓N zone, else UTC.
    if (const auto local = readFile("/etc/localtime"); local && Zone::isTzif(*local)) {
        if (const auto zone = TimeZone::fromData(*local)) {
            const auto now = static_cast<std::int64_t>(::time(nullptr)) * 1000;
            const auto seconds = zone->offsetMs(now) / 1000;
            if (seconds % 3600 == 0) {
                const auto hours = -seconds / 3600;
                const auto id = hours == 0 ? std::string("Etc/GMT") : "Etc/GMT" + std::string(hours > 0 ? "+" : "") + std::to_string(hours);
                if (auto data = zoneinfo(id)) return {id, std::move(*data)};
            }
        }
    }
    if (auto data = zoneinfo("UTC")) return {"UTC", std::move(*data)};
    return {"UTC", utcTzif()};
}

}  // namespace agentspeed::remote
