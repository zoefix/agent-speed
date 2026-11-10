#include "agentspeed/quota/vendors/values.hpp"

#include <charconv>
#include <cmath>
#include <limits>

#include "agentspeed/rfc3339.hpp"

namespace agentspeed::quota::vendors {

namespace {

constexpr std::string_view kSpace = " \t\r\n\f\v";

std::string_view trimmed(std::string_view text) {
    const auto first = text.find_first_not_of(kSpace);
    if (first == std::string_view::npos) return {};
    const auto last = text.find_last_not_of(kSpace);
    return text.substr(first, last - first + 1);
}

bool allDigits(std::string_view text) {
    if (text.empty()) return false;
    for (const char c : text) {
        if (c < '0' || c > '9') return false;
    }
    return true;
}

bool hasZone(std::string_view tail) { return tail.find_first_of("Zz+-") != std::string_view::npos; }

}  // namespace

std::optional<double> strictNumber(const json::Value& value) {
    if (!value.isNumber()) return std::nullopt;
    return value.finiteNumber();
}

std::optional<double> numberText(std::string_view text) {
    auto t = trimmed(text);
    if (!t.empty() && t.front() == '+') t.remove_prefix(1);
    if (t.empty() || t.front() == '+' || t.find_first_of("xX") != std::string_view::npos) return std::nullopt;
    double parsed = 0;
    const auto [end, ec] = std::from_chars(t.data(), t.data() + t.size(), parsed);
    if (ec != std::errc() || end != t.data() + t.size() || !std::isfinite(parsed)) return std::nullopt;
    return parsed;
}

std::optional<double> number(const json::Value& value) {
    if (auto n = strictNumber(value)) return n;
    if (!value.isString()) return std::nullopt;
    const auto s = value.string();
    return s ? numberText(*s) : std::nullopt;
}

std::int64_t truncated(double value) {
    if (std::isnan(value)) return 0;
    constexpr double kMax = 9.2233720368547748e18;
    if (value >= kMax) return std::numeric_limits<std::int64_t>::max();
    if (value <= -kMax) return std::numeric_limits<std::int64_t>::min();
    return static_cast<std::int64_t>(value);
}

std::int64_t epochMs(double value) { return truncated(value > 1e11 ? value : value * 1000); }

std::optional<std::int64_t> isoMs(std::string_view text) {
    if (auto parsed = parseTimestamp(text)) return parsed;
    if (text.size() == 10) return parseTimestamp(std::string(text) + "T00:00:00Z");
    if (text.size() < 16 || text.size() > 40) return std::nullopt;
    if (text.size() == 16 || (text[16] != ':' && text[16] != '.')) {
        // Hours and minutes only (`2026-11-10T22:00`, `…T22:00+08:00`).
        std::string full(text.substr(0, 16));
        full += ":00";
        full += text.size() == 16 ? std::string_view("Z") : text.substr(16);
        return parseTimestamp(full);
    }
    if (text.size() > 19 && hasZone(text.substr(19))) return std::nullopt;
    return parseTimestamp(std::string(text) + "Z");
}

std::optional<std::int64_t> timeMs(const json::Value& value) {
    if (auto n = strictNumber(value)) return epochMs(*n);
    if (!value.isString()) return std::nullopt;
    const auto s = value.string();
    if (!s) return std::nullopt;
    const auto t = trimmed(*s);
    if (t.empty()) return std::nullopt;
    if (allDigits(t)) {
        std::int64_t integer = 0;
        const auto [end, ec] = std::from_chars(t.data(), t.data() + t.size(), integer);
        if (ec == std::errc() && end == t.data() + t.size()) {
            if (integer > 100'000'000'000) return integer;
            if (integer <= std::numeric_limits<std::int64_t>::max() / 1000) return integer * 1000;
        }
        return std::nullopt;
    }
    return isoMs(t);
}

std::string text(const json::Value& value) {
    if (value.isString()) return value.string().value_or("");
    if (value.isNumber()) {
        const auto n = value.number();
        return n && *n != 0 ? std::string(value.raw()) : std::string();
    }
    if (value.isTrue()) return "True";
    return {};
}

bool truthy(const json::Value& value) {
    if (!value.valid()) return false;
    switch (value.type()) {
        case json::Type::Null:
        case json::Type::False: return false;
        case json::Type::True: return true;
        case json::Type::Number: return value.number().value_or(0) != 0;
        case json::Type::String: return !value.raw().empty();
        case json::Type::Array:
        case json::Type::Object: return value.size() > 0;
    }
    return false;
}

Kind kindForMinutes(std::int64_t minutes) {
    if (minutes >= 290 && minutes <= 310) return Kind::FiveHour;
    if (minutes >= 10000 && minutes <= 10160) return Kind::SevenDay;
    return Kind::Other;
}

json::Value pick(const json::Value& object, std::initializer_list<std::string_view> keys) {
    if (!object.isObject()) return {};
    for (const auto key : keys) {
        const auto member = object[key];
        if (member.valid() && !member.isNull()) return member;
    }
    return {};
}

}  // namespace agentspeed::quota::vendors
