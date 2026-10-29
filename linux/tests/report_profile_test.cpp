#include <gtest/gtest.h>

#include "agentspeed/report_profile.hpp"

using namespace agentspeed::report;
using namespace agentspeed::report::profile;

namespace {

std::string chunk(std::string_view kind, std::string_view data) {
    std::string out;
    const auto n = static_cast<std::uint32_t>(data.size());
    for (int shift = 24; shift >= 0; shift -= 8) out += static_cast<char>((n >> shift) & 0xFF);
    out += kind;
    out += data;
    out += std::string(4, '\0');  // the CRC is not checked here
    return out;
}

std::string png(std::uint32_t width, std::uint32_t height, bool animated = false) {
    std::string ihdr;
    for (const auto v : {width, height})
        for (int shift = 24; shift >= 0; shift -= 8) ihdr += static_cast<char>((v >> shift) & 0xFF);
    ihdr += std::string("\x08\x06\x00\x00\x00", 5);
    std::string out("\x89PNG\r\n\x1a\n", 8);
    out += chunk("IHDR", ihdr);
    if (animated) out += chunk("acTL", std::string(8, '\0'));
    out += chunk("IDAT", "xx");
    out += chunk("IEND", "");
    return out;
}

constexpr const char* kProfile = R"({"sharedUserId":"11111111-1111-4111-8111-111111111111","displayName":"Zoe","avatar":{"url":"https://service.example/avatars/a.png","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","contentType":"image/png","width":256,"height":256},"devices":[{"deviceId":"22222222-2222-4222-8222-222222222222","platform":"Linux","isCurrent":true,"online":true},{"deviceId":"33333333-3333-4333-8333-333333333333","platform":"macOS","isCurrent":false,"online":false}]})";

}  // namespace

TEST(ReportProfile, Snapshots) {
    const auto profile = parseProfile(kProfile, UsernameRules{});
    ASSERT_TRUE(profile);
    EXPECT_EQ(profile->sharedUserId, std::optional<std::string>("11111111-1111-4111-8111-111111111111"));
    ASSERT_TRUE(profile->avatar);
    EXPECT_EQ(profile->devices.size(), 2U);
    EXPECT_TRUE(profile->devices[0].isCurrent);
    std::string unbound = R"({"sharedUserId":null,"displayName":"zoe-mbp","avatar":null,"devices":[]})";
    EXPECT_TRUE(parseProfile(unbound, UsernameRules{}));
    for (const auto& [from, to] : std::vector<std::pair<std::string, std::string>>{
             {"\"Linux\",\"isCurrent\":true", "\"Plan9\",\"isCurrent\":true"},
             {"\"width\":256", "\"width\":512"},
             {"https://service.example", "http://service.example"},
             {"\"isCurrent\":false", "\"isCurrent\":true"},
             {"33333333-3333-4333-8333-333333333333", "22222222-2222-4222-8222-222222222222"},
             {"\"displayName\":\"Zoe\"", "\"displayName\":\"x\""}}) {
        auto text = std::string(kProfile);
        text.replace(text.find(from), from.size(), to);
        EXPECT_FALSE(parseProfile(text, UsernameRules{})) << to;
    }
    EXPECT_FALSE(parseProfile(R"({"sharedUserId":null,"displayName":"zoe","devices":[]})", UsernameRules{}));  // avatar missing
}

TEST(ReportProfile, Pairings) {
    const std::int64_t now = 1'792'872'000'000;  // 2026-10-24T20:00:00Z
    const auto waiting = parsePairing(R"({"id":"33333333-3333-4333-8333-333333333333","state":"waiting","expiresAt":"2026-10-24T20:10:00.000Z","code":"abcd-efgh-jkmn"})", now);
    ASSERT_TRUE(waiting);
    EXPECT_EQ(waiting->code, std::optional<std::string>("ABCDEFGHJKMN"));
    EXPECT_EQ(waiting->expiresAtMs, now + 600'000);
    EXPECT_FALSE(parsePairing(R"({"id":"33333333-3333-4333-8333-333333333333","state":"pending","expiresAt":"2026-10-24T20:10:00.000Z"})", now));
    EXPECT_TRUE(parsePairing(R"({"id":"33333333-3333-4333-8333-333333333333","state":"pending","expiresAt":"2026-10-24T20:10:00.000Z","claimant":{"deviceId":"44444444-4444-4444-8444-444444444444","platform":"Windows"}})", now));
    EXPECT_FALSE(parsePairing(R"({"id":"33333333-3333-4333-8333-333333333333","state":"waiting","expiresAt":"2026-10-24T20:30:00.000Z"})", now));
    EXPECT_FALSE(parsePairing(R"({"id":"33333333-3333-4333-8333-333333333333","state":"done","expiresAt":"2026-10-24T20:10:00.000Z"})", now));
    EXPECT_EQ(normalizeCode("abcd-efgh-jkmn"), std::optional<std::string>("ABCDEFGHJKMN"));
    EXPECT_FALSE(normalizeCode("ABCD-EFGH-IJKL"));  // I is not in the alphabet
    EXPECT_FALSE(normalizeCode("ABCD-EFGH"));
    EXPECT_EQ(displayCode("ABCDEFGHJKMN"), "ABCD-EFGH-JKMN");
}

TEST(ReportProfile, ImageHeaders) {
    const auto header = imageHeader(png(256, 256));
    ASSERT_TRUE(header);
    EXPECT_TRUE(header->png);
    EXPECT_TRUE(isUpload(png(256, 256)));
    EXPECT_FALSE(isUpload(png(512, 512)));
    EXPECT_FALSE(imageHeader(png(256, 256, true)));
    EXPECT_FALSE(imageHeader(png(9000, 10)));
    EXPECT_FALSE(imageHeader(png(5000, 5000)));
    std::string jpeg("\xFF\xD8\xFF\xE0\x00\x04xx\xFF\xC0\x00\x11\x08\x01\x00\x02\x00", 20);
    jpeg += std::string(16, '\0');
    const auto j = imageHeader(jpeg);
    ASSERT_TRUE(j);
    EXPECT_FALSE(j->png);
    EXPECT_EQ(j->width, 512);
    EXPECT_EQ(j->height, 256);
    EXPECT_FALSE(imageHeader("GIF89a........"));
}
