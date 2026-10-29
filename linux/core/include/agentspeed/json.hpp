#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace agentspeed::json {

enum class Type : std::uint8_t { Null, False, True, Number, String, Array, Object };

// One parsed token. Containers store the size of their subtree so siblings can be skipped.
struct Node {
    Type type;
    bool escaped = false;  // strings only: the raw text contains a backslash
    std::uint32_t begin = 0;  // byte offset of the raw token (strings: inside the quotes)
    std::uint32_t end = 0;
    std::uint32_t span = 1;  // nodes in this subtree, itself included
    std::uint32_t count = 0;  // containers: elements, or members for objects
};

class Document;

// A cheap handle to a node; an invalid handle stands for a missing value.
class Value {
public:
    Value() = default;
    Value(const Document* doc, std::uint32_t index) : doc_(doc), index_(index) {}

    bool valid() const { return doc_ != nullptr; }
    explicit operator bool() const { return valid(); }
    Type type() const;
    bool isNull() const { return valid() && type() == Type::Null; }
    bool isString() const { return valid() && type() == Type::String; }
    bool isNumber() const { return valid() && type() == Type::Number; }
    bool isObject() const { return valid() && type() == Type::Object; }
    bool isArray() const { return valid() && type() == Type::Array; }
    bool isTrue() const { return valid() && type() == Type::True; }
    bool isBool() const { return valid() && (type() == Type::True || type() == Type::False); }

    // Object member by key; duplicate keys resolve to the last one, as Python's json does.
    Value operator[](std::string_view key) const;
    // Array element by position.
    Value at(std::uint32_t position) const;
    std::uint32_t size() const;

    std::vector<Value> elements() const;
    std::vector<std::pair<std::string, Value>> members() const;

    // The raw token text: strings without quotes and undecoded, numbers as written.
    std::string_view raw() const;
    // Decoded string value; nullopt for any other type.
    std::optional<std::string> string() const;
    // A string that is present and not empty, else nullopt.
    std::optional<std::string> nonEmptyString() const;
    // Compares a string value with ASCII or UTF-8 text without allocating when it has no escapes.
    bool equals(std::string_view text) const;
    bool startsWith(std::string_view prefix) const;

    // Number helpers (METRICS 3.6). All return nullopt for non-numbers unless stated.
    bool isIntegerLiteral() const;
    // The nearest double; infinite for literals beyond the double range.
    std::optional<double> number() const;
    std::optional<double> finiteNumber() const;
    // Truncated toward zero and saturated to the signed 64-bit range; nullopt when not finite.
    std::optional<std::int64_t> truncated() const;
    // An integer literal 0 … 2^63 − 1 (Codex ordinals, METRICS 3.5).
    std::optional<std::int64_t> ordinal() const;
    // A token counter (METRICS 3.2): any non-number counts 0.
    std::int64_t count() const;

    std::uint32_t index() const { return index_; }

private:
    const Node& node() const;
    const Document* doc_ = nullptr;
    std::uint32_t index_ = 0;
};

class Document {
public:
    // Parses one complete JSON text. Returns false on any error (METRICS 4: the line is malformed).
    bool parse(std::string_view text);
    Value root() const { return nodes_.empty() ? Value() : Value(this, 0); }
    std::string_view text() const { return text_; }
    const Node& node(std::uint32_t index) const { return nodes_[index]; }

    static constexpr std::size_t kMaxDepth = 512;

private:
    bool parseValue(std::size_t& pos, std::size_t depth);
    bool parseString(std::size_t& pos, bool& escaped);
    bool parseNumber(std::size_t& pos);
    bool parseLiteral(std::size_t& pos, std::string_view word, Type type);
    void skipSpace(std::size_t& pos) const;

    std::string_view text_;
    std::vector<Node> nodes_;
};

// Decodes the inside of a JSON string literal (without the quotes). Lone surrogates become U+FFFD.
std::string decodeString(std::string_view raw);

// True when the bytes are only whitespace (space, tab, CR, LF, VT, FF).
bool isBlank(std::string_view line);

}  // namespace agentspeed::json
