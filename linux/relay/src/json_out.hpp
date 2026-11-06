#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// Compact JSON for the relay's lines (REMOTE 5.1): members in the order written, strings as valid UTF-8 (every invalid
// byte becomes U+FFFD) with control characters and DEL escaped.
namespace agentspeed::relay {

// The text with every byte that is not part of a valid UTF-8 sequence replaced by U+FFFD.
std::string sanitizeUtf8(std::string_view text);
bool hasControl(std::string_view text);

class JsonOut {
public:
    JsonOut& beginObject();
    JsonOut& endObject();
    JsonOut& beginArray();
    JsonOut& endArray();
    JsonOut& key(std::string_view name);

    JsonOut& string(std::string_view text);
    JsonOut& integer(std::int64_t value);
    JsonOut& boolean(bool value);
    JsonOut& null();
    // The shortest text that reads back as the same double (0 for a non-finite value).
    JsonOut& number(double value);
    // A fraction 0 … 1 with three decimals.
    JsonOut& fraction(double value);
    // JSON text written as it is (the caller's exact encoding).
    JsonOut& raw(std::string_view json);
    // null for nullopt.
    JsonOut& optionalString(const std::optional<std::string>& text);
    JsonOut& optionalInteger(const std::optional<std::int64_t>& value);
    JsonOut& optionalBoolean(const std::optional<bool>& value);

    const std::string& text() const { return out_; }
    std::string take() { return std::move(out_); }

private:
    void beforeValue();

    std::string out_;
    std::vector<std::size_t> counts_;
    bool afterKey_ = false;
};

}  // namespace agentspeed::relay
