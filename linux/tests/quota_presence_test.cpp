#include <gtest/gtest.h>

#include "agentspeed/quota/paths.hpp"
#include "agentspeed/quota/presence.hpp"
#include "quota_test_support.hpp"

// QUOTAS 6: running sources from a process listing (METRICS 10.5 for every source) and the signed-in markers.
using namespace agentspeed;
using namespace agentspeed::quota;
namespace fs = std::filesystem;

namespace {

traffic::Process process(std::int64_t pid, std::int64_t ppid, std::string exe, std::vector<std::string> args = {}) {
    traffic::Process p;
    p.pid = pid;
    p.ppid = ppid;
    p.start = pid;
    p.exe = std::move(exe);
    p.args = std::move(args);
    if (p.args.empty()) p.args.push_back(p.exe);
    return p;
}

traffic::Env linuxEnv() {
    traffic::Env env;
    env.platform = traffic::Platform::Linux;
    env.home = "/home/u";
    return env;
}

}  // namespace

TEST(QuotaPresence, AgentProcessesOfEverySourceRun) {
    RunningSources running(linuxEnv());
    const std::vector<traffic::Process> listing = {
        process(1, 0, "/usr/lib/systemd/systemd"),
        process(10, 1, "/usr/bin/codex"),
        process(11, 10, "/usr/bin/bash", {"bash", "-lc", "make"}),
        process(20, 1, "/usr/bin/node", {"node", "/usr/lib/node_modules/@anthropic-ai/claude-code/cli.js"}),
        process(30, 1, "/home/u/.local/share/claude/versions/2.1.321/claude"),
        process(40, 1, "/usr/bin/node", {"node", "/usr/lib/node_modules/@google/gemini-cli/dist/index.js"}),
        process(50, 1, "/opt/Claude/claude-desktop"),
    };
    EXPECT_TRUE(running.update(listing));
    EXPECT_EQ(running.running(), (std::set<SourceKind>{SourceKind::Codex, SourceKind::ClaudeCode, SourceKind::Gemini}));
    // QUOTAS 6 names no Linux path for Claude Desktop's main process.
    EXPECT_FALSE(running.runs(SourceKind::ClaudeDesktop));
    EXPECT_FALSE(running.claudeDesktopStarted());
    EXPECT_FALSE(running.update(listing));
    std::vector<traffic::Process> fewer(listing.begin(), listing.begin() + 2);
    EXPECT_TRUE(running.update(fewer));
    EXPECT_EQ(running.running(), std::set<SourceKind>{SourceKind::Codex});
    EXPECT_TRUE(running.update({}));
    EXPECT_TRUE(running.running().empty());
}

TEST(QuotaPresence, DesktopAppsOnMacOS) {
    traffic::Env env;
    env.platform = traffic::Platform::MacOS;
    env.home = "/Users/u";
    RunningSources running(env);
    const std::vector<traffic::Process> listing = {
        process(5, 1, "/Applications/Claude.app/Contents/MacOS/Claude"),
        process(6, 5, "/Applications/Claude.app/Contents/Frameworks/Claude Helper.app/Contents/MacOS/Claude Helper"),
        process(7, 1, "/Applications/Codex.app/Contents/MacOS/Codex"),
    };
    EXPECT_TRUE(running.update(listing));
    EXPECT_TRUE(running.runs(SourceKind::ClaudeDesktop));
    EXPECT_TRUE(running.runs(SourceKind::Codex));
    EXPECT_FALSE(running.runs(SourceKind::ClaudeCode));
    EXPECT_TRUE(running.claudeDesktopStarted());
    running.update(listing);
    EXPECT_FALSE(running.claudeDesktopStarted());
}

TEST(QuotaPresence, SignedInMarkers) {
    EXPECT_TRUE(signedInMarker(SignInKind::Codex, R"({"auth_mode": "chatgpt", "OPENAI_API_KEY": null})"));
    EXPECT_TRUE(signedInMarker(SignInKind::Codex, R"({"tokens": {"id_token": "x"}, "last_refresh": "y"})"));
    EXPECT_FALSE(signedInMarker(SignInKind::Codex, R"({"auth_mode": "apikey", "OPENAI_API_KEY": "sk-x"})"));
    EXPECT_FALSE(signedInMarker(SignInKind::Codex, R"({"tokens": null})"));
    EXPECT_FALSE(signedInMarker(SignInKind::Codex, R"([{"tokens": {}}])"));
    EXPECT_FALSE(signedInMarker(SignInKind::Codex, "{\"tokens\": {}"));
    EXPECT_TRUE(signedInMarker(SignInKind::ClaudeCode, R"({"numStartups": 3, "oauthAccount": {"accountUuid": "u"}})"));
    EXPECT_FALSE(signedInMarker(SignInKind::ClaudeCode, R"({"numStartups": 3, "oauthAccount": null})"));
    EXPECT_FALSE(signedInMarker(SignInKind::ClaudeCode, R"({"auth_mode": "chatgpt"})"));
}

