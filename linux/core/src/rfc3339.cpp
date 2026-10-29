#include "agentspeed/rfc3339.hpp"

namespace agentspeed {

namespace {

bool digits(std::string_view text, std::size_t pos, std::size_t count, int& out) {
    if (pos + count > text.size()) return false;
    int value = 0;
    for (std::size_t i = pos; i < pos + count; ++i) {
        const char c = text[i];
        if (c < '0' || c > '9') return false;
        value = value * 10 + (c - '0');
    }
    out = value;
    return true;
}

bool leapYear(int year) { return (year % 4 == 0 && year % 100 != 0) || year % 400 == 0; }

int daysInMonth(int year, int month) {
    static constexpr int kDays[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    return month == 2 && leapYear(year) ? 29 : kDays[month - 1];
}

}  // namespace

std::int64_t daysFromCivil(std::int64_t year, unsigned month, unsigned day) {
    year -= month <= 2 ? 1 : 0;
    const std::int64_t era = (year >= 0 ? year : year - 399) / 400;
    const auto yoe = static_cast<unsigned>(year - era * 400);
    const unsigned doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
    const unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + static_cast<std::int64_t>(doe) - 719468;
}

std::optional<std::int64_t> parseTimestamp(std::string_view text) {
    if (text.size() > 64 || text.size() < 20) return std::nullopt;
    int year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0;
    if (!digits(text, 0, 4, year) || text[4] != '-' || !digits(text, 5, 2, month) || text[7] != '-' ||
        !digits(text, 8, 2, day)) {
        return std::nullopt;
    }
    const char separator = text[10];
    if (separator != 'T' && separator != 't' && separator != ' ') return std::nullopt;
    if (!digits(text, 11, 2, hour) || text[13] != ':' || !digits(text, 14, 2, minute) || text[16] != ':' ||
        !digits(text, 17, 2, second)) {
        return std::nullopt;
    }
    if (month < 1 || month > 12 || day < 1 || day > daysInMonth(year, month) || hour > 23 || minute > 59 ||
        second > 59) {
        return std::nullopt;
    }
    std::size_t pos = 19;
    int millis = 0;
    if (pos < text.size() && text[pos] == '.') {
        ++pos;
        const std::size_t start = pos;
        while (pos < text.size() && text[pos] >= '0' && text[pos] <= '9') {
            if (pos - start < 3) millis = millis * 10 + (text[pos] - '0');
            ++pos;
        }
        const std::size_t count = pos - start;
        if (count == 0) return std::nullopt;
        for (std::size_t k = count; k < 3; ++k) millis *= 10;
    }
    if (pos >= text.size()) return std::nullopt;
    std::int64_t offsetSeconds = 0;
    const char zone = text[pos];
    if (zone == 'Z' || zone == 'z') {
        ++pos;
    } else if (zone == '+' || zone == '-') {
        int offsetHour = 0, offsetMinute = 0;
        if (!digits(text, pos + 1, 2, offsetHour)) return std::nullopt;
        std::size_t next = pos + 3;
        if (next < text.size() && text[next] == ':') ++next;
        if (!digits(text, next, 2, offsetMinute)) return std::nullopt;
        if (offsetHour > 23 || offsetMinute > 59) return std::nullopt;
        offsetSeconds = (offsetHour * 3600 + offsetMinute * 60) * (zone == '-' ? -1 : 1);
        pos = next + 2;
    } else {
        return std::nullopt;
    }
    if (pos != text.size()) return std::nullopt;
    const std::int64_t days = daysFromCivil(year, static_cast<unsigned>(month), static_cast<unsigned>(day));
    const std::int64_t seconds = days * 86400 + hour * 3600 + minute * 60 + second - offsetSeconds;
    return seconds * 1000 + millis;
}

}  // namespace agentspeed
