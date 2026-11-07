#include <algorithm>

#include "agentspeed/lan_protocol.hpp"
#include "agentspeed/text.hpp"

namespace agentspeed::lan {

namespace {

// Strict UTF-8: no overlong forms, surrogates or code points above U+10FFFF.
bool validUtf8(std::string_view s) {
    for (std::size_t i = 0; i < s.size();) {
        const auto b0 = static_cast<unsigned char>(s[i]);
        std::size_t length = b0 < 0x80 ? 1 : (b0 & 0xE0) == 0xC0 ? 2 : (b0 & 0xF0) == 0xE0 ? 3 : (b0 & 0xF8) == 0xF0 ? 4 : 0;
        if (length == 0 || i + length > s.size()) return false;
        std::uint32_t cp = length == 1 ? b0 : length == 2 ? b0 & 0x1Fu : length == 3 ? b0 & 0x0Fu : b0 & 0x07u;
        for (std::size_t k = 1; k < length; ++k) {
            const auto b = static_cast<unsigned char>(s[i + k]);
            if ((b & 0xC0) != 0x80) return false;
            cp = (cp << 6) | (b & 0x3Fu);
        }
        static constexpr std::uint32_t kMinimum[] = {0, 0, 0x80, 0x800, 0x10000};
        if (cp < kMinimum[length] || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) return false;
        i += length;
    }
    return true;
}

// `METHOD SP target SP HTTP/1.x`: METHOD in A–Z, the target without white space.
bool requestLine(std::string_view line, std::string* method = nullptr, std::string* target = nullptr) {
    std::size_t i = 0;
    while (i < line.size() && line[i] >= 'A' && line[i] <= 'Z') ++i;
    if (i == 0 || i >= line.size() || line[i] != ' ') return false;
    const auto methodEnd = i++;
    const auto targetStart = i;
    while (i < line.size()) {
        auto next = i;
        if (text::isPythonSpace(text::nextCodePoint(line, next))) break;
        i = next;
    }
    if (i == targetStart) return false;
    const auto rest = line.substr(i);
    if (rest != " HTTP/1.0" && rest != " HTTP/1.1") return false;
    if (method) *method = std::string(line.substr(0, methodEnd));
    if (target) *target = std::string(line.substr(targetStart, i - targetStart));
    return true;
}

std::string lower(std::string_view s) {
    std::string out(s);
    std::transform(out.begin(), out.end(), out.begin(), [](unsigned char c) { return static_cast<char>(c >= 'A' && c <= 'Z' ? c + 32 : c); });
    return out;
}

std::string_view withoutCr(std::string_view line) { return !line.empty() && line.back() == '\r' ? line.substr(0, line.size() - 1) : line; }

}  // namespace

FirstLine classifyFirstLine(std::string_view data, bool final) {
    const auto end = data.find('\n');
    if (end == std::string_view::npos) return final || data.size() >= kHeadLimit ? FirstLine::Close : FirstLine::Pending;
    if (end >= kHeadLimit) return FirstLine::Close;
    const auto line = withoutCr(data.substr(0, end));
    if (!validUtf8(line)) return FirstLine::Close;
    if (requestLine(line)) return FirstLine::Http;
    return !line.empty() && line.front() == '{' ? FirstLine::Line : FirstLine::Close;
}

const std::string* Head::header(std::string_view name) const {
    for (const auto& [key, value] : headers)
        if (key == name) return &value;
    return nullptr;
}

HeadParse parseHead(std::string_view data, bool final) {
    HeadParse out;
    auto end = data.find("\r\n\r\n");
    std::size_t sep = 4;
    if (const auto alt = data.find("\n\n"); end == std::string_view::npos || (alt != std::string_view::npos && alt < end)) {
        end = alt;
        sep = 2;
    }
    if (end == std::string_view::npos) {
        out.state = final || data.size() >= kHeadLimit ? HeadState::Invalid : HeadState::Pending;
        return out;
    }
    if (end + sep > kHeadLimit) {
        out.state = HeadState::Invalid;
        return out;
    }
    const auto text = text::sanitizeUtf8(data.substr(0, end));
    std::vector<std::string_view> lines;
    for (std::size_t start = 0;;) {
        const auto nl = text.find('\n', start);
        lines.push_back(withoutCr(std::string_view(text).substr(start, nl == std::string::npos ? std::string::npos : nl - start)));
        if (nl == std::string::npos) break;
        start = nl + 1;
    }
    auto& head = out.head;
    if (!requestLine(lines.front(), &head.method, &head.target)) {
        out.state = HeadState::Invalid;
        return out;
    }
    out.state = HeadState::Ok;
    out.length = end + sep;
    for (std::size_t i = 1; i < lines.size(); ++i) {
        const auto colon = lines[i].find(':');
        if (colon == std::string_view::npos) {
            head.malformed = true;
            break;
        }
        auto name = lower(text::strip(lines[i].substr(0, colon)));
        if (head.header(name)) continue;
        head.headers.emplace_back(std::move(name), text::strip(lines[i].substr(colon + 1)));
    }
    return out;
}

}  // namespace agentspeed::lan
