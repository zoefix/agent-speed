#pragma once

#include <array>
#include <cstdint>
#include <functional>
#include <initializer_list>
#include <optional>
#include <string>
#include <string_view>

#include "agentspeed/quota/window.hpp"

// QUOTAS 7: the texts of the quota rows and the orb card. The app passes its string table (key → template in the app
// language, `%@` / `%d` placeholders and `%%`, spec/strings.json) and the local UTC offset.
namespace agentspeed::quota {

using StringLookup = std::function<std::string(std::string_view key)>;
// The UTC offset in milliseconds at an instant (positive east of Greenwich), e.g. TimeZone::offsetMs.
using UtcOffset = std::function<std::int64_t(std::int64_t unixMs)>;

// The templates of QUOTAS 7 in one language.
struct QuotaStrings {
    std::string resetIn;       // quota.reset.in
    std::string resetAt;       // quota.reset.at
    std::string minutes;       // quota.duration.m
    std::string hours;         // quota.duration.h
    std::string hoursMinutes;  // quota.duration.hm
    std::string days;          // quota.duration.d
    std::string age;           // quota.age
    std::string pace;          // quota.pace
    std::string month;         // quota.window.month
    std::string credits;       // quota.window.credits
    static QuotaStrings from(const StringLookup& lookup);
};

// Replaces `%@` and `%d` in order and `%%` with `%`, like the reference's formatter, in every language.
std::string fill(std::string_view text, std::initializer_list<std::string_view> arguments);

// The weekday short names, Monday first, of `zh-Hant`, `zh-Hans`, `en` and `ja` (any other language: `en`).
const std::array<std::string_view, 7>& weekdayNames(std::string_view language);

class QuotaFormat {
public:
    static constexpr std::int64_t kHourMs = 3'600'000;
    static constexpr std::int64_t kDayMs = 24 * kHourMs;

    QuotaFormat(QuotaStrings strings, std::string language, UtcOffset offset);

    // `42 分後重置`, `3 小時 12 分後重置`, `週五 09:00 重置`, or `11/17 09:00 重置` when the weekday is today's.
    std::string resetLong(std::int64_t resetsAtMs, std::int64_t nowMs) const;
    // `42m`, `3h`, the weekday's short name, or `M/d` when it is today's weekday.
    std::string resetShort(std::int64_t resetsAtMs, std::int64_t nowMs) const;
    // `quota.age` once the snapshot is an hour old: whole hours, from 24 hours whole days; nullopt before.
    std::optional<std::string> age(std::int64_t observedMs, std::int64_t nowMs) const;
    // Minutes below an hour, else hours with their minutes rounded down (an exact hour drops them).
    std::string duration(std::int64_t ms) const;
    // `時間進度 60%` for a pace share 0…1.
    std::string paceText(double share) const;
    // The window's label with this language's month and credits words.
    std::string label(const Window& window) const;

    const QuotaStrings& strings() const { return strings_; }

private:
    struct Local {
        int weekday = 0;  // Monday 0
        unsigned month = 1, day = 1;
        int hour = 0, minute = 0;
    };
    Local local(std::int64_t unixMs) const;
    std::string weekdayOrDate(std::int64_t resetsAtMs, std::int64_t nowMs) const;

    QuotaStrings strings_;
    std::string language_;
    UtcOffset offset_;
};

}  // namespace agentspeed::quota
