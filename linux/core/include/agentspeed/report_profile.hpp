#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/report.hpp"

// The profile extensions of live reporting (REPORTING-EXTENSIONS 4–6): the profile snapshot, avatars and one-time
// pairing of devices into a shared identity.
namespace agentspeed::report::profile {

constexpr std::size_t kMaxJsonBytes = 65'536;
constexpr std::size_t kMaxInputBytes = 10 * 1024 * 1024;
constexpr std::size_t kMaxPngBytes = 512 * 1024;
constexpr int kAvatarSize = 256;

struct Avatar {
    std::string url, sha256;
};
struct Device {
    std::string deviceId, platform;
    bool isCurrent = false, online = false;
};
struct Profile {
    std::optional<std::string> sharedUserId;
    std::string displayName;
    std::optional<Avatar> avatar;
    std::vector<Device> devices;
};
struct Pairing {
    std::string id, state;  // waiting, pending, approved, cancelled, expired
    std::int64_t expiresAtMs = 0;
    std::optional<Device> claimant;
    std::optional<std::string> code;  // only in the answer that created the invitation
};

// nullopt when the body is not a valid snapshot (sizes, ids, links, the username rules, at most 32 devices).
std::optional<Profile> parseProfile(std::string_view body, const UsernameRules& rules);
// `nowMs` on the corrected clock: an expiry more than 11 minutes ahead is refused.
std::optional<Pairing> parsePairing(std::string_view body, std::int64_t nowMs);
// XXXX-XXXX-XXXX in any case and with or without hyphens → the 12 characters, or nullopt.
std::optional<std::string> normalizeCode(std::string_view code);
std::string displayCode(std::string_view normalized);

struct ImageSize {
    int width = 0, height = 0;
    bool png = false;
};
// The dimensions from a PNG or JPEG header, checked before decoding (≤ 8192 a side, ≤ 16 000 000 pixels); a PNG must be
// one still image.
std::optional<ImageSize> imageHeader(std::string_view bytes);
// What may be uploaded: a 256 × 256 single PNG of at most 512 KiB.
bool isUpload(std::string_view png);

}  // namespace agentspeed::report::profile
