#pragma once

#include <filesystem>
#include <fstream>
#include <optional>
#include <sstream>
#include <string>

#include "agentspeed/json.hpp"

namespace fixtures {

inline std::filesystem::path specDir() { return std::filesystem::path(AGENTSPEED_SPEC_DIR); }
inline std::filesystem::path fixtureDir() { return specDir() / "fixtures"; }

inline std::string readText(const std::filesystem::path& path) {
    std::ifstream in(path, std::ios::binary);
    std::stringstream buffer;
    buffer << in.rdbuf();
    return buffer.str();
}

// Keeps the text alive with its parsed document.
struct JsonFile {
    std::string text;
    agentspeed::json::Document doc;
    explicit JsonFile(const std::filesystem::path& path) : text(readText(path)) { ok = doc.parse(text); }
    agentspeed::json::Value root() const { return doc.root(); }
    bool ok = false;
};

inline std::optional<std::int64_t> optInt(const agentspeed::json::Value& v) {
    if (!v.valid() || v.isNull()) return std::nullopt;
    return v.truncated();
}

inline std::optional<std::string> optString(const agentspeed::json::Value& v) {
    if (!v.valid() || v.isNull()) return std::nullopt;
    return v.string();
}

}  // namespace fixtures
