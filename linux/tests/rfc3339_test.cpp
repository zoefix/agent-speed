#include <gtest/gtest.h>

#include "agentspeed/rfc3339.hpp"

using agentspeed::parseTimestamp;

TEST(Rfc3339, ParsesZonesAndFractions) {
    EXPECT_EQ(parseTimestamp("1970-01-01T00:00:00Z"), 0);
    EXPECT_EQ(parseTimestamp("2026-10-20T10:00:00.123Z"), 1792490400123);
    EXPECT_EQ(parseTimestamp("2026-10-20t10:00:00.1234567z"), 1792490400123);
    EXPECT_EQ(parseTimestamp("2026-10-20 10:00:00.5Z"), 1792490400500);
    EXPECT_EQ(parseTimestamp("2026-10-20T18:00:00+08:00"), 1792490400000);
    EXPECT_EQ(parseTimestamp("2026-10-20T18:00:00+0800"), 1792490400000);
    EXPECT_EQ(parseTimestamp("2026-10-20T05:00:00-05:00"), 1792490400000);
}

TEST(Rfc3339, HandlesCalendarEdges) {
    EXPECT_EQ(parseTimestamp("1969-12-31T23:59:59Z"), -1000);
    EXPECT_TRUE(parseTimestamp("0000-02-29T00:00:00Z"));
    EXPECT_TRUE(parseTimestamp("2024-02-29T00:00:00Z"));
    EXPECT_FALSE(parseTimestamp("2023-02-29T00:00:00Z"));
    EXPECT_FALSE(parseTimestamp("1900-02-29T00:00:00Z"));
    EXPECT_EQ(parseTimestamp("9999-12-31T23:59:59-23:59"), 253402300799000LL + (23 * 3600 + 59 * 60) * 1000LL);
}

TEST(Rfc3339, RejectsEverythingElse) {
    for (std::string_view bad : {"2026-10-20T10:00:00", "2026-10-20T10:00:00+08", "2026-10-20T24:00:00Z",
                                 "2026-10-20T10:60:00Z", "2026-10-20T10:00:60Z", "2026-13-01T00:00:00Z",
                                 "2026-10-20T10:00:00.Z", " 2026-10-20T10:00:00Z", "2026-10-20T10:00:00Z ",
                                 "2026-10-20X10:00:00Z", "2026-10-20T10:00:00+24:00", "2026-10-20T10:00:00+08:60",
                                 "2026-1-20T10:00:00Z", "2026-10-20T10:00:00+08:0"}) {
        EXPECT_FALSE(parseTimestamp(bad)) << bad;
    }
    std::string longFraction = "2026-10-20T10:00:00." + std::string(44, '1') + "Z";
    EXPECT_EQ(longFraction.size(), 65u);
    EXPECT_FALSE(parseTimestamp(longFraction));
}
