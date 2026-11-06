#include <gtest/gtest.h>
#include <unistd.h>

#include <filesystem>

#include "agentspeed/tz.hpp"
#include "fake_home.hpp"
#include "fixture_support.hpp"
#include "zone_file.hpp"

using namespace agentspeed;
using namespace agentspeed::relay;
namespace fs = std::filesystem;

namespace {

// A TZif v1 file with one type (UTC+2, no transitions), and its base64.
std::string fixedZone() {
    std::string d = "TZif";
    d += std::string(16, '\0');
    const auto count = [&](std::uint32_t v) {
        for (int s = 24; s >= 0; s -= 8) d += static_cast<char>((v >> s) & 0xFF);
    };
    count(0);  // isutcnt
    count(0);  // isstdcnt
    count(0);  // leapcnt
    count(0);  // timecnt
    count(1);  // typecnt
    count(4);  // charcnt
    count(7200);
    d += '\0';
    d += '\0';
    d += std::string("EET\0", 4);
    return d;
}

}  // namespace

TEST(RelayZone, DecodesBase64) {
    EXPECT_EQ(decodeBase64("TWFu"), "Man");
    EXPECT_EQ(decodeBase64("TWE="), "Ma");
    EXPECT_EQ(decodeBase64("TWE"), "Ma");
    EXPECT_EQ(decodeBase64("TQ=="), "M");
    EXPECT_EQ(decodeBase64(""), "");
    EXPECT_FALSE(decodeBase64("TW!u"));
    EXPECT_FALSE(decodeBase64("T"));
    EXPECT_FALSE(decodeBase64("TWFu TWFu"));
    EXPECT_FALSE(decodeBase64("TWFuTWFu", 3));
}

TEST(RelayZone, ParsesTzifBytesAndRefusesBrokenHeaders) {
    const auto zone = TimeZone::fromData(fixedZone());
    ASSERT_TRUE(zone);
    EXPECT_EQ(zone->offsetMs(1792490400000), 7'200'000);
    EXPECT_FALSE(TimeZone::fromData(""));
    EXPECT_FALSE(TimeZone::fromData("TZif2"));
    auto negative = fixedZone();
    negative[20 + 12] = '\xFF';  // timecnt −1 ...
    negative[20 + 13] = '\xFF';
    negative[20 + 14] = '\xFF';
    negative[20 + 15] = '\xFF';
    EXPECT_FALSE(TimeZone::fromData(negative));
    auto huge = fixedZone();
    huge[20 + 12] = '\x7F';  // ... and a count beyond the data
    EXPECT_FALSE(TimeZone::fromData(huge));
}

TEST(RelayZone, InstallsTheAppsZoneInTheFolderAndFallsBack) {
    const auto folder = relay_test::makeTempDir("agentspeed-zone");
    const std::string encoded = "VFppZgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAQAABwgAABFRVQA";
    ASSERT_EQ(decodeBase64(encoded), fixedZone());
    auto loaded = installZone(folder.string(), "Europe/Athens", encoded);
    ASSERT_TRUE(loaded.zone);
    EXPECT_TRUE(loaded.fromApp);
    EXPECT_EQ(loaded.id, "Europe/Athens");
    EXPECT_EQ(loaded.zone->offsetMs(0), 7'200'000);
    EXPECT_EQ(fixtures::readText(folder / "zone.tzif"), fixedZone());

    // Unusable data: the host's zone files, else UTC; a hostile id is never a path.
    loaded = installZone(folder.string(), "UTC", "!!!");
    EXPECT_FALSE(loaded.fromApp);
    EXPECT_EQ(loaded.id, "UTC");
    EXPECT_EQ(loaded.zone->offsetMs(0), 0);
    loaded = installZone(folder.string(), "../../etc/passwd", "AAAA");
    EXPECT_EQ(loaded.id, "UTC");
    loaded = installZone(folder.string(), "/etc/localtime", "AAAA");
    EXPECT_EQ(loaded.id, "UTC");

    // Never written through a symbolic link.
    fs::remove(folder / "zone.tzif");
    fs::create_symlink(folder / "target", folder / "zone.tzif");
    loaded = installZone(folder.string(), "UTC", encoded);
    EXPECT_FALSE(loaded.fromApp);
    EXPECT_FALSE(fs::exists(folder / "target"));
    fs::remove_all(folder);
}
