#include <gtest/gtest.h>

#include "agentspeed/quota/bridge.hpp"
#include "quota_test_support.hpp"

// QUOTAS 5.1–5.4: every `bridge`, `bridgeStatus` and posix `commands` fixture case, as text edits and on disk with a
// temporary home, plus the file rules the ports share (numbers verbatim, permissions and links kept, unreadable files).
using namespace agentspeed;
using namespace agentspeed::quota;
using namespace agentspeed::quota::bridge;
using quota_test::section;
namespace fs = std::filesystem;

namespace {

std::optional<std::string> optText(const json::Value& v) { return fixtures::optString(v); }

std::optional<ordered_json::Value> optOrdered(const json::Value& v) {
    if (!v.valid() || v.isNull()) return std::nullopt;
    return ordered_json::parse(fixtures::compactJson(v));
}

std::string fixtureCommand() { return *section("commands")["posix"].at(0).at(1).string(); }

struct Home {
    quota_test::TempDir dir;
    Paths paths = Paths::forData((dir / ".claude" / "settings.json").string(), (dir / "data").string());
    fs::path helper = dir / "app" / "agent-speed-statusline";
    Home() { quota_test::writeFile(helper, "#!/bin/sh\necho helper\n"); }
};

}  // namespace

TEST(QuotaFixtures, PosixCommands) {
    for (const auto& item : section("commands")["posix"].elements()) {
        EXPECT_EQ(posixCommand(*item.at(0).string()), *item.at(1).string());
    }
}

TEST(QuotaFixtures, BridgeEdits) {
    for (const auto& c : section("bridge").elements()) {
        SCOPED_TRACE(*c["name"].string());
        const auto settings = optText(c["settings"]);
        const auto sidecar = optOrdered(c["sidecar"]);
        if (!c["on"].isNull()) {
            const auto edit = turnOnEdit(settings, fixtureCommand(), sidecar);
            EXPECT_EQ(edit.readable ? "on" : "unreadable", *c["on"]["status"].string());
            EXPECT_EQ(edit.settings, optText(c["on"]["settings"]));
            EXPECT_EQ(edit.previous, optOrdered(c["on"]["previous"]));
        }
        if (!c["off"].isNull()) {
            const auto source = c["on"].isNull() ? settings : optText(c["on"]["settings"]);
            const auto previous = c["on"].isNull() ? sidecar : optOrdered(c["on"]["previous"]);
            EXPECT_EQ(turnOffEdit(source, previous), optText(c["off"]["settings"]));
        }
    }
}

TEST(QuotaFixtures, BridgeOnDisk) {
    for (const auto& c : section("bridge").elements()) {
        SCOPED_TRACE(*c["name"].string());
        Home home;
        const Bridge bridge(home.paths, fixtureCommand());
        const auto settings = optText(c["settings"]);
        const auto sidecar = c["sidecar"];
        if (settings) quota_test::writeFile(home.paths.settingsFile, *settings);
        if (!sidecar.isNull()) quota_test::writeFile(home.paths.sidecar(), fixtures::compactJson(sidecar));
        const auto on = c["on"];
        if (!on.isNull()) {
            const auto result = bridge.turnOn(home.helper.string());
            EXPECT_EQ(result == TurnOnResult::On ? "on" : result == TurnOnResult::Unreadable ? "unreadable" : "failed",
                      *on["status"].string());
            const auto written = quota_test::readFile(home.paths.settingsFile);
            EXPECT_EQ(written, on["settings"].isNull() ? settings : optText(on["settings"]));
            const auto kept = quota_test::readFile(home.paths.sidecar());
            const auto expected = optOrdered(on["previous"]);
            EXPECT_EQ(kept ? ordered_json::parse(*kept) : std::nullopt, expected);
            if (result == TurnOnResult::On) {
                EXPECT_EQ(quota_test::readFile(home.paths.helperCopy), quota_test::readFile(home.helper));
                EXPECT_EQ(quota_test::modeOf(home.paths.helperCopy), 0755u);
                const bool wasOurs = !sidecar.isNull();
                if (!wasOurs) {
                    EXPECT_EQ(quota_test::modeOf(home.paths.sidecar()), 0600u);
                }
                EXPECT_EQ(quota_test::readFile(home.paths.backup()), wasOurs ? std::nullopt : settings);
            }
        }
        const auto off = c["off"];
        if (off.isNull()) continue;
        quota_test::writeFile(home.paths.quotaFile(), "{}");
        const auto before = quota_test::readFile(home.paths.settingsFile);
        EXPECT_TRUE(bridge.turnOff());
        EXPECT_EQ(quota_test::readFile(home.paths.settingsFile), off["settings"].isNull() ? before : optText(off["settings"]));
        EXPECT_FALSE(fs::exists(home.paths.sidecar()));
        EXPECT_FALSE(fs::exists(home.paths.backup()));
        EXPECT_FALSE(fs::exists(home.paths.quotaFile()));
    }
}

