#include <gtest/gtest.h>

#include "agentspeed/lan_address.hpp"
#include "lan_vectors.hpp"

// LAN 2.3 and 5.1: local addresses, target addresses and the check after resolution.
using namespace agentspeed;
using namespace agentspeed::lan;

TEST(LanLocal, Vectors) {
    const auto cases = lan_vectors::section("local").elements();
    ASSERT_EQ(cases.size(), 24u);
    for (const auto& c : cases) {
        const auto address = *c.at(0).string();
        EXPECT_EQ(isLocal(address, c.at(1).isTrue()), c.at(2).isTrue()) << address << (c.at(1).isTrue() ? " udp" : "");
    }
}

TEST(LanLocal, RawBytes) {
    EXPECT_TRUE(isLocalIPv4({192, 168, 0, 1}));
    EXPECT_FALSE(isLocalIPv4({100, 64, 0, 1}));
    EXPECT_TRUE(isLocalIPv6({0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 10, 0, 0, 1}));
    EXPECT_FALSE(isLocalIPv6({0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1}));
    EXPECT_FALSE(isLocal("192.168.01.1")) << "leading zeros are not an address";
    EXPECT_FALSE(isLocal("desk.local"));
}

TEST(LanTargets, Vectors) {
    const auto cases = lan_vectors::section("targets").elements();
    ASSERT_EQ(cases.size(), 27u);
    for (const auto& c : cases) {
        const auto address = *c["address"].string();
        const auto kind = *kindFromName(*c["kind"].string());
        const auto result = parseTarget(kind, address);
        const auto expected = c["result"];
        if (const auto error = expected["error"]; error.valid()) {
            ASSERT_TRUE(std::holds_alternative<std::string>(result)) << address;
            EXPECT_EQ(std::get<std::string>(result), *error.string()) << address;
            EXPECT_FALSE(validTarget(kind, address));
            continue;
        }
        ASSERT_TRUE(std::holds_alternative<TargetAddress>(result)) << address << ": " << std::get<std::string>(result);
        const auto& got = std::get<TargetAddress>(result);
        EXPECT_EQ(got.host, *expected["host"].string()) << address;
        EXPECT_EQ(got.literal, expected["literal"].isTrue()) << address;
        EXPECT_EQ(got.port, *expected["port"].truncated()) << address;
        if (kind == TargetKind::Http) {
            EXPECT_EQ(got.hostHeader, *expected["hostHeader"].string()) << address;
            EXPECT_EQ(got.target, *expected["target"].string()) << address;
        }
    }
}

TEST(LanTargets, Broadcast) {
    const auto parsed = [](std::string_view a) { return std::get<TargetAddress>(parseTarget(TargetKind::Udp, a)); };
    EXPECT_TRUE(isBroadcast(parsed("255.255.255.255:7247")));
    EXPECT_TRUE(isBroadcast(parsed("192.168.1.255:7247")));
    EXPECT_FALSE(isBroadcast(parsed("192.168.1.25:7247")));
    EXPECT_FALSE(isBroadcast(parsed("host.255x:1")));
    EXPECT_FALSE(isBroadcast(parsed("[fe80::255]:1")));
}

TEST(LanResolutions, Vectors) {
    const auto cases = lan_vectors::section("resolutions").elements();
    ASSERT_EQ(cases.size(), 6u);
    for (const auto& c : cases) {
        std::vector<std::string> addresses;
        for (const auto& a : c["addresses"].elements()) addresses.push_back(*a.string());
        const auto status = resolveCheck(addresses, c["udp"].isTrue());
        const auto name = status == Resolution::Ok ? "ok" : status == Resolution::NotFound ? "notFound" : "notLocal";
        EXPECT_EQ(name, *c["status"].string());
    }
}
