#include "agentspeed/tz.hpp"

#include <algorithm>
#include <fstream>
#include <iterator>

#include "agentspeed/rfc3339.hpp"

namespace agentspeed {

namespace {

std::int64_t readBig(const std::string& d, std::size_t at, int bytes) {
    std::uint64_t v = 0;
    for (int i = 0; i < bytes; ++i) v = (v << 8) | static_cast<unsigned char>(d[at + static_cast<std::size_t>(i)]);
    if (bytes == 4) return static_cast<std::int32_t>(static_cast<std::uint32_t>(v));
    return static_cast<std::int64_t>(v);
}

// [+-]hh[:mm[:ss]] → seconds; POSIX offsets are west-positive, so the caller negates for east-positive offsets.
bool parseTime(const std::string& s, std::size_t& i, std::int64_t& out) {
    int sign = 1;
    if (i < s.size() && (s[i] == '+' || s[i] == '-')) {
        sign = s[i] == '-' ? -1 : 1;
        ++i;
    }
    auto number = [&](std::int64_t& v) {
        if (i >= s.size() || s[i] < '0' || s[i] > '9') return false;
        v = 0;
        while (i < s.size() && s[i] >= '0' && s[i] <= '9') v = v * 10 + (s[i++] - '0');
        return true;
    };
    std::int64_t h = 0, m = 0, sec = 0;
    if (!number(h)) return false;
    if (i < s.size() && s[i] == ':') {
        ++i;
        if (!number(m)) return false;
        if (i < s.size() && s[i] == ':') {
            ++i;
            if (!number(sec)) return false;
        }
    }
    out = sign * (h * 3600 + m * 60 + sec);
    return true;
}

bool parseName(const std::string& s, std::size_t& i) {
    if (i < s.size() && s[i] == '<') {
        const auto end = s.find('>', i);
        if (end == std::string::npos) return false;
        i = end + 1;
        return true;
    }
    const std::size_t start = i;
    while (i < s.size() && ((s[i] >= 'a' && s[i] <= 'z') || (s[i] >= 'A' && s[i] <= 'Z'))) ++i;
    return i - start >= 3;
}

bool parseDate(const std::string& s, std::size_t& i, TimeZone::Rule::Date& date) {
    auto number = [&](int& v) {
        if (i >= s.size() || s[i] < '0' || s[i] > '9') return false;
        v = 0;
        while (i < s.size() && s[i] >= '0' && s[i] <= '9') v = v * 10 + (s[i++] - '0');
        return true;
    };
    if (i < s.size() && s[i] == 'M') {
        ++i;
        date.kind = 'M';
        if (!number(date.month) || i >= s.size() || s[i++] != '.' || !number(date.week) || i >= s.size() || s[i++] != '.' ||
            !number(date.day)) {
            return false;
        }
    } else if (i < s.size() && s[i] == 'J') {
        ++i;
        date.kind = 'J';
        if (!number(date.day)) return false;
    } else {
        date.kind = 'N';
        if (!number(date.day)) return false;
    }
    if (i < s.size() && s[i] == '/') {
        ++i;
        if (!parseTime(s, i, date.timeSeconds)) return false;
    }
    return true;
}

bool leap(std::int64_t y) { return (y % 4 == 0 && y % 100 != 0) || y % 400 == 0; }

// Seconds since the epoch of the local transition date in `year`, as local wall time (without the offset).
std::int64_t transitionWall(std::int64_t year, const TimeZone::Rule::Date& d) {
    const std::int64_t jan1 = daysFromCivil(year, 1, 1);
    std::int64_t day = 0;
    if (d.kind == 'J') {
        day = d.day - 1;
        if (leap(year) && d.day >= 60) ++day;
    } else if (d.kind == 'N') {
        day = d.day;
    } else {
        static constexpr int kDays[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
        const std::int64_t first = daysFromCivil(year, static_cast<unsigned>(d.month), 1);
        const std::int64_t weekday = ((first % 7) + 11) % 7;  // 1970-01-01 was a Thursday (4)
        std::int64_t delta = (d.day - weekday + 7) % 7;
        std::int64_t dom = 1 + delta + (d.week - 1) * 7;
        int length = kDays[d.month - 1] + (d.month == 2 && leap(year) ? 1 : 0);
        while (dom > length) dom -= 7;
        day = first - jan1 + dom - 1;
    }
    return (jan1 + day) * 86400 + d.timeSeconds;
}

}  // namespace

std::optional<TimeZone::Rule> parsePosixTz(const std::string& s) {
    TimeZone::Rule rule;
    std::size_t i = 0;
    if (!parseName(s, i)) return std::nullopt;
    std::int64_t west = 0;
    if (!parseTime(s, i, west)) return std::nullopt;
    rule.stdOffset = -west;
    if (i >= s.size()) return rule;
    if (!parseName(s, i)) return std::nullopt;
    rule.hasDst = true;
    rule.dstOffset = rule.stdOffset + 3600;
    if (i < s.size() && s[i] != ',') {
        if (!parseTime(s, i, west)) return std::nullopt;
        rule.dstOffset = -west;
    }
    if (i >= s.size()) {
        // No rule: the US default (M3.2.0, M11.1.0).
        rule.start = {'M', 3, 2, 0, 7200};
        rule.end = {'M', 11, 1, 0, 7200};
        return rule;
    }
    if (s[i++] != ',' || !parseDate(s, i, rule.start) || i >= s.size() || s[i++] != ',' || !parseDate(s, i, rule.end)) {
        return std::nullopt;
    }
    return rule;
}

std::shared_ptr<const TimeZone> TimeZone::utc() {
    static const auto zone = [] {
        auto z = std::make_shared<TimeZone>();
        z->offsets_ = {0};
        z->isDst_ = {false};
        return z;
    }();
    return zone;
}

std::shared_ptr<const TimeZone> TimeZone::load(const std::string& id) {
    if (id == "UTC" || id == "Etc/UTC") return utc();
    std::string path;
    if (id.empty() || id == "localtime") {
        path = "/etc/localtime";
    } else {
        if (id.find("..") != std::string::npos || id.front() == '/') return nullptr;
        path = "/usr/share/zoneinfo/" + id;
    }
    std::ifstream in(path, std::ios::binary);
    if (!in) return nullptr;
    const std::string data((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    auto zone = std::make_shared<TimeZone>();
    if (!parse(data, *zone)) return nullptr;
    return zone;
}

bool TimeZone::parse(const std::string& d, TimeZone& z) {
    auto header = [&](std::size_t at, std::int64_t counts[6]) {
        if (d.size() < at + 44 || d.compare(at, 4, "TZif") != 0) return false;
        for (int k = 0; k < 6; ++k) counts[k] = readBig(d, at + 20 + 4 * static_cast<std::size_t>(k), 4);
        return true;
    };
    std::int64_t c[6];  // isutcnt, isstdcnt, leapcnt, timecnt, typecnt, charcnt
    if (!header(0, c)) return false;
    const char version = d[4];
    auto blockSize = [&](const std::int64_t n[6], int timeBytes) {
        return static_cast<std::size_t>(n[3] * timeBytes + n[3] + n[4] * 6 + n[5] + n[2] * (timeBytes + 4) + n[1] + n[0]);
    };
    std::size_t at = 44;
    int timeBytes = 4;
    if (version >= '2') {
        at += blockSize(c, 4);
        if (!header(at, c)) return false;
        at += 44;
        timeBytes = 8;
    }
    if (d.size() < at + blockSize(c, timeBytes)) return false;
    const auto timecnt = static_cast<std::size_t>(c[3]), typecnt = static_cast<std::size_t>(c[4]);
    for (std::size_t k = 0; k < timecnt; ++k) z.transitions_.push_back(readBig(d, at + k * static_cast<std::size_t>(timeBytes), timeBytes));
    at += timecnt * static_cast<std::size_t>(timeBytes);
    for (std::size_t k = 0; k < timecnt; ++k) z.indices_.push_back(static_cast<std::uint8_t>(d[at + k]));
    at += timecnt;
    for (std::size_t k = 0; k < typecnt; ++k) {
        z.offsets_.push_back(static_cast<std::int32_t>(readBig(d, at + k * 6, 4)));
        z.isDst_.push_back(d[at + k * 6 + 4] != 0);
    }
    if (z.offsets_.empty()) return false;
    for (auto index : z.indices_) {
        if (index >= typecnt) return false;
    }
    at += typecnt * 6 + static_cast<std::size_t>(c[5]) + static_cast<std::size_t>(c[2] * (timeBytes + 4) + c[1] + c[0]);
    if (version >= '2' && at < d.size() && d[at] == '\n') {
        const auto end = d.find('\n', at + 1);
        if (end != std::string::npos) z.rule_ = parsePosixTz(d.substr(at + 1, end - at - 1));
    }
    return true;
}

std::int64_t TimeZone::ruleOffsetSeconds(std::int64_t t) const {
    const Rule& r = *rule_;
    if (!r.hasDst) return r.stdOffset;
    // The civil year of the instant in standard time.
    const std::int64_t days = (t + r.stdOffset) / 86400 - ((t + r.stdOffset) % 86400 < 0 ? 1 : 0);
    std::int64_t z = days + 719468;
    const std::int64_t era = (z >= 0 ? z : z - 146096) / 146097;
    const std::int64_t doe = z - era * 146097;
    const std::int64_t yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    std::int64_t year = yoe + era * 400;
    const std::int64_t doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    const std::int64_t mp = (5 * doy + 2) / 153;
    if (mp >= 10) ++year;
    // Transitions are given in the local time in force before them.
    const std::int64_t start = transitionWall(year, r.start) - r.stdOffset;
    const std::int64_t end = transitionWall(year, r.end) - r.dstOffset;
    const bool dst = start < end ? (t >= start && t < end) : !(t >= end && t < start);
    return dst ? r.dstOffset : r.stdOffset;
}

std::int64_t TimeZone::offsetMs(std::int64_t unixMs) const {
    const std::int64_t t = unixMs / 1000 - (unixMs % 1000 < 0 ? 1 : 0);
    if (transitions_.empty()) {
        if (rule_) return ruleOffsetSeconds(t) * 1000;
        return static_cast<std::int64_t>(offsets_.front()) * 1000;
    }
    if (t < transitions_.front()) {
        // Before the first transition: the first standard-time type.
        for (std::size_t k = 0; k < offsets_.size(); ++k) {
            if (!isDst_[k]) return static_cast<std::int64_t>(offsets_[k]) * 1000;
        }
        return static_cast<std::int64_t>(offsets_.front()) * 1000;
    }
    if (t >= transitions_.back() && rule_) return ruleOffsetSeconds(t) * 1000;
    const auto it = std::upper_bound(transitions_.begin(), transitions_.end(), t);
    const auto index = indices_[static_cast<std::size_t>(it - transitions_.begin()) - 1];
    return static_cast<std::int64_t>(offsets_[index]) * 1000;
}

}  // namespace agentspeed
