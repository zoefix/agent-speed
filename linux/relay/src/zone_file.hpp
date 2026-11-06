#pragma once

#include <memory>
#include <optional>
#include <string>
#include <string_view>

#include "agentspeed/tz.hpp"

// REMOTE 8.1: the app's time zone, sent as base64 TZif, kept in `<folder>/zone.tzif` and loaded from there.
namespace agentspeed::relay {

inline constexpr std::size_t kMaxZoneBytes = 1u << 20;

// Standard base64 with or without padding; nullopt for any other character or more than `max` bytes.
std::optional<std::string> decodeBase64(std::string_view text, std::size_t max = kMaxZoneBytes);

struct LoadedZone {
    std::shared_ptr<const TimeZone> zone;
    std::string id;     // the id the store's days are labelled with
    bool fromApp = false;  // false: the app's data was unusable and the host's zone files (or UTC) stood in
};

// Writes the decoded data to `<folder>/zone.tzif` (never through a symbolic link) and loads it back; when that fails,
// the id from the host's zone files, else UTC.
LoadedZone installZone(const std::string& folder, const std::string& id, std::string_view base64);

}  // namespace agentspeed::relay
