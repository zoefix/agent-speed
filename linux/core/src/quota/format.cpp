#include "agentspeed/quota/format.hpp"

#include <cmath>
#include <cstdio>

namespace agentspeed::quota {

namespace {

constexpr std::array<std::string_view, 7> kZhHant = {"週一", "週二", "週三", "週四", "週五", "週六", "週日"};
constexpr std::array<std::string_view, 7> kZhHans = {"周一", "周二", "周三", "周四", "周五", "周六", "周日"};
constexpr std::array<std::string_view, 7> kEnglish = {"Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"};
constexpr std::array<std::string_view, 7> kJapanese = {"月", "火", "水", "木", "金", "土", "日"};

std::int64_t floorDiv(std::int64_t a, std::int64_t b) { return a / b - ((a % b != 0) && ((a < 0) != (b < 0)) ? 1 : 0); }

// Days since 1970-01-01 to a proleptic Gregorian month and day (H. Hinnant's civil_from_days).
void civilFromDays(std::int64_t days, unsigned& month, unsigned& day) {
    const std::int64_t z = days + 719468;
    const std::int64_t era = floorDiv(z, 146097);
    const auto doe = static_cast<unsigned>(z - era * 146097);
    const unsigned yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    const unsigned doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    const unsigned mp = (5 * doy + 2) / 153;
    day = doy - (153 * mp + 2) / 5 + 1;
    month = mp < 10 ? mp + 3 : mp - 9;
}

std::string twoDigits(int value) {
    char text[8];
    std::snprintf(text, sizeof text, "%02d", value);
    return text;
}

}  // namespace

QuotaStrings QuotaStrings::from(const StringLookup& lookup) {
    QuotaStrings s;
    s.resetIn = lookup("quota.reset.in");
    s.resetAt = lookup("quota.reset.at");
    s.minutes = lookup("quota.duration.m");
    s.hours = lookup("quota.duration.h");
    s.hoursMinutes = lookup("quota.duration.hm");
    s.days = lookup("quota.duration.d");
    s.age = lookup("quota.age");
    s.pace = lookup("quota.pace");
    s.month = lookup("quota.window.month");
    s.credits = lookup("quota.window.credits");
    return s;
}

std::string fill(std::string_view text, std::initializer_list<std::string_view> arguments) {
    std::string out;
    auto next = arguments.begin();
    for (std::size_t i = 0; i < text.size(); ++i) {
        if (text[i] != '%' || i + 1 >= text.size()) {
            out += text[i];
            continue;
        }
        const char kind = text[i + 1];
        if (kind == '%') {
            out += '%';
            ++i;
        } else if (kind == '@' || kind == 'd') {
            if (next != arguments.end()) out += *next++;
            ++i;
        } else {
            out += '%';
        }
    }
    return out;
}

const std::array<std::string_view, 7>& weekdayNames(std::string_view language) {
    if (language == "zh-Hant") return kZhHant;
    if (language == "zh-Hans") return kZhHans;
    if (language == "ja") return kJapanese;
    return kEnglish;
}

QuotaFormat::QuotaFormat(QuotaStrings strings, std::string language, UtcOffset offset)
    : strings_(std::move(strings)), language_(std::move(language)), offset_(std::move(offset)) {}

QuotaFormat::Local QuotaFormat::local(std::int64_t unixMs) const {
    const std::int64_t localMs = unixMs + (offset_ ? offset_(unixMs) : 0);
    const std::int64_t days = floorDiv(localMs, kDayMs);
    const std::int64_t msOfDay = localMs - days * kDayMs;
    Local out;
    // 1970-01-01 was a Thursday (Monday 0 → 3).
    const std::int64_t shifted = days + 3;
    out.weekday = static_cast<int>(shifted - floorDiv(shifted, 7) * 7);
    civilFromDays(days, out.month, out.day);
    out.hour = static_cast<int>(msOfDay / kHourMs);
    out.minute = static_cast<int>((msOfDay % kHourMs) / 60'000);
    return out;
}

std::string QuotaFormat::weekdayOrDate(std::int64_t resetsAtMs, std::int64_t nowMs) const {
    const auto at = local(resetsAtMs);
    if (at.weekday == local(nowMs).weekday) return std::to_string(at.month) + "/" + std::to_string(at.day);
    return std::string(weekdayNames(language_)[static_cast<std::size_t>(at.weekday)]);
}

std::string QuotaFormat::duration(std::int64_t ms) const {
    const std::int64_t minutes = ms / 60'000;
    if (ms < kHourMs) return fill(strings_.minutes, {std::to_string(minutes)});
    const std::int64_t hours = minutes / 60;
    const std::int64_t rest = minutes % 60;
    if (rest == 0) return fill(strings_.hours, {std::to_string(hours)});
    return fill(strings_.hoursMinutes, {std::to_string(hours), std::to_string(rest)});
}

std::string QuotaFormat::resetLong(std::int64_t resetsAtMs, std::int64_t nowMs) const {
    const std::int64_t d = resetsAtMs - nowMs;
    if (d < kDayMs) return fill(strings_.resetIn, {duration(d > 0 ? d : 0)});
    const auto at = local(resetsAtMs);
    return fill(strings_.resetAt, {weekdayOrDate(resetsAtMs, nowMs) + " " + twoDigits(at.hour) + ":" + twoDigits(at.minute)});
}

std::string QuotaFormat::resetShort(std::int64_t resetsAtMs, std::int64_t nowMs) const {
    const std::int64_t d = resetsAtMs - nowMs;
    if (d < kHourMs) return std::to_string((d > 0 ? d : 0) / 60'000) + "m";
    if (d < kDayMs) return std::to_string(d / kHourMs) + "h";
    return weekdayOrDate(resetsAtMs, nowMs);
}

std::optional<std::string> QuotaFormat::age(std::int64_t observedMs, std::int64_t nowMs) const {
    const std::int64_t elapsed = nowMs - observedMs;
    if (elapsed < kHourMs) return std::nullopt;
    if (elapsed < kDayMs) return fill(strings_.age, {fill(strings_.hours, {std::to_string(elapsed / kHourMs)})});
    return fill(strings_.age, {fill(strings_.days, {std::to_string(elapsed / kDayMs)})});
}

std::string QuotaFormat::paceText(double share) const {
    const double clamped = std::isfinite(share) ? std::fmin(1.0, std::fmax(0.0, share)) : 0.0;
    return fill(strings_.pace, {std::to_string(std::lround(clamped * 100))});
}

std::string QuotaFormat::label(const Window& window) const { return quota::label(window, strings_.month, strings_.credits); }

}  // namespace agentspeed::quota
