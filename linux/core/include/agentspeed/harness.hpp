#pragma once

#include <cstdint>
#include <string_view>
#include <memory>
#include <optional>
#include <string>

#include "agentspeed/json.hpp"
#include "agentspeed/model.hpp"

// Shared pieces of the harness sources: ZCode (4.10), Kimi (4.11) and Grok Build (4.12).
namespace agentspeed::harness {

// A small JSON metadata file kept with its parsed document.
struct Metadata {
    std::string text;
    json::Document document;
    json::Value root() const { return document.root(); }
};

// Reads a regular file of 1 … maxBytes bytes holding a JSON object (nesting ≤ 64); nullptr otherwise.
std::unique_ptr<Metadata> readMetadata(const std::string& path, std::int64_t maxBytes);

// A finite number ≥ 0 (truncated) or an RFC 3339 string.
std::optional<std::int64_t> milliseconds(const json::Value& value);
std::optional<std::int64_t> validTimestamp(std::optional<std::int64_t> value);
inline std::optional<std::int64_t> timestamp(const json::Value& value) { return validTimestamp(milliseconds(value)); }

TokenCounts kimiUsage(const json::Value& usage);

std::string sessionsOf(const std::string& root);
std::string zcodeDatabase(const std::string& root);

// A name that is safe as one path component: no separators, colons or control characters, not `.` or `..`, and at
// most `maxUnits` UTF-16 code units.
bool isPlainComponent(std::string_view name, std::size_t maxUnits = SIZE_MAX);

}  // namespace agentspeed::harness
