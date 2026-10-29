#include "agentspeed/money.hpp"

#include <algorithm>
#include <charconv>
#include <cmath>
#include <stdexcept>

#include "agentspeed/pricing.hpp"
#include "agentspeed/text.hpp"

namespace agentspeed::money {

namespace {

using Int = Decimal::Int;

Int pow10(int n) {
    Int p = 1;
    for (int i = 0; i < n; ++i) p *= 10;
    return p;
}

int digitCount(Int m) {
    if (m < 0) m = -m;
    int n = 1;
    while (m >= 10) {
        m /= 10;
        ++n;
    }
    return n;
}

// Half away from zero division of a mantissa by 10^n.
Int roundDown(Int m, int n) {
    if (n <= 0) return m;
    if (n > 38) return 0;
    const Int divisor = pow10(n);
    Int q = m / divisor;
    const Int r = m % divisor;
    if ((r < 0 ? -r : r) * 2 >= divisor) q += m < 0 ? -1 : 1;
    return q;
}

std::string mantissaText(Int m) {
    if (m == 0) return "0";
    std::string out;
    const bool negative = m < 0;
    if (negative) m = -m;
    while (m > 0) {
        out += static_cast<char>('0' + static_cast<int>(m % 10));
        m /= 10;
    }
    if (negative) out += '-';
    std::reverse(out.begin(), out.end());
    return out;
}

bool isLetter(std::uint32_t cp) {
    if ((cp >= 'A' && cp <= 'Z') || (cp >= 'a' && cp <= 'z')) return true;
    if (cp >= 0xC0 && cp <= 0x24F) return cp != 0xD7 && cp != 0xF7;
    return (cp >= 0x370 && cp <= 0x3FF) || (cp >= 0x400 && cp <= 0x52F) || (cp >= 0x5D0 && cp <= 0x5EA) ||
           (cp >= 0x620 && cp <= 0x64A) || (cp >= 0xE01 && cp <= 0xE30) || (cp >= 0x3041 && cp <= 0x30FF) ||
           (cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0xAC00 && cp <= 0xD7A3);
}

const Decimal kMillion(1'000'000);

std::string trim(const std::string& value, int digits) {
    const auto dot = value.find('.');
    const auto whole = dot == std::string::npos ? value : value.substr(0, dot);
    auto fraction = dot == std::string::npos ? std::string() : value.substr(dot + 1);
    auto length = fraction.size();
    while (length > static_cast<std::size_t>(digits) && fraction[length - 1] == '0') --length;
    fraction.resize(length);
    if (fraction.size() < static_cast<std::size_t>(digits)) fraction.append(static_cast<std::size_t>(digits) - fraction.size(), '0');
    return fraction.empty() ? whole : whole + "." + fraction;
}

std::string group(const std::string& value) {
    const auto dot = value.find('.');
    const auto whole = dot == std::string::npos ? value : value.substr(0, dot);
    const auto rest = dot == std::string::npos ? std::string() : value.substr(dot);
    std::string out;
    const std::size_t lead = whole.size() % 3 == 0 ? 3 : whole.size() % 3;
    out += whole.substr(0, std::min(lead, whole.size()));
    for (std::size_t i = lead; i < whole.size(); i += 3) out += "," + whole.substr(i, 3);
    return out + rest;
}

std::pair<Decimal, int> roundSignificant(const Decimal& value, int digits) {
    const int scale = std::clamp(-std::max(value.adjusted() - digits + 1, -6), 0, Decimal::kMaxScale);
    return {value.round(scale), scale};
}

std::string compact(const Decimal& value) {
    for (const auto& [unit, scale] : {std::pair<const char*, std::int64_t>{"M", 1'000'000}, {"B", 1'000'000'000}}) {
        const auto x = value / Decimal(scale);
        for (const auto& [limit, places] : {std::pair<std::int64_t, int>{10, 2}, {100, 1}, {1000, 0}}) {
            const auto r = x.round(places);
            if (x < Decimal(limit) && r < Decimal(limit)) return r.fixed(places) + unit;
        }
        if (std::string_view(unit) == "B") return group(x.round(0).fixed(0)) + "B";
    }
    return value.fixed(0);
}

}  // namespace

Decimal Decimal::of(Int mantissa, int scale) {
    Decimal d;
    if (scale < 0) {
        d.mantissa_ = mantissa * pow10(-scale);
        d.scale_ = 0;
    } else if (scale > kMaxScale) {
        d.mantissa_ = roundDown(mantissa, scale - kMaxScale);
        d.scale_ = kMaxScale;
    } else {
        d.mantissa_ = mantissa;
        d.scale_ = scale;
    }
    return d;
}

std::optional<Decimal> Decimal::parse(std::string_view text) {
    std::size_t i = 0;
    bool negative = false;
    if (i < text.size() && (text[i] == '-' || text[i] == '+')) negative = text[i++] == '-';
    Int mantissa = 0;
    int scale = 0, digits = 0, significant = 0;
    bool dot = false;
    for (; i < text.size(); ++i) {
        const char c = text[i];
        if (c >= '0' && c <= '9') {
            ++digits;
            if (mantissa != 0 || c != '0') ++significant;
            if (significant > 36) {
                if (!dot) --scale;
                continue;
            }
            mantissa = mantissa * 10 + (c - '0');
            if (dot) ++scale;
        } else if (c == '.' && !dot) {
            dot = true;
        } else {
            break;
        }
    }
    if (digits == 0) return std::nullopt;
    if (i < text.size()) {
        if (text[i] != 'e' && text[i] != 'E') return std::nullopt;
        int power = 0;
        const auto* begin = text.data() + i + 1;
        const auto* end = text.data() + text.size();
        if (begin < end && *begin == '+') ++begin;
        const auto [ptr, ec] = std::from_chars(begin, end, power);
        if (ec != std::errc() || ptr != end || power > 28 + 38 || power < -100) return std::nullopt;
        scale -= power;
    }
    if (scale < -9) return std::nullopt;
    return of(negative ? -mantissa : mantissa, scale);
}

std::optional<Decimal> Decimal::ofJson(const json::Value& value) {
    if (!value.isNumber()) return std::nullopt;
    const auto raw = value.raw();
    if (raw.find_first_of(".eE") == std::string_view::npos) {
        if (raw.size() > 30) return std::nullopt;
        return parse(raw);
    }
    const auto number = value.number();
    if (!number || !std::isfinite(*number)) return std::nullopt;
    return ofDouble(*number);
}

Decimal Decimal::ofDouble(double value) {
    char buffer[64];
    const auto [ptr, ec] = std::to_chars(buffer, buffer + sizeof buffer, value);
    if (ec != std::errc()) return Decimal();
    return parse(std::string_view(buffer, static_cast<std::size_t>(ptr - buffer))).value_or(Decimal());
}

Decimal Decimal::operator*(const Decimal& other) const {
    Int a = mantissa_, b = other.mantissa_;
    int sa = scale_, sb = other.scale_;
    Int product;
    while (__builtin_mul_overflow(a, b, &product)) {
        if (sa >= sb && sa > 0) {
            a = roundDown(a, 1);
            --sa;
        } else if (sb > 0) {
            b = roundDown(b, 1);
            --sb;
        } else {
            throw std::overflow_error("decimal overflow");
        }
    }
    return of(product, sa + sb);
}

Decimal Decimal::operator/(const Decimal& other) const {
    if (other.mantissa_ == 0) throw std::domain_error("division by zero");
    Int numerator = mantissa_;
    int scale = scale_ - other.scale_;
    // Scale the numerator up as far as it fits, then round the last digit.
    while (scale < kMaxScale + 1 && digitCount(numerator) < 37) {
        numerator *= 10;
        ++scale;
    }
    Int q = numerator / other.mantissa_;
    const Int r = numerator % other.mantissa_;
    const Int divisor = other.mantissa_ < 0 ? -other.mantissa_ : other.mantissa_;
    if ((r < 0 ? -r : r) * 2 >= divisor) q += (numerator < 0) != (other.mantissa_ < 0) ? -1 : 1;
    auto result = of(q, scale);
    // Drop trailing zeros so the scale stays meaningful.
    while (result.scale_ > 0 && result.mantissa_ % 10 == 0) {
        result.mantissa_ /= 10;
        --result.scale_;
    }
    return result;
}

Decimal Decimal::operator-(const Decimal& other) const {
    const int scale = std::max(scale_, other.scale_);
    return of(mantissa_ * pow10(scale - scale_) - other.mantissa_ * pow10(scale - other.scale_), scale);
}

Decimal Decimal::operator+(const Decimal& other) const {
    const int scale = std::max(scale_, other.scale_);
    return of(mantissa_ * pow10(scale - scale_) + other.mantissa_ * pow10(scale - other.scale_), scale);
}

Decimal Decimal::half() const { return of(mantissa_ * 5, scale_ + 1); }

std::strong_ordering Decimal::operator<=>(const Decimal& other) const {
    const int scale = std::max(scale_, other.scale_);
    const Int a = mantissa_ * pow10(scale - scale_);
    const Int b = other.mantissa_ * pow10(scale - other.scale_);
    return a < b ? std::strong_ordering::less : a > b ? std::strong_ordering::greater : std::strong_ordering::equal;
}

Decimal Decimal::round(int places) const {
    if (scale_ <= places) return *this;
    return of(roundDown(mantissa_, scale_ - places), places);
}

Decimal Decimal::floor() const {
    if (scale_ == 0) return *this;
    const Int divisor = pow10(scale_);
    Int q = mantissa_ / divisor;
    if (mantissa_ < 0 && mantissa_ % divisor != 0) --q;
    return of(q, 0);
}

int Decimal::adjusted() const { return mantissa_ == 0 ? 0 : digitCount(mantissa_) - 1 - scale_; }

std::string Decimal::fixed(int places) const {
    const auto rounded = round(places);
    Int m = rounded.mantissa_ < 0 ? -rounded.mantissa_ : rounded.mantissa_;
    m *= pow10(places - rounded.scale_);
    auto digits = mantissaText(m);
    if (places == 0) return digits;
    if (digits.size() <= static_cast<std::size_t>(places)) digits.insert(0, static_cast<std::size_t>(places) + 1 - digits.size(), '0');
    digits.insert(digits.size() - static_cast<std::size_t>(places), ".");
    return digits;
}

std::string Decimal::text() const {
    auto out = fixed(scale_);
    return negative() ? "-" + out : out;
}

double Decimal::toDouble() const { return std::strtod(text().c_str(), nullptr); }

std::int64_t Decimal::toInt64() const { return static_cast<std::int64_t>(floor().mantissa_); }

const std::string& Currency::symbolFor(const std::string& language) const {
    const auto it = symbols.find(language);
    return it == symbols.end() ? symbol : it->second;
}

const CurrencyTable& CurrencyTable::bundled() {
    static const CurrencyTable table = [] {
        auto parsed = parse(bundledCurrenciesJson());
        if (!parsed) throw std::runtime_error("The bundled currency table does not parse");
        return *parsed;
    }();
    return table;
}

std::optional<CurrencyTable> CurrencyTable::parse(std::string_view utf8) {
    const std::string text(utf8);
    json::Document document;
    if (!document.parse(text)) return std::nullopt;
    const auto root = document.root();
    const auto schema = root["schema"];
    if (!root.isObject() || !schema.isIntegerLiteral() || schema.raw() != "1" || !root["currencies"].isArray()) return std::nullopt;
    CurrencyTable table;
    for (const auto& item : root["currencies"].elements()) {
        const auto code = item["code"].string();
        const auto digits = item["digits"].isIntegerLiteral() ? item["digits"].truncated() : std::nullopt;
        const auto symbol = item["symbol"].string();
        if (!item.isObject() || !code || !digits || *digits < 0 || *digits > 28 || !symbol) return std::nullopt;
        Currency currency{*code, static_cast<int>(*digits), *symbol, {}, std::nullopt};
        if (item["symbols"].isObject()) {
            for (const auto& [language, value] : item["symbols"].members()) {
                if (auto s = value.string()) currency.symbols[language] = *s;
            }
        }
        if (item["common"].isIntegerLiteral()) {
            if (const auto rank = item["common"].truncated(); rank && *rank >= INT32_MIN && *rank <= INT32_MAX) {
                currency.common = static_cast<int>(*rank);
            }
        }
        table.currencies_.push_back(std::move(currency));
    }
    const auto fallback = root["fallbackRates"];
    const auto date = fallback["date"].string(), source = fallback["source"].string();
    if (!fallback.isObject() || !date || !source || !fallback["rates"].isObject()) return std::nullopt;
    table.fallback_.date = *date;
    table.fallback_.source = *source;
    for (const auto& [code, value] : fallback["rates"].members()) {
        const auto rate = Decimal::ofJson(value);
        if (!rate || *rate <= Decimal()) return std::nullopt;
        table.fallback_.rates[code] = *rate;
    }
    return table;
}

const Currency* CurrencyTable::find(const std::string& code) const {
    const auto it = std::find_if(currencies_.begin(), currencies_.end(), [&](const Currency& c) { return c.code == code; });
    return it == currencies_.end() ? nullptr : &*it;
}

const Currency& CurrencyTable::currencyOrUsd(const std::string& code) const {
    if (const auto* c = find(code)) return *c;
    return *find("USD");
}

std::set<std::string> CurrencyTable::codes() const {
    std::set<std::string> out;
    for (const auto& c : currencies_) out.insert(c.code);
    return out;
}

std::vector<const Currency*> CurrencyTable::commonGroup(const std::string& language) const {
    static const std::map<std::string, std::vector<std::string>> kHome = {
        {"zh-Hant", {"TWD", "HKD", "MOP"}}, {"zh-Hans", {"CNY"}}, {"ja", {"JPY"}}};
    std::vector<std::string> order{"USD"};
    if (const auto home = kHome.find(language); home != kHome.end()) {
        for (const auto& code : home->second) {
            if (std::find(order.begin(), order.end(), code) == order.end() && find(code)) order.push_back(code);
        }
    }
    std::vector<const Currency*> ranked;
    for (const auto& c : currencies_) {
        if (c.common) ranked.push_back(&c);
    }
    std::stable_sort(ranked.begin(), ranked.end(), [](const Currency* a, const Currency* b) { return *a->common < *b->common; });
    for (const auto* c : ranked) {
        if (std::find(order.begin(), order.end(), c->code) == order.end()) order.push_back(c->code);
    }
    std::vector<const Currency*> out;
    for (const auto& code : order) {
        if (const auto* c = find(code)) out.push_back(c);
    }
    return out;
}

Decimal amount(std::int64_t microUsd, const Decimal& rate) {
    try {
        return Decimal(std::max<std::int64_t>(0, microUsd)) * rate / kMillion;
    } catch (const std::exception&) {
        return Decimal::of(static_cast<Int>(INT64_MAX), 0);
    }
}

Decimal ratePerSecond(double microUsdPerSecond, const Decimal& rate) {
    if (!std::isfinite(microUsdPerSecond) || microUsdPerSecond <= 0) return Decimal();
    try {
        return Decimal::ofDouble(microUsdPerSecond) * rate / kMillion;
    } catch (const std::exception&) {
        return Decimal::of(static_cast<Int>(INT64_MAX), 0);
    }
}

std::string withSymbol(const std::string& symbol, const std::string& value) {
    std::uint32_t last = 0;
    for (std::size_t i = 0; i < symbol.size();) last = text::nextCodePoint(symbol, i);
    const std::string separator = !symbol.empty() && isLetter(last) ? "\xC2\xA0" : "";
    if (!value.empty() && value.front() == '<') return "<" + symbol + separator + value.substr(1);
    return symbol + separator + value;
}

std::string number(const Decimal& value, int digits, Form form) {
    const int d = std::clamp(digits, 0, Decimal::kMaxScale);
    const Decimal v = value.negative() ? Decimal() : value;
    if (v.isZero()) return form == Form::Rate || d == 0 ? "0" : "0." + std::string(static_cast<std::size_t>(d), '0');
    if (v < Decimal::of(5, 7)) return "<0.000001";
    if (v < Decimal(1)) {
        const auto [w, scale] = roundSignificant(v, 2);
        if (w < Decimal(1)) return trim(w.fixed(scale), d);
    }
    if (form == Form::Rate && v < Decimal(100)) {
        const auto [w, scale] = roundSignificant(v, 3);
        if (w < Decimal(100)) return trim(w.fixed(scale), d);
    }
    if (v < kMillion) {
        const int scale = form == Form::Rate ? 0 : d;
        const auto w = v.round(scale);
        if (w < kMillion) return group(w.fixed(scale));
    }
    return compact(v);
}

Formatted format(const Decimal& value, const Currency& currency, const std::string& language, Form form) {
    const auto digits = number(value, currency.digits, form);
    const auto& symbol = currency.symbolFor(language);
    return {digits, form == Form::Amount ? withSymbol(symbol, digits) : digits + " " + symbol + "/s", symbol};
}

std::string significant(const Decimal& value, int digits) {
    if (value <= Decimal()) return "0";
    int scale = std::clamp(digits - 1 - value.adjusted(), 0, Decimal::kMaxScale);
    auto rounded = value.round(scale);
    if (rounded.adjusted() > value.adjusted() && scale > 0) {
        --scale;
        rounded = value.round(scale);
    }
    return group(rounded.fixed(scale));
}

std::string csvUsd(std::int64_t microUsd) { return (Decimal(std::max<std::int64_t>(0, microUsd)) / kMillion).fixed(6); }

std::string csv(std::int64_t microUsd, const Decimal& rate, int digits) {
    const int scale = std::clamp(digits + 2, 0, Decimal::kMaxScale);
    auto value = amount(microUsd, rate);
    if (value.negative()) value = Decimal();
    return value.round(scale).fixed(scale);
}

namespace rates {

std::optional<Shape> shapeOf(std::string_view id) {
    if (id == "open.er-api.com") return Shape::ErApi;
    if (id == "currency-api") return Shape::Fawaz;
    if (id == "frankfurter") return Shape::Frankfurter;
    return std::nullopt;
}

std::string idOf(Shape shape) {
    switch (shape) {
        case Shape::ErApi: return "open.er-api.com";
        case Shape::Fawaz: return "currency-api";
        case Shape::Frankfurter: break;
    }
    return "frankfurter";
}

std::optional<Parsed> parse(Shape shape, std::string_view body, const std::set<std::string>& supported, std::string* reason) {
    const std::string text(body);
    json::Document document;
    if (!document.parse(text)) {
        if (reason) *reason = "not JSON";
        return std::nullopt;
    }
    return parse(shape, document.root(), supported, reason);
}

std::optional<Parsed> parse(Shape shape, const json::Value& root, const std::set<std::string>& supported, std::string* reason) {
    const auto fail = [&](const char* why) {
        if (reason) *reason = why;
        return std::optional<Parsed>();
    };
    if (!root.isObject()) return fail("not an object");
    const auto dateOf = [](const json::Value& v) -> std::optional<std::int64_t> {
        const auto s = v.string();
        if (!s || !pricing::UtcDay::isDateText(*s)) return std::nullopt;
        return pricing::UtcDay::instantMs(*s);
    };
    Parsed parsed;
    std::vector<std::pair<std::string, json::Value>> raw;
    switch (shape) {
        case Shape::ErApi: {
            if (!root["result"].equals("success") || !root["result"].isString() || !root["base_code"].isString() ||
                !root["base_code"].equals("USD")) {
                return fail("not a successful USD response");
            }
            const auto stamp = Decimal::ofJson(root["time_last_update_unix"]);
            if (!stamp || *stamp <= Decimal() || *stamp >= Decimal(1'000'000'000'000'000)) return fail("no update time");
            parsed.dateMs = stamp->toInt64() * 1000;
            if (!root["rates"].isObject()) return fail("no rates");
            raw = root["rates"].members();
            break;
        }
        case Shape::Fawaz: {
            const auto date = dateOf(root["date"]);
            if (!date) return fail("no date");
            parsed.dateMs = *date;
            if (!root["usd"].isObject()) return fail("no rates");
            raw = root["usd"].members();
            break;
        }
        case Shape::Frankfurter: {
            const auto date = dateOf(root["date"]);
            if (!root["base"].isString() || !root["base"].equals("USD") || !date) return fail("not a USD response with a date");
            parsed.dateMs = *date;
            const auto rates = root["rates"];
            if (rates.isObject()) {
                raw = rates.members();
            } else if (rates.valid() && !rates.isNull()) {
                return fail("no rates");
            }
            raw.erase(std::remove_if(raw.begin(), raw.end(), [](const auto& m) { return m.first == "USD"; }), raw.end());
            break;
        }
    }
    // Repeated keys keep the last value, like Python's json.
    std::map<std::string, json::Value> members;
    for (const auto& [code, value] : raw) members[code] = value;
    for (const auto& [code, value] : members) {
        std::string upper = code;
        for (char& c : upper) {
            if (c >= 'a' && c <= 'z') c = static_cast<char>(c - 'a' + 'A');
        }
        if (!supported.contains(upper)) continue;
        const auto rate = Decimal::ofJson(value);
        if (!rate || *rate <= Decimal()) {
            if (reason) *reason = "bad rate for " + upper;
            return std::nullopt;
        }
        parsed.rates[upper] = *rate;
    }
    parsed.rates["USD"] = Decimal(1);
    return parsed;
}

Verdict check(std::int64_t dateMs, const std::map<std::string, Decimal>& rates, const std::string& selected, std::int64_t nowMs,
              const std::optional<Reference>& last, const std::map<std::string, Decimal>* fallback,
              const std::map<std::string, Decimal>* other, std::string* reason) {
    constexpr std::int64_t kDayMs = 86'400'000;
    const auto reject = [&](const char* why) {
        if (reason) *reason = why;
        return Verdict::Rejected;
    };
    if (dateMs > nowMs + 2 * kDayMs) return reject("date in the future");
    if (last && dateMs < last->dateMs) return reject("older than the stored rates");
    const auto value = rates.find(selected);
    if (value == rates.end()) return reject("selected currency missing");
    std::optional<Decimal> reference;
    if (last) {
        if (nowMs - last->acceptedMs <= 30 * kDayMs) {
            if (const auto previous = last->rates.find(selected); previous != last->rates.end()) reference = previous->second;
        }
    } else if (fallback) {
        if (const auto bundledRate = fallback->find(selected); bundledRate != fallback->end()) reference = bundledRate->second;
    }
    if (reference) {
        return reference->half() <= value->second && value->second.half() <= *reference ? Verdict::Accepted : reject("outside x0.5..x2");
    }
    if (!other) return Verdict::NeedsSecondProvider;
    const auto second = other->find(selected);
    if (second == other->end()) return Verdict::NeedsSecondProvider;
    const auto difference = (value->second - second->second).abs();
    return difference <= second->second * Decimal::of(3, 2) ? Verdict::Accepted : reject("providers disagree");
}

}  // namespace rates

}  // namespace agentspeed::money
