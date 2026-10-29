#pragma once

#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <vector>

// A time zone from the system's TZif files (with the POSIX rule of their footer), for converting the zone-less wall
// times some sources write (Hermes Agent, METRICS 4.15).
namespace agentspeed {

class TimeZone {
public:
    // "UTC" and "Etc/UTC" are built in; "" or "localtime" is /etc/localtime; anything else /usr/share/zoneinfo/<id>.
    // nullptr when the zone cannot be read.
    static std::shared_ptr<const TimeZone> load(const std::string& id);
    static std::shared_ptr<const TimeZone> utc();
    // UTC offset in milliseconds at the instant (positive east of Greenwich).
    std::int64_t offsetMs(std::int64_t unixMs) const;

    struct Rule {
        // POSIX TZ: standard and daylight offsets (seconds east) and the two transitions.
        std::int64_t stdOffset = 0, dstOffset = 0;
        bool hasDst = false;
        struct Date {
            char kind = 'M';  // 'M' month.week.day, 'J' Julian 1-365 without Feb 29, 'N' zero-based day of year
            int month = 0, week = 0, day = 0;
            std::int64_t timeSeconds = 7200;
        } start, end;
    };

private:
    std::vector<std::int64_t> transitions_;  // seconds
    std::vector<std::uint8_t> indices_;
    std::vector<std::int32_t> offsets_;  // seconds, per type
    std::vector<bool> isDst_;
    std::optional<Rule> rule_;
    static bool parse(const std::string& data, TimeZone& zone);
    std::int64_t ruleOffsetSeconds(std::int64_t unixSeconds) const;
};

std::optional<TimeZone::Rule> parsePosixTz(const std::string& text);

}  // namespace agentspeed
