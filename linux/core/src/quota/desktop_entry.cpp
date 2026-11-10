#include "agentspeed/quota/desktop_entry.hpp"

#include <openssl/evp.h>

#include <algorithm>
#include <cstdio>

#include "agentspeed/quota/files.hpp"

namespace agentspeed::quota::desktop {

namespace {

constexpr std::uint64_t kHeaderMagic = 0xFCFB6D1BA7725C30ULL;
constexpr std::uint64_t kEofMagic = 0xF4FA6F45970D41D8ULL;
constexpr std::uint32_t kVersion = 5;
constexpr std::uint32_t kKeyHashFlag = 0x2;
constexpr std::string_view kUsagePrefix = "https://claude.ai/api/organizations/";

std::uint64_t u64At(std::string_view bytes, std::size_t at) {
    std::uint64_t value = 0;
    for (std::size_t k = 0; k < 8; ++k) value |= static_cast<std::uint64_t>(static_cast<unsigned char>(bytes[at + k])) << (8 * k);
    return value;
}

std::uint32_t u32At(std::string_view bytes, std::size_t at) {
    std::uint32_t value = 0;
    for (std::size_t k = 0; k < 4; ++k) value |= static_cast<std::uint32_t>(static_cast<unsigned char>(bytes[at + k])) << (8 * k);
    return value;
}

bool isDigit(char c) { return c >= '0' && c <= '9'; }

bool isHex(char c) { return isDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'); }

bool isSpace(char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f'; }

std::string_view trimmed(std::string_view text) {
    while (!text.empty() && isSpace(text.front())) text.remove_prefix(1);
    while (!text.empty() && isSpace(text.back())) text.remove_suffix(1);
    return text;
}

std::string lowered(std::string_view text) {
    std::string out(text);
    for (auto& c : out) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return out;
}

// `<digits>/` at `at`; the position after it, else npos.
std::size_t afterNumberSlash(std::string_view text, std::size_t at) {
    const std::size_t start = at;
    while (at < text.size() && isDigit(text[at])) ++at;
    if (at == start || at >= text.size() || text[at] != '/') return std::string_view::npos;
    return at + 1;
}

}  // namespace

std::string entryFileName(std::string_view key) {
    unsigned char digest[EVP_MAX_MD_SIZE];
    unsigned int length = 0;
    if (EVP_Digest(key.data(), key.size(), digest, &length, EVP_sha1(), nullptr) != 1 || length < 8) return {};
    std::uint64_t value = 0;
    for (std::size_t k = 0; k < 8; ++k) value |= static_cast<std::uint64_t>(digest[k]) << (8 * k);
    char text[24];
    std::snprintf(text, sizeof text, "%016llx_0", static_cast<unsigned long long>(value));
    return text;
}

bool isEntryFileName(std::string_view name) {
    if (name.size() != 18 || name.substr(16) != "_0") return false;
    for (std::size_t i = 0; i < 16; ++i) {
        if (!isDigit(name[i]) && !(name[i] >= 'a' && name[i] <= 'f')) return false;
    }
    return true;
}

std::optional<std::uint32_t> keyLength(std::string_view header) {
    if (header.size() < kHeaderSize || u64At(header, 0) != kHeaderMagic || u32At(header, 8) != kVersion) return std::nullopt;
    return u32At(header, 12);
}

std::string urlOfKey(std::string_view key) {
    if (const auto space = key.rfind(' '); space != std::string_view::npos) return std::string(key.substr(space + 1));
    const auto first = afterNumberSlash(key, 0);
    if (first == std::string_view::npos) return std::string(key);
    const auto second = afterNumberSlash(key, first);
    if (second == std::string_view::npos) return std::string(key);
    return std::string(key.substr(second));
}

bool isUsageUrl(std::string_view url) {
    if (url.substr(0, kUsagePrefix.size()) != kUsagePrefix) return false;
    auto rest = url.substr(kUsagePrefix.size());
    constexpr std::size_t kGroups[] = {8, 4, 4, 4, 12};
    for (std::size_t g = 0; g < 5; ++g) {
        if (rest.size() < kGroups[g]) return false;
        for (std::size_t i = 0; i < kGroups[g]; ++i) {
            if (!isHex(rest[i])) return false;
        }
        rest.remove_prefix(kGroups[g]);
        if (g < 4) {
            if (rest.empty() || rest.front() != '-') return false;
            rest.remove_prefix(1);
        }
    }
    if (rest.substr(0, 6) != "/usage") return false;
    rest.remove_prefix(6);
    return rest.empty() || (rest.front() == '?' && rest.find('\n') == std::string_view::npos);
}

std::optional<CacheEntry> parseCacheEntry(std::string_view bytes) {
    if (bytes.size() > kFileLimit || bytes.size() < kHeaderSize + 2 * kEofSize) return std::nullopt;
    const auto length = keyLength(bytes);
    if (!length) return std::nullopt;
    const std::size_t keyEnd = kHeaderSize + *length;
    if (keyEnd > bytes.size()) return std::nullopt;
    const auto key = bytes.substr(kHeaderSize, *length);
    if (!files::validUtf8(key)) return std::nullopt;
    const std::size_t eof0 = bytes.size() - kEofSize;
    if (u64At(bytes, eof0) != kEofMagic) return std::nullopt;
    const std::uint32_t flags = u32At(bytes, eof0 + 8);
    const std::uint32_t size0 = u32At(bytes, eof0 + 16);
    const std::size_t stream0End = eof0 - ((flags & kKeyHashFlag) != 0 ? kKeyHashSize : 0);
    if (size0 > stream0End || stream0End - size0 < kEofSize) return std::nullopt;
    const std::size_t stream0Start = stream0End - size0;
    const std::size_t eof1 = stream0Start - kEofSize;
    if (eof1 < keyEnd || u64At(bytes, eof1) != kEofMagic) return std::nullopt;
    return CacheEntry{std::string(key), bytes.substr(keyEnd, eof1 - keyEnd), bytes.substr(stream0Start, size0)};
}

std::optional<std::string> ResponseInfo::header(std::string_view name) const {
    for (const auto& [key, value] : headers) {
        if (key == name) return value;
    }
    return std::nullopt;
}

std::optional<ResponseInfo> parseResponseInfo(std::string_view stream) {
    const auto start = stream.find("HTTP/");
    if (start == std::string_view::npos) return std::nullopt;
    std::vector<std::string_view> lines;
    for (std::size_t at = start; at < stream.size();) {
        const auto end = std::min(stream.find('\0', at), stream.size());
        if (end == at) break;
        lines.push_back(stream.substr(at, end - at));
        at = end + 1;
    }
    if (lines.empty()) return std::nullopt;
    ResponseInfo info;
    const auto first = lines.front();
    if (const auto space = first.find(' '); space != std::string_view::npos) {
        auto code = first.substr(space + 1);
        code = code.substr(0, code.find(' '));
        bool digits = !code.empty() && code.size() <= 9;
        for (const char c : code) digits = digits && isDigit(c);
        if (digits) {
            int value = 0;
            for (const char c : code) value = value * 10 + (c - '0');
            info.status = value;
        }
    }
    for (std::size_t i = 1; i < lines.size(); ++i) {
        const auto colon = lines[i].find(':');
        if (colon == std::string_view::npos) continue;
        auto name = lowered(trimmed(lines[i].substr(0, colon)));
        if (info.header(name)) continue;
        info.headers.emplace_back(std::move(name), std::string(trimmed(lines[i].substr(colon + 1))));
    }
    return info;
}

std::optional<Coding> codingOf(const std::optional<std::string>& header) {
    const auto name = lowered(trimmed(header ? std::string_view(*header) : std::string_view("identity")));
    if (name == "identity" || name.empty()) return Coding::Identity;
    if (name == "zstd") return Coding::Zstd;
    if (name == "gzip") return Coding::Gzip;
    if (name == "br") return Coding::Brotli;
    return std::nullopt;
}

}  // namespace agentspeed::quota::desktop
