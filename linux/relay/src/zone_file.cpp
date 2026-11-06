#include "zone_file.hpp"

#include <fcntl.h>
#include <unistd.h>

#include <cerrno>
#include <cstdint>

namespace agentspeed::relay {

namespace {

constexpr std::size_t kMaxZoneId = 128;

int valueOf(char c) {
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '+') return 62;
    if (c == '/') return 63;
    return -1;
}

bool writeFile(const std::string& path, std::string_view data) {
    const int fd = ::open(path.c_str(), O_WRONLY | O_CREAT | O_TRUNC | O_NOFOLLOW | O_CLOEXEC, 0600);
    if (fd < 0) return false;
    bool ok = true;
    while (!data.empty()) {
        const auto n = ::write(fd, data.data(), data.size());
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) {
            ok = false;
            break;
        }
        data.remove_prefix(static_cast<std::size_t>(n));
    }
    return ::close(fd) == 0 && ok;
}

std::optional<std::string> readFile(const std::string& path, std::size_t max) {
    const int fd = ::open(path.c_str(), O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
    if (fd < 0) return std::nullopt;
    std::string out;
    char buffer[16 * 1024];
    while (true) {
        const auto n = ::read(fd, buffer, sizeof buffer);
        if (n < 0 && errno == EINTR) continue;
        if (n == 0) {
            ::close(fd);
            return out;
        }
        if (n < 0 || out.size() + static_cast<std::size_t>(n) > max) {
            ::close(fd);
            return std::nullopt;
        }
        out.append(buffer, static_cast<std::size_t>(n));
    }
}

// IANA ids only: letters, digits and `_+-/`, never `..`; TimeZone::load also refuses a leading `/`.
bool plausibleId(const std::string& id) {
    if (id.empty() || id.size() > kMaxZoneId || id.find("..") != std::string::npos) return false;
    for (const char c : id) {
        const bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '+' ||
                        c == '-' || c == '/';
        if (!ok) return false;
    }
    return true;
}

}  // namespace

std::optional<std::string> decodeBase64(std::string_view text, std::size_t max) {
    while (!text.empty() && text.back() == '=') text.remove_suffix(1);
    if (text.size() % 4 == 1 || text.size() / 4 * 3 > max) return std::nullopt;
    std::string out;
    out.reserve(text.size() / 4 * 3 + 2);
    std::uint32_t bits = 0;
    int count = 0;
    for (const char c : text) {
        const int v = valueOf(c);
        if (v < 0) return std::nullopt;
        bits = (bits << 6) | static_cast<std::uint32_t>(v);
        if (++count == 4) {
            out += static_cast<char>((bits >> 16) & 0xFF);
            out += static_cast<char>((bits >> 8) & 0xFF);
            out += static_cast<char>(bits & 0xFF);
            bits = 0;
            count = 0;
        }
    }
    if (count == 2) {
        out += static_cast<char>((bits >> 4) & 0xFF);
    } else if (count == 3) {
        out += static_cast<char>((bits >> 10) & 0xFF);
        out += static_cast<char>((bits >> 2) & 0xFF);
    }
    return out;
}

LoadedZone installZone(const std::string& folder, const std::string& id, std::string_view base64) {
    LoadedZone loaded;
    const auto path = folder + "/zone.tzif";
    if (const auto data = decodeBase64(base64); data && TimeZone::fromData(*data) && writeFile(path, *data)) {
        if (const auto written = readFile(path, kMaxZoneBytes)) loaded.zone = TimeZone::fromData(*written);
    }
    if (loaded.zone) {
        loaded.fromApp = true;
        loaded.id = id;
        return loaded;
    }
    if (plausibleId(id)) loaded.zone = TimeZone::load(id);
    if (loaded.zone) {
        loaded.id = id;
        return loaded;
    }
    loaded.zone = TimeZone::utc();
    loaded.id = "UTC";
    return loaded;
}

}  // namespace agentspeed::relay
