#pragma once

#include <algorithm>
#include <filesystem>
#include <fstream>
#include <optional>
#include <sstream>
#include <string>
#include <vector>

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

// Every file below a folder (relative paths), to check that reading created nothing.
inline std::vector<std::string> listFiles(const std::filesystem::path& root) {
    std::vector<std::string> out;
    std::error_code ec;
    for (auto it = std::filesystem::recursive_directory_iterator(root, ec); !ec && it != std::filesystem::end(it);
         it.increment(ec)) {
        if (it->is_regular_file()) out.push_back(std::filesystem::relative(it->path(), root).string());
    }
    std::sort(out.begin(), out.end());
    return out;
}

}  // namespace fixtures
