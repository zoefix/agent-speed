#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/json.hpp"

// DESIGN 9 money: exact decimals, the bundled currency table, formatting, and NETWORK 4 exchange rates.
namespace agentspeed::money {

// A non-negative decimal: mantissa / 10^scale, at most 28 decimals (like .NET decimal).
class Decimal {
public:
    __extension__ using Int = __int128;
    static constexpr int kMaxScale = 28;

    Decimal() = default;
    Decimal(std::int64_t whole) : mantissa_(whole) {}
    static Decimal of(Int mantissa, int scale);
    // [-]digits[.digits][(e|E)[+-]digits]
    static std::optional<Decimal> parse(std::string_view text);
    // A JSON number the way Python's Decimal reads it: an integer literal exactly, anything else as its nearest double's
    // shortest text.
    static std::optional<Decimal> ofJson(const json::Value& value);
    static Decimal ofDouble(double value);

    Decimal operator*(const Decimal& other) const;
    Decimal operator/(const Decimal& other) const;
    Decimal operator-(const Decimal& other) const;
    Decimal operator+(const Decimal& other) const;
    Decimal half() const;
    std::strong_ordering operator<=>(const Decimal& other) const;
    bool operator==(const Decimal& other) const { return (*this <=> other) == 0; }
    bool isZero() const { return mantissa_ == 0; }
    bool negative() const { return mantissa_ < 0; }
    Decimal abs() const { return negative() ? of(-mantissa_, scale_) : *this; }

    // Half away from zero to `places` decimals.
    Decimal round(int places) const;
    Decimal floor() const;
    // Floor of log10 of a positive value.
    int adjusted() const;
    // Exactly `places` decimals (the value is rounded first).
    std::string fixed(int places) const;
    std::string text() const;
    double toDouble() const;
    std::int64_t toInt64() const;

private:
    Int mantissa_ = 0;
    int scale_ = 0;
};

struct Currency {
    std::string code;
    int digits = 2;
    std::string symbol;
    std::map<std::string, std::string> symbols;  // per UI language
    std::optional<int> common;
    const std::string& symbolFor(const std::string& language) const;
};

struct FallbackRates {
    std::string date, source;
    std::map<std::string, Decimal> rates;
};

class CurrencyTable {
public:
    static const CurrencyTable& bundled();
    static std::optional<CurrencyTable> parse(std::string_view utf8);

    const std::vector<Currency>& currencies() const { return currencies_; }
    const FallbackRates& fallbackRates() const { return fallback_; }
    const Currency* find(const std::string& code) const;
    const Currency& currencyOrUsd(const std::string& code) const;
    std::set<std::string> codes() const;
    // The picker's common group: USD, the UI language's home currencies, then the remaining `common` ranks.
    std::vector<const Currency*> commonGroup(const std::string& language) const;

private:
    std::vector<Currency> currencies_;
    FallbackRates fallback_;
};

enum class Form { Rate, Amount };

struct Formatted {
    std::string number, text, symbol;
};

// costMicroUsd / 1 000 000 × rate.
Decimal amount(std::int64_t microUsd, const Decimal& rate);
// A live micro-USD-per-second value, entering as the decimal of its shortest round-trip text.
Decimal ratePerSecond(double microUsdPerSecond, const Decimal& rate);
Formatted format(const Decimal& value, const Currency& currency, const std::string& language, Form form);
std::string withSymbol(const std::string& symbol, const std::string& number);
std::string number(const Decimal& value, int digits, Form form);
// At least `digits` significant digits, integers grouped (the rate in the status line).
std::string significant(const Decimal& value, int digits = 4);
std::string csvUsd(std::int64_t microUsd);
std::string csv(std::int64_t microUsd, const Decimal& rate, int digits);

// NETWORK 4: provider responses and the acceptance checks.
namespace rates {

enum class Shape { ErApi, Fawaz, Frankfurter };
std::optional<Shape> shapeOf(std::string_view id);
std::string idOf(Shape shape);

struct Parsed {
    std::int64_t dateMs = 0;
    std::map<std::string, Decimal> rates;
};
// nullopt with `reason` set when the body is not the provider's shape.
std::optional<Parsed> parse(Shape shape, std::string_view body, const std::set<std::string>& supported, std::string* reason = nullptr);
std::optional<Parsed> parse(Shape shape, const json::Value& body, const std::set<std::string>& supported, std::string* reason = nullptr);

struct Reference {
    std::int64_t dateMs = 0, acceptedMs = 0;
    std::map<std::string, Decimal> rates;
};

enum class Verdict { Accepted, NeedsSecondProvider, Rejected };
Verdict check(std::int64_t dateMs, const std::map<std::string, Decimal>& rates, const std::string& selected, std::int64_t nowMs,
              const std::optional<Reference>& last, const std::map<std::string, Decimal>* fallback,
              const std::map<std::string, Decimal>* other = nullptr, std::string* reason = nullptr);

}  // namespace rates

std::string_view bundledCurrenciesJson();

}  // namespace agentspeed::money