TEST(QuotaPresence, MarkerFilesAreReadAtMostOnceAMinuteAndOnChange) {
    quota_test::TempDir home;
    const auto file = home / ".codex" / "auth.json";
    SignInMarker marker(SignInKind::Codex, file.string());
    EXPECT_FALSE(marker.refresh(0, true));
    EXPECT_EQ(marker.reads(), 0);
    quota_test::writeFile(file, R"({"auth_mode": "chatgpt"})");
    quota_test::setMtimeMs(file, 1'000'000);
    EXPECT_TRUE(marker.refresh(1000, true));
    EXPECT_EQ(marker.reads(), 1);
    quota_test::writeFile(file, R"({"auth_mode": "apikey"})");
    quota_test::setMtimeMs(file, 2'000'000);
    EXPECT_TRUE(marker.refresh(30'000, true)) << "changed, but read less than a minute ago";
    EXPECT_EQ(marker.reads(), 1);
    EXPECT_FALSE(marker.refresh(61'000, true));
    EXPECT_EQ(marker.reads(), 2);
    EXPECT_FALSE(marker.refresh(300'000, true));
    EXPECT_EQ(marker.reads(), 2) << "unchanged: not read again";
    // Disabled: nothing is read and it is not signed in.
    quota_test::writeFile(file, R"({"tokens": {}})");
    quota_test::setMtimeMs(file, 3'000'000);
    EXPECT_FALSE(marker.refresh(400'000, false));
    EXPECT_EQ(marker.reads(), 2);
    EXPECT_TRUE(marker.refresh(401'000, true));
    EXPECT_EQ(marker.reads(), 3);
    fs::remove(file);
    EXPECT_FALSE(marker.refresh(402'000, true));
}

TEST(QuotaPresence, MarkerPathsAndDesktopSignIn) {
    const Environment plain = [](const std::string& name) -> std::optional<std::string> {
        return name == "HOME" ? std::optional<std::string>("/home/u") : std::nullopt;
    };
    EXPECT_EQ(codexAuthFile(plain), "/home/u/.codex/auth.json");
    EXPECT_EQ(claudeAccountFile(plain), "/home/u/.claude.json");
    EXPECT_EQ(desktopCacheFolder(plain), "/home/u/.config/Claude/Cache/Cache_Data");
    EXPECT_EQ(dataFolder(plain), "/home/u/.local/share/agent-speed");
    const Environment custom = [](const std::string& name) -> std::optional<std::string> {
        if (name == "HOME") return "/home/u";
        if (name == "CODEX_HOME") return "/srv/codex";
        if (name == "CLAUDE_CONFIG_DIR") return "/srv/claude";
        if (name == "XDG_CONFIG_HOME") return "/cfg";
        if (name == "XDG_DATA_HOME") return "";
        return std::nullopt;
    };
    EXPECT_EQ(codexAuthFile(custom), "/srv/codex/auth.json");
    EXPECT_EQ(claudeAccountFile(custom), "/srv/claude/.claude.json");
    EXPECT_EQ(desktopCacheFolder(custom), "/cfg/Claude/Cache/Cache_Data");
    EXPECT_EQ(dataFolder(custom), "/home/u/.local/share/agent-speed");

    quota::Snapshot snapshot;
    snapshot.observedMs = 1'000'000;
    EXPECT_TRUE(desktopSignedIn(snapshot, 1'000'000 + kDesktopSignedInMs));
    EXPECT_FALSE(desktopSignedIn(snapshot, 1'000'001 + kDesktopSignedInMs));
    EXPECT_FALSE(desktopSignedIn(std::nullopt, 0));
}

// Regression (2026-11-11): Claude Desktop fetches its usage only while its window is in front, so after hours in the
// background its snapshot is hours old and it is still signed in; a day old, it is not.
TEST(QuotaPresence, DesktopLeftInTheBackgroundStaysSignedIn) {
    constexpr std::int64_t kMinute = 60'000;
    const std::int64_t now = 100 * 24 * 60 * kMinute;
    quota::Snapshot snapshot;
    for (std::int64_t minutes : {46LL, 66LL, 3 * 60LL, 23 * 60LL, 24 * 60LL}) {
        snapshot.observedMs = now - minutes * kMinute;
        EXPECT_TRUE(desktopSignedIn(snapshot, now)) << minutes << " min old";
    }
    snapshot.observedMs = now - 25 * 60 * kMinute;
    EXPECT_FALSE(desktopSignedIn(snapshot, now));
}
