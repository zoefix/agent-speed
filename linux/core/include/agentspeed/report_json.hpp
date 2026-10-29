#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

// A JSON document as the report builder writes it: members in insertion order, decimals with a fixed number of places
// (trailing zeros trimmed), strings with only the required escapes (Python's ensure_ascii=False).
namespace agentspeed::report {

class Json {
public:
    enum class Kind { Null, Bool, Int, Decimal, String, Array, Object };
    using Member = std::pair<std::string, Json>;

    Json() = default;
    static Json null() { return {}; }
    static Json of(bool value);
    static Json of(std::int64_t value);
    static Json of(int value) { return of(static_cast<std::int64_t>(value)); }
    static Json of(std::string value);
    static Json of(const char* value) { return of(std::string(value)); }
    static Json of(std::string_view value) { return of(std::string(value)); }
    static Json decimal(double value, int places);
    static Json array(std::vector<Json> items);
    static Json object(std::vector<Member> members);

    Kind kind() const { return kind_; }
    const std::vector<Json>& items() const { return items_; }
    const std::vector<Member>& members() const { return members_; }
    const Json* find(std::string_view key) const;
    void add(std::string key, Json value) { members_.emplace_back(std::move(key), std::move(value)); }
    void push(Json value) { items_.push_back(std::move(value)); }

    std::string compact() const;
    static std::string quoted(std::string_view text);
    // Half away from zero to `places` (0 for a non-finite value).
    static double round(double value, int places);
    static std::string number(double value, int places);

private:
    void write(std::string& out) const;

    Kind kind_ = Kind::Null;
    bool bool_ = false;
    std::int64_t int_ = 0;
    double decimal_ = 0;
    int places_ = 0;
    std::string string_;
    std::vector<Json> items_;
    std::vector<Member> members_;
};

}  // namespace agentspeed::report
