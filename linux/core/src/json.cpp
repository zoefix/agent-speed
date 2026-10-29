#include "agentspeed/json.hpp"

#include <charconv>
#include <cmath>
#include <limits>

namespace agentspeed::json {

namespace {

constexpr std::int64_t kTokenCap = 1'000'000'000'000;

bool isHex(char c) {
    return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}

unsigned hexValue(char c) {
    if (c >= '0' && c <= '9') return static_cast<unsigned>(c - '0');
    if (c >= 'a' && c <= 'f') return static_cast<unsigned>(c - 'a' + 10);
    return static_cast<unsigned>(c - 'A' + 10);
}

void appendUtf8(std::string& out, std::uint32_t cp) {
    if (cp < 0x80) {
        out.push_back(static_cast<char>(cp));
    } else if (cp < 0x800) {
        out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else if (cp < 0x10000) {
        out.push_back(static_cast<char>(0xE0 | (cp >> 12)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else {
        out.push_back(static_cast<char>(0xF0 | (cp >> 18)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    }
}

// Length of a valid UTF-8 sequence starting at text[pos], or 0 when it is invalid (overlong, surrogate, > U+10FFFF).
std::size_t utf8Length(std::string_view text, std::size_t pos) {
    const auto b0 = static_cast<unsigned char>(text[pos]);
    auto cont = [&](std::size_t i) {
        return i < text.size() && (static_cast<unsigned char>(text[i]) & 0xC0) == 0x80;
    };
    if (b0 >= 0xC2 && b0 <= 0xDF) return cont(pos + 1) ? 2 : 0;
    if (b0 >= 0xE0 && b0 <= 0xEF) {
        if (!cont(pos + 1) || !cont(pos + 2)) return 0;
        const auto b1 = static_cast<unsigned char>(text[pos + 1]);
        if (b0 == 0xE0 && b1 < 0xA0) return 0;
        if (b0 == 0xED && b1 > 0x9F) return 0;
        return 3;
    }
    if (b0 >= 0xF0 && b0 <= 0xF4) {
        if (!cont(pos + 1) || !cont(pos + 2) || !cont(pos + 3)) return 0;
        const auto b1 = static_cast<unsigned char>(text[pos + 1]);
        if (b0 == 0xF0 && b1 < 0x90) return 0;
        if (b0 == 0xF4 && b1 > 0x8F) return 0;
        return 4;
    }
    return 0;
}

bool literalHasFractionOrExponent(std::string_view raw) {
    return raw.find_first_of(".eE") != std::string_view::npos;
}

// Parses an integer literal; false when it does not fit 64 bits.
bool parseInt64(std::string_view raw, std::int64_t& out) {
    const auto* first = raw.data();
    const auto* last = raw.data() + raw.size();
    auto [ptr, ec] = std::from_chars(first, last, out);
    return ec == std::errc() && ptr == last;
}

}  // namespace

void Document::skipSpace(std::size_t& pos) const {
    while (pos < text_.size()) {
        const char c = text_[pos];
        if (c == ' ' || c == '\t' || c == '\n' || c == '\r') {
            ++pos;
        } else {
            break;
        }
    }
}

bool Document::parse(std::string_view text) {
    text_ = text;
    nodes_.clear();
    if (text.size() >= std::numeric_limits<std::uint32_t>::max()) return false;
    std::size_t pos = 0;
    skipSpace(pos);
    if (pos >= text_.size() || !parseValue(pos, 0)) {
        nodes_.clear();
        return false;
    }
    skipSpace(pos);
    if (pos != text_.size()) {
        nodes_.clear();
        return false;
    }
    return true;
}

bool Document::parseLiteral(std::size_t& pos, std::string_view word, Type type) {
    if (text_.substr(pos, word.size()) != word) return false;
    Node node{type};
    node.begin = static_cast<std::uint32_t>(pos);
    pos += word.size();
    node.end = static_cast<std::uint32_t>(pos);
    nodes_.push_back(node);
    return true;
}

bool Document::parseString(std::size_t& pos, bool& escaped) {
    // text_[pos] is the opening quote.
    const std::size_t start = ++pos;
    escaped = false;
    while (pos < text_.size()) {
        const auto c = static_cast<unsigned char>(text_[pos]);
        if (c == '"') {
            Node node{Type::String};
            node.escaped = escaped;
            node.begin = static_cast<std::uint32_t>(start);
            node.end = static_cast<std::uint32_t>(pos);
            nodes_.push_back(node);
            ++pos;
            return true;
        }
        if (c == '\\') {
            escaped = true;
            if (pos + 1 >= text_.size()) return false;
            const char e = text_[pos + 1];
            if (e == 'u') {
                if (pos + 5 >= text_.size()) return false;
                for (std::size_t i = pos + 2; i < pos + 6; ++i) {
                    if (!isHex(text_[i])) return false;
                }
                pos += 6;
            } else if (e == '"' || e == '\\' || e == '/' || e == 'b' || e == 'f' || e == 'n' || e == 'r' ||
                       e == 't') {
                pos += 2;
            } else {
                return false;
            }
            continue;
        }
        if (c < 0x20) return false;
        if (c >= 0x80) {
            const std::size_t length = utf8Length(text_, pos);
            if (length == 0) return false;
            pos += length;
            continue;
        }
        ++pos;
    }
    return false;
}

bool Document::parseNumber(std::size_t& pos) {
    const std::size_t start = pos;
    auto digit = [&](std::size_t i) { return i < text_.size() && text_[i] >= '0' && text_[i] <= '9'; };
    if (text_[pos] == '-') ++pos;
    if (!digit(pos)) return false;
    if (text_[pos] == '0') {
        ++pos;
    } else {
        while (digit(pos)) ++pos;
    }
    if (pos < text_.size() && text_[pos] == '.') {
        ++pos;
        if (!digit(pos)) return false;
        while (digit(pos)) ++pos;
    }
    if (pos < text_.size() && (text_[pos] == 'e' || text_[pos] == 'E')) {
        ++pos;
        if (pos < text_.size() && (text_[pos] == '+' || text_[pos] == '-')) ++pos;
        if (!digit(pos)) return false;
        while (digit(pos)) ++pos;
    }
    Node node{Type::Number};
    node.begin = static_cast<std::uint32_t>(start);
    node.end = static_cast<std::uint32_t>(pos);
    nodes_.push_back(node);
    return true;
}

bool Document::parseValue(std::size_t& pos, std::size_t depth) {
    if (pos >= text_.size()) return false;
    const char c = text_[pos];
    if (c == '{' || c == '[') {
        if (depth >= kMaxDepth) return false;
        const bool object = c == '{';
        const auto index = static_cast<std::uint32_t>(nodes_.size());
        Node container{object ? Type::Object : Type::Array};
        container.begin = static_cast<std::uint32_t>(pos);
        nodes_.push_back(container);
        ++pos;
        skipSpace(pos);
        std::uint32_t count = 0;
        const char close = object ? '}' : ']';
        if (pos < text_.size() && text_[pos] == close) {
            ++pos;
        } else {
            while (true) {
                if (object) {
                    if (pos >= text_.size() || text_[pos] != '"') return false;
                    bool escaped = false;
                    if (!parseString(pos, escaped)) return false;
                    skipSpace(pos);
                    if (pos >= text_.size() || text_[pos] != ':') return false;
                    ++pos;
                    skipSpace(pos);
                }
                if (!parseValue(pos, depth + 1)) return false;
                ++count;
                skipSpace(pos);
                if (pos >= text_.size()) return false;
                if (text_[pos] == ',') {
                    ++pos;
                    skipSpace(pos);
                    continue;
                }
                if (text_[pos] == close) {
                    ++pos;
                    break;
                }
                return false;
            }
        }
        Node& node = nodes_[index];
        node.end = static_cast<std::uint32_t>(pos);
        node.span = static_cast<std::uint32_t>(nodes_.size() - index);
        node.count = count;
        return true;
    }
    if (c == '"') {
        bool escaped = false;
        return parseString(pos, escaped);
    }
    if (c == 't') return parseLiteral(pos, "true", Type::True);
    if (c == 'f') return parseLiteral(pos, "false", Type::False);
    if (c == 'n') return parseLiteral(pos, "null", Type::Null);
    if (c == '-' || (c >= '0' && c <= '9')) return parseNumber(pos);
    return false;
}

const Node& Value::node() const { return doc_->node(index_); }

Type Value::type() const { return node().type; }

Value Value::operator[](std::string_view key) const {
    if (!isObject()) return {};
    const Node& object = node();
    std::uint32_t i = index_ + 1;
    Value found;
    for (std::uint32_t m = 0; m < object.count; ++m) {
        const Node& keyNode = doc_->node(i);
        const std::uint32_t valueIndex = i + 1;
        const auto rawKey = doc_->text().substr(keyNode.begin, keyNode.end - keyNode.begin);
        const bool match = keyNode.escaped ? decodeString(rawKey) == key : rawKey == key;
        if (match) found = Value(doc_, valueIndex);
        i = valueIndex + doc_->node(valueIndex).span;
    }
    return found;
}

std::uint32_t Value::size() const {
    if (!valid()) return 0;
    const Type t = type();
    return (t == Type::Array || t == Type::Object) ? node().count : 0;
}

Value Value::at(std::uint32_t position) const {
    if (!isArray() || position >= node().count) return {};
    std::uint32_t i = index_ + 1;
    for (std::uint32_t e = 0; e < position; ++e) i += doc_->node(i).span;
    return Value(doc_, i);
}

std::vector<Value> Value::elements() const {
    std::vector<Value> out;
    if (!isArray()) return out;
    out.reserve(node().count);
    std::uint32_t i = index_ + 1;
    for (std::uint32_t e = 0; e < node().count; ++e) {
        out.emplace_back(doc_, i);
        i += doc_->node(i).span;
    }
    return out;
}

std::vector<std::pair<std::string, Value>> Value::members() const {
    std::vector<std::pair<std::string, Value>> out;
    if (!isObject()) return out;
    std::uint32_t i = index_ + 1;
    for (std::uint32_t m = 0; m < node().count; ++m) {
        const Node& keyNode = doc_->node(i);
        const auto rawKey = doc_->text().substr(keyNode.begin, keyNode.end - keyNode.begin);
        out.emplace_back(keyNode.escaped ? decodeString(rawKey) : std::string(rawKey), Value(doc_, i + 1));
        i = i + 1 + doc_->node(i + 1).span;
    }
    return out;
}

std::string_view Value::raw() const {
    if (!valid()) return {};
    const Node& n = node();
    return doc_->text().substr(n.begin, n.end - n.begin);
}

std::optional<std::string> Value::string() const {
    if (!isString()) return std::nullopt;
    return node().escaped ? decodeString(raw()) : std::string(raw());
}

std::optional<std::string> Value::nonEmptyString() const {
    auto s = string();
    if (!s || s->empty()) return std::nullopt;
    return s;
}

bool Value::equals(std::string_view text) const {
    if (!isString()) return false;
    return node().escaped ? decodeString(raw()) == text : raw() == text;
}

bool Value::startsWith(std::string_view prefix) const {
    if (!isString()) return false;
    if (!node().escaped) return raw().substr(0, prefix.size()) == prefix;
    const auto decoded = decodeString(raw());
    return std::string_view(decoded).substr(0, prefix.size()) == prefix;
}

bool Value::isIntegerLiteral() const { return isNumber() && !literalHasFractionOrExponent(raw()); }

std::optional<double> Value::number() const {
    if (!isNumber()) return std::nullopt;
    const auto text = raw();
    double value = 0;
    auto [ptr, ec] = std::from_chars(text.data(), text.data() + text.size(), value);
    if (ec == std::errc()) return value;
    if (ec == std::errc::result_out_of_range) {
        // Overflow when the magnitude is large (no exponent, or a positive one); underflow otherwise.
        const bool negative = text.front() == '-';
        const auto e = text.find_first_of("eE");
        bool large = e == std::string_view::npos;
        if (!large) large = text[e + 1] != '-';
        if (large) return negative ? -std::numeric_limits<double>::infinity() : std::numeric_limits<double>::infinity();
        return negative ? -0.0 : 0.0;
    }
    return std::nullopt;
}

std::optional<double> Value::finiteNumber() const {
    auto value = number();
    if (!value || !std::isfinite(*value)) return std::nullopt;
    return value;
}

std::optional<std::int64_t> Value::truncated() const {
    if (!isNumber()) return std::nullopt;
    if (isIntegerLiteral()) {
        std::int64_t exact = 0;
        if (parseInt64(raw(), exact)) return exact;
    }
    const auto value = finiteNumber();
    if (!value) return std::nullopt;
    const double t = std::trunc(*value);
    if (t >= 9.223372036854775807e18) return std::numeric_limits<std::int64_t>::max();
    if (t <= -9.223372036854775808e18) return std::numeric_limits<std::int64_t>::min();
    return static_cast<std::int64_t>(t);
}

std::optional<std::int64_t> Value::ordinal() const {
    if (!isIntegerLiteral()) return std::nullopt;
    std::int64_t value = 0;
    if (!parseInt64(raw(), value) || value < 0) return std::nullopt;
    return value;
}

std::int64_t Value::count() const {
    if (!isNumber()) return 0;
    if (isIntegerLiteral()) {
        std::int64_t exact = 0;
        if (parseInt64(raw(), exact)) return exact < 0 ? 0 : std::min(exact, kTokenCap);
        return raw().front() == '-' ? 0 : kTokenCap;
    }
    const auto value = number();
    if (!value) return 0;
    if (!std::isfinite(*value)) return *value < 0 ? 0 : kTokenCap;
    if (*value <= 0) return 0;
    if (*value >= static_cast<double>(kTokenCap)) return kTokenCap;
    return static_cast<std::int64_t>(*value);
}

std::string decodeString(std::string_view raw) {
    std::string out;
    out.reserve(raw.size());
    for (std::size_t i = 0; i < raw.size();) {
        const char c = raw[i];
        if (c != '\\') {
            out.push_back(c);
            ++i;
            continue;
        }
        const char e = raw[i + 1];
        switch (e) {
            case '"': out.push_back('"'); i += 2; continue;
            case '\\': out.push_back('\\'); i += 2; continue;
            case '/': out.push_back('/'); i += 2; continue;
            case 'b': out.push_back('\b'); i += 2; continue;
            case 'f': out.push_back('\f'); i += 2; continue;
            case 'n': out.push_back('\n'); i += 2; continue;
            case 'r': out.push_back('\r'); i += 2; continue;
            case 't': out.push_back('\t'); i += 2; continue;
            default: break;
        }
        std::uint32_t cp = 0;
        for (std::size_t k = i + 2; k < i + 6; ++k) cp = (cp << 4) | hexValue(raw[k]);
        i += 6;
        if (cp >= 0xD800 && cp <= 0xDBFF) {
            if (i + 6 <= raw.size() && raw[i] == '\\' && raw[i + 1] == 'u') {
                std::uint32_t low = 0;
                for (std::size_t k = i + 2; k < i + 6; ++k) low = (low << 4) | hexValue(raw[k]);
                if (low >= 0xDC00 && low <= 0xDFFF) {
                    appendUtf8(out, 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00));
                    i += 6;
                    continue;
                }
            }
            appendUtf8(out, 0xFFFD);
            continue;
        }
        if (cp >= 0xDC00 && cp <= 0xDFFF) {
            appendUtf8(out, 0xFFFD);
            continue;
        }
        appendUtf8(out, cp);
    }
    return out;
}

bool isBlank(std::string_view line) {
    for (const char c : line) {
        if (c != ' ' && c != '\t' && c != '\r' && c != '\n' && c != '\v' && c != '\f') return false;
    }
    return true;
}

}  // namespace agentspeed::json
