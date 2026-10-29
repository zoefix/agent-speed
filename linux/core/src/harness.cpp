#include "agentspeed/harness.hpp"

#include <sys/stat.h>

#include <algorithm>
#include <fstream>

#include "agentspeed/rfc3339.hpp"

namespace agentspeed::harness {

namespace {

std::string lower(std::string text) {
    for (char& c : text) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return text;
}

std::string trimSeparators(std::string path) {
    while (path.size() > 1 && path.back() == '/') path.pop_back();
    return path;
}

bool isFile(const std::string& path) {
    struct stat st {};
    return ::stat(path.c_str(), &st) == 0 && S_ISREG(st.st_mode);
}

}  // namespace

std::unique_ptr<Metadata> readMetadata(const std::string& path, std::int64_t maxBytes) {
    struct stat st {};
    if (::lstat(path.c_str(), &st) != 0 || !S_ISREG(st.st_mode)) return nullptr;
    if (st.st_size <= 0 || st.st_size > maxBytes) return nullptr;
    std::ifstream in(path, std::ios::binary);
    if (!in) return nullptr;
    auto metadata = std::make_unique<Metadata>();
    metadata->text.resize(static_cast<std::size_t>(st.st_size));
    in.read(metadata->text.data(), static_cast<std::streamsize>(metadata->text.size()));
    if (in.gcount() != static_cast<std::streamsize>(metadata->text.size())) return nullptr;
    if (!metadata->document.parse(metadata->text, 64) || !metadata->root().isObject()) return nullptr;
    return metadata;
}

std::optional<std::int64_t> milliseconds(const json::Value& value) {
    if (value.isNumber()) {
        const auto number = value.finiteNumber();
        if (number && *number >= 0 && *number < 9223372036854775807.0) return static_cast<std::int64_t>(*number);
        return std::nullopt;
    }
    if (const auto text = value.nonEmptyString(); text && text->size() <= 64) return parseTimestamp(*text);
    return std::nullopt;
}

std::optional<std::int64_t> validTimestamp(std::optional<std::int64_t> value) {
    if (value && *value >= -62'135'596'800'000 && *value <= 253'402'300'799'999) return value;
    return std::nullopt;
}

TokenCounts kimiUsage(const json::Value& usage) {
    const auto cache = usage["inputCacheRead"].count();
    const auto write = usage["inputCacheCreation"].count();
    const auto input = TokenCounts::saturatingAdd(usage["inputOther"].count(), TokenCounts::saturatingAdd(cache, write));
    const auto output = usage["output"].count();
    const auto raw = usage["raw"];
    auto reasoning = raw["reasoning_tokens"].count();
    if (reasoning == 0) reasoning = raw["completion_tokens_details"]["reasoning_tokens"].count();
    TokenCounts t;
    t.input = input;
    t.cachedInput = std::min(input, cache);
    t.cacheWriteInput = std::min(input, write);
    t.output = output;
    t.reasoning = std::min(output, reasoning);
    return t;
}

std::string sessionsOf(const std::string& root) {
    const auto trimmed = trimSeparators(root);
    const auto slash = trimmed.rfind('/');
    const auto name = slash == std::string::npos ? trimmed : trimmed.substr(slash + 1);
    return lower(name) == "sessions" ? root : trimmed + "/sessions";
}

std::string zcodeDatabase(const std::string& root) {
    if (std::all_of(root.begin(), root.end(), [](char c) { return c == ' ' || (c >= '\t' && c <= '\r'); })) return {};
    const auto path = trimSeparators(root);
    const auto slash = path.rfind('/');
    const auto dot = path.rfind('.');
    if (dot != std::string::npos && (slash == std::string::npos || dot > slash) && lower(path.substr(dot)) == ".sqlite") return path;
    for (const char* candidate : {"db.sqlite", "db/db.sqlite", "cli/db/db.sqlite", ".zcode/cli/db/db.sqlite"}) {
        const auto database = path + "/" + candidate;
        if (isFile(database)) return database;
    }
    return path + "/db.sqlite";
}

bool isPlainComponent(std::string_view name, std::size_t maxUnits) {
    if (name.empty() || name == "." || name == "..") return false;
    std::size_t units = 0;
    for (std::size_t i = 0; i < name.size(); ++i) {
        const auto c = static_cast<unsigned char>(name[i]);
        if ((c & 0xC0) != 0x80) units += c >= 0xF0 ? 2 : 1;
        if (c == '/' || c == '\\' || c == ':' || c < 0x20 || c == 0x7F) return false;
        // C1 controls U+0080 … U+009F.
        if (c == 0xC2 && i + 1 < name.size()) {
            const auto next = static_cast<unsigned char>(name[i + 1]);
            if (next >= 0x80 && next <= 0x9F) return false;
        }
    }
    return units <= maxUnits;
}

}  // namespace agentspeed::harness
