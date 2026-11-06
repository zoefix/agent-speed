#include <gtest/gtest.h>

#include "agentspeed/remote_host.hpp"

namespace {

using namespace agentspeed::remote;

constexpr const char* kId1 = "8C0D6B0A-1111-4222-8333-444455556666";
constexpr const char* kId2 = "9D1E7C1B-2222-4333-9444-555566667777";

std::string idNumber(int n) {
    char buffer[40];
    std::snprintf(buffer, sizeof buffer, "00000000-0000-4000-8000-%012d", n);
    return buffer;
}

TEST(RemoteHostSettings, AcceptsEveryDestinationForm) {
    for (const auto* destination : {"devbox", "user@host", "user@10.0.0.2", "ssh://user@host:2222", "ssh://[::1]:22", "host.example.com",
                                    "a_b-c.d", "user@[fe80::1]", "%h", "a:b"}) {
        EXPECT_FALSE(validation::destinationIssue(destination)) << destination;
    }
}

TEST(RemoteHostSettings, RefusesDestinationsThatCouldInject) {
    EXPECT_EQ(validation::destinationIssue(""), FieldIssue::Empty);
    EXPECT_EQ(validation::destinationIssue(std::string(256, 'a')), FieldIssue::TooLong);
    EXPECT_FALSE(validation::destinationIssue(std::string(255, 'a')));
    EXPECT_EQ(validation::destinationIssue("-oProxyCommand=sh"), FieldIssue::LeadingDash);
    for (const auto* destination : {"host;rm -rf ~", "a b", "a\tb", "$(id)", "`id`", "a|b", "a&b", "a>b", "a<b", "(a)", "a*", "a?", "a!",
                                    "{a}", "a#b", "\"a\"", "'a'", "a\\b", "héllo", "a\nb", "a\x7f"}) {
        EXPECT_EQ(validation::destinationIssue(destination), FieldIssue::ForbiddenCharacter) << destination;
    }
}

TEST(RemoteHostSettings, AcceptsAndRefusesRelayFolders) {
    for (const auto* folder : {"~/.cache/agent-speed", "/opt/agent-speed", "~/a.b_c-d/e", "/", "~/x/"}) {
        EXPECT_FALSE(validation::folderIssue(folder)) << folder;
    }
    EXPECT_EQ(validation::folderIssue(""), FieldIssue::Empty);
    EXPECT_EQ(validation::folderIssue("/" + std::string(200, 'a')), FieldIssue::TooLong);
    EXPECT_EQ(validation::folderIssue("relative/path"), FieldIssue::NotAbsolute);
    EXPECT_EQ(validation::folderIssue("~user/x"), FieldIssue::NotAbsolute);
    EXPECT_EQ(validation::folderIssue("/a//b"), FieldIssue::UnsafeComponent);
    EXPECT_EQ(validation::folderIssue("/a/../b"), FieldIssue::UnsafeComponent);
    EXPECT_EQ(validation::folderIssue("~/.."), FieldIssue::UnsafeComponent);
    EXPECT_FALSE(validation::folderIssue("/a/..b/c"));
    for (const auto* folder : {"/tmp/a b", "/tmp/$(id)", "~/x;y", "/tmp/a'b", "/tmp/\"x\"", "/tmp/a*", "/tmp/é"}) {
        EXPECT_EQ(validation::folderIssue(folder), FieldIssue::ForbiddenCharacter) << folder;
    }
}

TEST(RemoteHostSettings, IdsAreUpperCaseUuidsAndNeverLocal) {
    EXPECT_TRUE(validation::isValidId(kId1));
    EXPECT_FALSE(validation::isValidId("local"));
    EXPECT_FALSE(validation::isValidId("8c0d6b0a-1111-4222-8333-444455556666"));
    EXPECT_FALSE(validation::isValidId("8C0D6B0A11114222833344445555666600"));
    EXPECT_FALSE(validation::isValidId(""));
    const auto id = validation::newId();
    EXPECT_TRUE(validation::isValidId(id));
    EXPECT_NE(id, validation::newId());
}

TEST(RemoteHostSettings, NamesAreTrimmedCleanedAndLimited) {
    EXPECT_EQ(validation::cleanName(std::string("  Dev box \n")), "Dev box");
    EXPECT_EQ(validation::cleanName(std::string("a\x01" "b")), "ab");
    EXPECT_EQ(validation::cleanName(std::string("   ")), std::nullopt);
    EXPECT_EQ(validation::cleanName(std::nullopt), std::nullopt);
    EXPECT_EQ(validation::cleanName(std::string(40, 'x')), std::string(32, 'x'));
    EXPECT_EQ(validation::cleanName(std::string("伺服器")), "伺服器");
}

TEST(RemoteHostSettings, AutomaticNamesAreTheHostPart) {
    EXPECT_EQ(validation::automaticName("devbox"), "devbox");
    EXPECT_EQ(validation::automaticName("me@devbox"), "devbox");
    EXPECT_EQ(validation::automaticName("ssh://me@devbox:2222"), "devbox");
    EXPECT_EQ(validation::automaticName("SSH://devbox/"), "devbox");
    EXPECT_EQ(validation::automaticName("ssh://[fe80::1]:22"), "fe80::1");
    EXPECT_EQ(validation::automaticName("a@b@host"), "host");
    EXPECT_EQ(validation::automaticName("host:22"), "host:22");
    EXPECT_EQ(validation::automaticName("me@"), "me@");
}

TEST(RemoteHostSettings, DuplicateAutomaticNamesGetCounters) {
    HostsDocument doc({Host::make(kId1, "me@devbox"), Host::make(kId2, "you@devbox"), Host::make(idNumber(3), "devbox", std::string("devbox (2)"))});
    const auto names = doc.displayNames();
    EXPECT_EQ(names.at(kId1), "devbox");
    EXPECT_EQ(names.at(kId2), "devbox (3)");
    EXPECT_EQ(names.at(idNumber(3)), "devbox (2)");
}

TEST(RemoteHostSettings, AtMostEightHostsAndAddRefusesReservedOrTakenIds) {
    HostsDocument doc;
    for (int i = 0; i < 8; ++i) EXPECT_TRUE(doc.add(Host::make(idNumber(i), "h" + std::to_string(i))));
    EXPECT_FALSE(doc.canAdd());
    EXPECT_FALSE(doc.add(Host::make(idNumber(9), "h9")));
    HostsDocument small;
    EXPECT_FALSE(small.add(Host::make("local", "x")));
    EXPECT_TRUE(small.add(Host::make(kId1, "x")));
    EXPECT_FALSE(small.add(Host::make(kId1, "y")));
    std::vector<Host> many;
    for (int i = 0; i < 10; ++i) many.push_back(Host::make(idNumber(i), "h"));
    EXPECT_EQ(HostsDocument(many).hosts().size(), 8u);
}

TEST(RemoteHostSettings, RoundTripKeepsUnknownFields) {
    const std::string text = R"({"future":{"a":[1,2.50,"x"]},"hosts":[{"destination":"devbox","enabled":false,"id":")" + std::string(kId1) +
                             R"(","later":"kept","name":"Box","relayFolder":"/srv/as"}],"version":3})";
    const auto doc = HostsDocument::parse(text);
    ASSERT_TRUE(doc);
    EXPECT_EQ(doc->version(), 3);
    ASSERT_EQ(doc->hosts().size(), 1u);
    const auto& host = doc->hosts()[0];
    EXPECT_EQ(host.destination, "devbox");
    EXPECT_FALSE(host.enabled);
    EXPECT_EQ(host.name, "Box");
    EXPECT_EQ(host.relayFolder, "/srv/as");
    EXPECT_EQ(doc->json(), text);
    auto edited = *doc;
    auto changed = host;
    changed.extra.clear();
    changed.enabled = true;
    EXPECT_TRUE(edited.update(changed));
    EXPECT_NE(edited.json().find(R"("later":"kept")"), std::string::npos);
}

