#pragma once

#include <algorithm>
#include <cstdio>
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

// Python's json.dumps(value, ensure_ascii=False, separators=(",", ":")): the fixtures' compact lines.
inline void appendCompact(std::string& out, const agentspeed::json::Value& v) {
    using agentspeed::json::Type;
    auto string = [&](const std::string& text) {
        out += '"';
        for (unsigned char c : text) {
            switch (c) {
                case '"': out += "\\\""; break;
                case '\\': out += "\\\\"; break;
                case '\n': out += "\\n"; break;
                case '\r': out += "\\r"; break;
                case '\t': out += "\\t"; break;
                case '\b': out += "\\b"; break;
                case '\f': out += "\\f"; break;
                default:
                    if (c < 0x20) {
                        char buf[8];
                        std::snprintf(buf, sizeof buf, "\\u%04x", c);
                        out += buf;
                    } else {
                        out += static_cast<char>(c);
                    }
            }
        }
        out += '"';
    };
    switch (v.type()) {
        case Type::Object: {
            out += '{';
            bool first = true;
            for (const auto& [key, value] : v.members()) {
                if (!first) out += ',';
                first = false;
                string(key);
                out += ':';
                appendCompact(out, value);
            }
            out += '}';
            break;
        }
        case Type::Array: {
            out += '[';
            bool first = true;
            for (const auto& item : v.elements()) {
                if (!first) out += ',';
                first = false;
                appendCompact(out, item);
            }
            out += ']';
            break;
        }
        case Type::String: string(*v.string()); break;
        default: out += std::string(v.raw()); break;
    }
}

inline std::string compactJson(const agentspeed::json::Value& v) {
    std::string out;
    appendCompact(out, v);
    return out;
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