TEST(QuotaFixtures, BridgeStatus) {
    for (const auto& c : section("bridgeStatus").elements()) {
        const auto settings = optText(c["settings"]);
        const bool quotaFile = c["quotaFile"].isTrue();
        const auto expected = *c["expected"].string();
        EXPECT_EQ(statusId(statusOf(settings, quotaFile)), expected);
        Home home;
        if (settings) quota_test::writeFile(home.paths.settingsFile, *settings);
        if (quotaFile) quota_test::writeFile(home.paths.quotaFile(), "{}");
        EXPECT_EQ(statusId(Bridge(home.paths).status()), expected);
    }
}

TEST(QuotaBridge, PathsFromTheEnvironment) {
    const Environment env = [](const std::string& name) -> std::optional<std::string> {
        if (name == "HOME") return "/home/u";
        if (name == "XDG_DATA_HOME") return "relative/ignored";
        return std::nullopt;
    };
    const auto paths = Paths::forComputer(env);
    EXPECT_EQ(paths.settingsFile, "/home/u/.claude/settings.json");
    EXPECT_EQ(paths.helperCopy, "/home/u/.local/share/agent-speed/bin/agent-speed-statusline");
    EXPECT_EQ(paths.quotaFile(), "/home/u/.local/share/agent-speed/quota/claude-cli.json");
    EXPECT_EQ(paths.sidecar(), "/home/u/.local/share/agent-speed/quota/claude-statusline-previous.json");
    EXPECT_EQ(paths.backup(), "/home/u/.local/share/agent-speed/quota/claude-settings.backup.json");
    const Environment custom = [](const std::string& name) -> std::optional<std::string> {
        if (name == "HOME") return "/home/u";
        if (name == "XDG_DATA_HOME") return "/data";
        if (name == "CLAUDE_CONFIG_DIR") return "~/cc";
        return std::nullopt;
    };
    EXPECT_EQ(Paths::forComputer(custom).settingsFile, "/home/u/cc/settings.json");
    EXPECT_EQ(Paths::forComputer(custom).helperCopy, "/data/agent-speed/bin/agent-speed-statusline");
    EXPECT_EQ(Bridge(Paths::forData("/s.json", "/home/o'neil/d")).command(), "'/home/o'\\''neil/d/bin/agent-speed-statusline'");
}

TEST(QuotaBridge, NumbersOrderAndUnicodeStayAsWritten) {
    Home home;
    const std::string original = "{\"b\": 1.50, \"a\": [1e3, -0, 2E-7], \"statusLine\": {\"type\": \"command\", \"command\": "
                                 "\"x\", \"padding\": 2.0}, \"name\": \"漢字 \\u00e9\\n\\u001f\", \"b\": 3}";
    quota_test::writeFile(home.paths.settingsFile, original);
    const Bridge bridge(home.paths, std::string("'/h/agent-speed-statusline'"));
    ASSERT_EQ(bridge.turnOn(home.helper.string()), TurnOnResult::On);
    EXPECT_EQ(*quota_test::readFile(home.paths.settingsFile),
              "{\n  \"b\": 3,\n  \"a\": [\n    1e3,\n    -0,\n    2E-7\n  ],\n  \"statusLine\": {\n    \"type\": \"command\",\n"
              "    \"command\": \"'/h/agent-speed-statusline'\",\n    \"padding\": 2.0\n  },\n  \"name\": \"漢字 é\\n\\u001f\"\n}\n");
    EXPECT_EQ(*quota_test::readFile(home.paths.backup()), original);
    EXPECT_TRUE(bridge.turnOff());
    EXPECT_EQ(*quota_test::readFile(home.paths.settingsFile),
              "{\n  \"b\": 3,\n  \"a\": [\n    1e3,\n    -0,\n    2E-7\n  ],\n  \"statusLine\": {\n    \"type\": \"command\",\n"
              "    \"command\": \"x\",\n    \"padding\": 2.0\n  },\n  \"name\": \"漢字 é\\n\\u001f\"\n}\n");
}

TEST(QuotaBridge, ByteOrderMarkAndInvalidUtf8AreUnreadable) {
    for (const std::string& text : {std::string("\xEF\xBB\xBF{}"), std::string("{\"a\": \"\xC3\x28\"}"), std::string("{\"a\": 1} x")}) {
        Home home;
        quota_test::writeFile(home.paths.settingsFile, text);
        const Bridge bridge(home.paths);
        EXPECT_EQ(bridge.turnOn(home.helper.string()), TurnOnResult::Unreadable);
        EXPECT_EQ(*quota_test::readFile(home.paths.settingsFile), text);
        EXPECT_EQ(bridge.status(), Status::Unreadable);
        EXPECT_FALSE(fs::exists(home.paths.sidecar()));
        EXPECT_FALSE(fs::exists(home.paths.helperCopy));
    }
}

