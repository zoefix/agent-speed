#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/json.hpp"
#include "agentspeed/model.hpp"
#include "agentspeed/modes.hpp"
#include "agentspeed/remote_protocol.hpp"

// Typed, validated access to one JSON object of a relay line (REMOTE 5.6). Every accessor throws LineError for a
// missing required member, a wrong type or a violated bound.
namespace agentspeed::remote::detail {

inline constexpr std::size_t kMaxLineDepth = 32;

enum class TextRule : std::uint8_t {
    // Ids, keys, projects, paths and tool names: no U+0000–U+001F or U+007F.
    Identifier,
    // Models, providers and other displayed text: also no C1 controls, line and paragraph separators or bidi controls.
    Model,
};

bool allows(TextRule rule, std::string_view text);
[[noreturn]] void fail(LineError::Kind kind, std::string field);

std::string checkedString(const json::Value& value, const std::string& name, std::size_t maxBytes, TextRule rule);
std::int64_t checkedInteger(const json::Value& value, const std::string& name);

class Fields {
public:
    Fields(json::Value object, std::string path) : object_(object), path_(std::move(path)) {}
    // The object member `key`, which must be an object.
    Fields object(std::string_view key) const;
    // An element or member value that must be an object.
    static Fields of(const json::Value& value, const std::string& path);

    std::string string(std::string_view key, std::size_t maxBytes, TextRule rule = TextRule::Identifier) const;
    std::optional<std::string> optionalString(std::string_view key, std::size_t maxBytes, TextRule rule = TextRule::Identifier) const;
    // A model or provider: null, absent or empty is nullopt.
    std::optional<std::string> modelString(std::string_view key, std::size_t maxBytes) const;
    SourceKind source(std::string_view key) const;
    std::string day(std::string_view key) const;
    std::string hex(std::string_view key, std::size_t count) const;
    bool boolean(std::string_view key) const;
    std::optional<bool> optionalBoolean(std::string_view key) const;
    std::int64_t integer(std::string_view key) const;
    std::optional<std::int64_t> optionalInteger(std::string_view key) const;
    std::int64_t count(std::string_view key, std::int64_t limit = kIntegerLimit) const;
    std::int64_t time(std::string_view key) const;
    std::optional<std::int64_t> optionalTime(std::string_view key) const;
    // A token value (METRICS 3.2): any number, truncated and clamped to 0 … 10^12.
    std::int64_t tokens(std::string_view key) const;
    double number(std::string_view key) const;
    // 0–1; finite values outside are clamped.
    double fraction(std::string_view key) const;
    json::Value members(std::string_view key) const;
    std::vector<json::Value> array(std::string_view key, std::size_t limit) const;
    ExecutionModes modes(std::string_view key) const;

    std::string name(std::string_view key) const { return path_ + std::string(key); }
    const json::Value& value() const { return object_; }

private:
    json::Value required(std::string_view key) const;
    // Absent and null are both nullopt.
    std::optional<json::Value> optional(std::string_view key) const;

    json::Value object_;
    std::string path_;
};

// METRICS 6.10 Storage: `{tool, digest, callMs, resultMs, failed, model, seeded}`.
work::Action action(const Fields& f);
// The day aggregate encoding of the Linux core (`history_store.cpp`).
history::DayAggregate aggregate(const Fields& f);
// METRICS 6.11 Records.
perf::Record perfRecord(const Fields& f);

}  // namespace agentspeed::remote::detail
