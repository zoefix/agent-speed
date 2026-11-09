#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/character/error.hpp"
#include "agentspeed/json.hpp"

// Small readers over the core JSON DOM for the character documents: `need*` throw CharacterError(Unreadable) when the
// key is missing or of the wrong type (as Swift's JSONDecoder does), the others return nullopt.
namespace agentspeed::character::jr {

inline std::optional<double> number(const json::Value& v) {
    if (!v.isNumber()) return std::nullopt;
    return v.finiteNumber();
}

inline std::optional<std::int64_t> integer(const json::Value& v) {
    auto d = number(v);
    if (!d || *d != static_cast<double>(static_cast<std::int64_t>(*d))) return std::nullopt;
    return static_cast<std::int64_t>(*d);
}

inline std::optional<std::vector<double>> numbers(const json::Value& v) {
    if (!v.isArray()) return std::nullopt;
    std::vector<double> out;
    out.reserve(v.size());
    for (const auto& e : v.elements()) {
        auto d = number(e);
        if (!d) return std::nullopt;
        out.push_back(*d);
    }
    return out;
}

inline std::optional<std::vector<std::int64_t>> integers(const json::Value& v) {
    if (!v.isArray()) return std::nullopt;
    std::vector<std::int64_t> out;
    out.reserve(v.size());
    for (const auto& e : v.elements()) {
        auto d = integer(e);
        if (!d) return std::nullopt;
        out.push_back(*d);
    }
    return out;
}

inline std::optional<std::vector<std::string>> strings(const json::Value& v) {
    if (!v.isArray()) return std::nullopt;
    std::vector<std::string> out;
    for (const auto& e : v.elements()) {
        auto s = e.string();
        if (!s) return std::nullopt;
        out.push_back(*s);
    }
    return out;
}

[[noreturn]] inline void fail(std::string_view key) {
    throw CharacterError(CharacterError::Kind::Unreadable, std::string(key));
}

inline std::string needString(const json::Value& o, std::string_view key) {
    auto s = o[key].string();
    if (!s) fail(key);
    return *s;
}

inline double needNumber(const json::Value& o, std::string_view key) {
    auto d = number(o[key]);
    if (!d) fail(key);
    return *d;
}

inline std::int64_t needInteger(const json::Value& o, std::string_view key) {
    auto d = integer(o[key]);
    if (!d) fail(key);
    return *d;
}

inline std::vector<double> needNumbers(const json::Value& o, std::string_view key) {
    auto d = numbers(o[key]);
    if (!d) fail(key);
    return *d;
}

inline std::vector<std::int64_t> needIntegers(const json::Value& o, std::string_view key) {
    auto d = integers(o[key]);
    if (!d) fail(key);
    return *d;
}

// An optional key: absent or null gives nullopt, a value of the wrong type fails.
inline std::optional<double> optNumber(const json::Value& o, std::string_view key) {
    auto v = o[key];
    if (!v || v.isNull()) return std::nullopt;
    auto d = number(v);
    if (!d) fail(key);
    return d;
}

inline std::optional<std::string> optString(const json::Value& o, std::string_view key) {
    auto v = o[key];
    if (!v || v.isNull()) return std::nullopt;
    auto s = v.string();
    if (!s) fail(key);
    return s;
}

inline std::optional<std::vector<double>> optNumbers(const json::Value& o, std::string_view key) {
    auto v = o[key];
    if (!v || v.isNull()) return std::nullopt;
    auto d = numbers(v);
    if (!d) fail(key);
    return d;
}

}  // namespace agentspeed::character::jr