TEST(QuotaBridge, PermissionsAndLinksAreKept) {
    Home home;
    const auto real = home.dir / "dotfiles" / "claude-settings.json";
    quota_test::writeFile(real, "{\"model\": \"opus\"}\n");
    fs::permissions(real, fs::perms::owner_read | fs::perms::owner_write | fs::perms::group_read);
    fs::create_directories(fs::path(home.paths.settingsFile).parent_path());
    fs::create_symlink(real, home.paths.settingsFile);
    const Bridge bridge(home.paths);
    ASSERT_EQ(bridge.turnOn(home.helper.string()), TurnOnResult::On);
    EXPECT_TRUE(fs::is_symlink(home.paths.settingsFile));
    EXPECT_EQ(quota_test::modeOf(real), 0640u);
    EXPECT_NE(quota_test::readFile(real)->find("agent-speed-statusline"), std::string::npos);
    EXPECT_EQ(bridge.status(), Status::Waiting);
    EXPECT_TRUE(bridge.turnOff());
    EXPECT_EQ(*quota_test::readFile(real), "{\n  \"model\": \"opus\"\n}\n");
    EXPECT_EQ(quota_test::modeOf(real), 0640u);
    // No temporary file is left beside it.
    std::size_t files = 0;
    for (const auto& item : fs::directory_iterator(real.parent_path())) files += item.is_regular_file() ? 1 : 0;
    EXPECT_EQ(files, 1u);
}

TEST(QuotaBridge, ANewSettingsFileIsPrivate) {
    Home home;
    const Bridge bridge(home.paths);
    ASSERT_EQ(bridge.turnOn(home.helper.string()), TurnOnResult::On);
    EXPECT_EQ(quota_test::modeOf(home.paths.settingsFile), 0600u);
    EXPECT_FALSE(fs::exists(home.paths.backup()));
    EXPECT_EQ(bridge.status(), Status::Waiting);
    quota_test::writeFile(home.paths.quotaFile(), "{}");
    EXPECT_EQ(bridge.status(), Status::Ok);
    EXPECT_TRUE(bridge.turnOff());
    EXPECT_EQ(*quota_test::readFile(home.paths.settingsFile), "{}\n");
}

TEST(QuotaBridge, TurningOnAgainAfterTheUserReplacedTheLine) {
    Home home;
    quota_test::writeFile(home.paths.settingsFile, "{\"statusLine\": {\"type\": \"command\", \"command\": \"mine\"}}");
    const Bridge bridge(home.paths);
    ASSERT_EQ(bridge.turnOn(home.helper.string()), TurnOnResult::On);
    quota_test::writeFile(home.paths.settingsFile, "{\"statusLine\": {\"type\": \"command\", \"command\": \"npx ccstatusline\"}}");
    EXPECT_EQ(bridge.status(), Status::Replaced);
    ASSERT_EQ(bridge.turnOn(home.helper.string()), TurnOnResult::On);
    EXPECT_NE(quota_test::readFile(home.paths.sidecar())->find("ccstatusline"), std::string::npos);
    EXPECT_TRUE(bridge.turnOff());
    EXPECT_NE(quota_test::readFile(home.paths.settingsFile)->find("npx ccstatusline"), std::string::npos);
}

TEST(QuotaBridge, TheHelperCopyIsReplacedOnlyWhenItsBytesDiffer) {
    Home home;
    const Bridge bridge(home.paths);
    ASSERT_TRUE(bridge.installHelper(home.helper.string()));
    const auto first = quota_test::inodeOf(home.paths.helperCopy);
    ASSERT_TRUE(bridge.installHelper(home.helper.string()));
    EXPECT_EQ(quota_test::inodeOf(home.paths.helperCopy), first);
    quota_test::writeFile(home.helper, "#!/bin/sh\necho newer\n");
    ASSERT_TRUE(bridge.installHelper(home.helper.string()));
    EXPECT_NE(quota_test::inodeOf(home.paths.helperCopy), first);
    EXPECT_EQ(quota_test::readFile(home.paths.helperCopy), quota_test::readFile(home.helper));
    EXPECT_EQ(quota_test::modeOf(home.paths.helperCopy), 0755u);
    EXPECT_EQ(quota_test::modeOf(fs::path(home.paths.helperCopy).parent_path()), 0700u);
    EXPECT_FALSE(bridge.installHelper((home.dir / "missing").string()));
    EXPECT_EQ(bridge.turnOn((home.dir / "missing").string()), TurnOnResult::Failed);
    EXPECT_FALSE(fs::exists(home.paths.settingsFile));
}
