#include "json_out.hpp"

#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdio>

namespace agentspeed::relay {

namespace {

constexpr std::string_view kReplacement = "\xEF\xBF\xBD";

bool continuation(unsigned char c) { return (c & 0xC0) == 0x80; }

// The length of the valid UTF-8 sequence at `at` (RFC 3629: no overlongs, surrogates or code points over U+10FFFF);
// 0 when the byte there starts none.
std::size_t sequenceAt(std::string_view text, std::size_t at) {
    const auto byte = [&](std::size_t k) { return static_cast<unsigned char>(text[at + k]); };
    const unsigned char c = byte(0);
    if (c < 0x80) return 1;
    const std::size_t left = text.size() - at;
    if (c >= 0xC2 && c <= 0xDF) return left >= 2 && continuation(byte(1)) ? 2 : 0;
    if (c >= 0xE0 && c <= 0xEF) {
        if (left < 3 || !continuation(byte(1)) || !continuation(byte(2))) return 0;
        if (c == 0xE0 && byte(1) < 0xA0) return 0;
        if (c == 0xED && byte(1) > 0x9F) return 0;
        return 3;
    }
    if (c >= 0xF0 && c <= 0xF4) {
        if (left < 4 || !continuation(byte(1)) || !continuation(byte(2)) || !continuation(byte(3))) return 0;
        if (c == 0xF0 && byte(1) < 0x90) return 0;
        if (c == 0xF4 && byte(1) > 0x8F) return 0;
        return 4;
    }
    return 0;
}

void appendQuoted(std::string& out, std::string_view text) {
    out += '"';
    std::size_t i = 0;
    while (i < text.size()) {
        const auto c = static_cast<unsigned char>(text[i]);
        if (c >= 0x80) {
            const auto length = sequenceAt(text, i);
            if (length == 0) {
                out += kReplacement;
                ++i;
            } else {
                out.append(text.substr(i, length));
                i += length;
            }
            continue;
        }
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20 || c == 0x7F) {
                    char buffer[8];
                    std::snprintf(buffer, sizeof buffer, "\\u%04x", static_cast<unsigned>(c));
                    out += buffer;
                } else {
                    out += static_cast<char>(c);
                }
        }
        ++i;
    }
    out += '"';
}

}  // namespace

std::string sanitizeUtf8(std::string_view text) {
    std::string out;
    out.reserve(text.size());
    std::size_t i = 0;
    while (i < text.size()) {
        const auto length = sequenceAt(text, i);
        if (length == 0) {
            out += kReplacement;
            ++i;
        } else {
            out.append(text.substr(i, length));
            i += length;
        }
    }
    return out;
}

bool hasControl(std::string_view text) {
    for (const char ch : text) {
        const auto c = static_cast<unsigned char>(ch);
        if (c < 0x20 || c == 0x7F) return true;
    }
    return false;
}

void JsonOut::beforeValue() {
    if (afterKey_) {
        afterKey_ = false;
        return;
    }
    if (!counts_.empty() && counts_.back()++ > 0) out_ += ',';
}

JsonOut& JsonOut::beginObject() {
    beforeValue();
    out_ += '{';
    counts_.push_back(0);
    return *this;
}

JsonOut& JsonOut::endObject() {
    out_ += '}';
    counts_.pop_back();
    return *this;
}

JsonOut& JsonOut::beginArray() {
    beforeValue();
    out_ += '[';
    counts_.push_back(0);
    return *this;
}

JsonOut& JsonOut::endArray() {
    out_ += ']';
    counts_.pop_back();
    return *this;
}

JsonOut& JsonOut::key(std::string_view name) {
    if (!counts_.empty() && counts_.back()++ > 0) out_ += ',';
    appendQuoted(out_, name);
    out_ += ':';
    afterKey_ = true;
    return *this;
}

JsonOut& JsonOut::string(std::string_view text) {
    beforeValue();
    appendQuoted(out_, text);
    return *this;
}

JsonOut& JsonOut::integer(std::int64_t value) {
    beforeValue();
    out_ += std::to_string(value);
    return *this;
}

JsonOut& JsonOut::boolean(bool value) {
    beforeValue();
    out_ += value ? "true" : "false";
    return *this;
}

JsonOut& JsonOut::null() {
    beforeValue();
    out_ += "null";
    return *this;
}

JsonOut& JsonOut::number(double value) {
    beforeValue();
    if (!std::isfinite(value) || value == 0) {
        out_ += '0';
        return *this;
    }
    char buffer[64];
    const auto result = std::to_chars(buffer, buffer + sizeof buffer, value);
    out_.append(buffer, result.ptr);
    return *this;
}

JsonOut& JsonOut::fraction(double value) {
    beforeValue();
    const double clamped = std::isfinite(value) ? std::clamp(value, 0.0, 1.0) : 0.0;
    char buffer[16];
    const auto result = std::to_chars(buffer, buffer + sizeof buffer, std::round(clamped * 1000) / 1000, std::chars_format::fixed, 3);
    std::string text(buffer, result.ptr);
    while (text.back() == '0') text.pop_back();
    if (text.back() == '.') text.pop_back();
    out_ += text;
    return *this;
}

JsonOut& JsonOut::raw(std::string_view json) {
    beforeValue();
    out_ += json;
    return *this;
}

JsonOut& JsonOut::optionalString(const std::optional<std::string>& text) { return text ? string(*text) : null(); }

JsonOut& JsonOut::optionalInteger(const std::optional<std::int64_t>& value) { return value ? integer(*value) : null(); }

JsonOut& JsonOut::optionalBoolean(const std::optional<bool>& value) { return value ? boolean(*value) : null(); }

}  // namespace agentspeed::relay
