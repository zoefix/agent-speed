#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

// A JSON document that keeps its members in order and its numbers as written, so one value of Claude Code's settings
// can change while the rest of the file stays as the user and Claude Code wrote it (QUOTAS 5.2).
namespace agentspeed::quota::ordered_json {

struct Value {
    enum class Type : std::uint8_t { Null, Bool, Number, String, Array, Object };
    Type type = Type::Null;
    bool boolean = false;
    // A number's literal text, or a string's decoded text.
    std::string text;
    std::vector<Value> items;
    std::vector<std::pair<std::string, Value>> members;

    static Value object() { return Value{Type::Object, false, {}, {}, {}}; }
    static Value string(std::string text) { return Value{Type::String, false, std::move(text), {}, {}}; }
    static Value number(std::string literal) { return Value{Type::Number, false, std::move(literal), {}, {}}; }

    bool isObject() const { return type == Type::Object; }
    bool isString() const { return type == Type::String; }
    // A number literal whose value is finite (QUOTAS 5.2 `padding`).
    bool isFiniteNumber() const;
    const Value* find(std::string_view key) const;
    // Replaces a member where it is, or appends it last.
    void set(const std::string& key, Value value);
    void remove(std::string_view key);
    bool operator==(const Value&) const = default;
};

// Strict RFC 8259 over valid UTF-8 without a byte-order mark; a repeated name keeps its first place and its last value
// (as Python's and JavaScript's parsers do).
std::optional<Value> parse(std::string_view text);
// Two-space indentation, `": "` after names, empty containers as `{}` / `[]`, non-ASCII text as is (Claude Code's
// `JSON.stringify(value, null, 2)` and Python's `json.dumps(value, indent=2, ensure_ascii=False)`); no final newline.
std::string write(const Value& value);

}  // namespace agentspeed::quota::ordered_json