TEST(RemoteHostSettings, DecodingSkipsInvalidHostsAndKeepsInvalidDestinations) {
    const std::string text = R"({"version":1,"hosts":[{"id":"local","destination":"a"},{"destination":"b"},{"id":")" + std::string(kId1) +
                             R"(","destination":"bad host;rm"},{"id":")" + std::string(kId1) + R"(","destination":"dup"},{"id":")" +
                             std::string(kId2) + R"(","destination":7,"enabled":"yes","relayFolder":""}]})";
    const auto doc = HostsDocument::parse(text);
    ASSERT_TRUE(doc);
    ASSERT_EQ(doc->hosts().size(), 2u);
    EXPECT_EQ(doc->hosts()[0].destination, "bad host;rm");
    EXPECT_FALSE(doc->hosts()[0].connectable());
    EXPECT_EQ(doc->hosts()[1].destination, "");
    EXPECT_TRUE(doc->hosts()[1].enabled);
    EXPECT_EQ(doc->hosts()[1].relayFolder, std::nullopt);
    EXPECT_FALSE(HostsDocument::parse("[1]"));
    EXPECT_FALSE(HostsDocument::parse("not json"));
    EXPECT_TRUE(HostsDocument::parse("{}")->hosts().empty());
}

TEST(RemoteHostSettings, EffectiveFolderDefaultsAndDropsTrailingSlash) {
    EXPECT_EQ(Host::make(kId1, "a").effectiveRelayFolder(), "~/.cache/agent-speed");
    EXPECT_EQ(Host::make(kId1, "a", std::nullopt, true, std::string("/srv/x/")).effectiveRelayFolder(), "/srv/x");
    EXPECT_EQ(Host::make(kId1, "a", std::nullopt, true, std::string("/")).effectiveRelayFolder(), "/");
    EXPECT_FALSE(Host::make(kId1, "a", std::nullopt, true, std::string("rel")).connectable());
    EXPECT_EQ(Host::make(kId1, "a").connectionIdentity(), std::make_pair(std::string("a"), std::string("~/.cache/agent-speed")));
}

}  // namespace
