#include <gtest/gtest.h>

#include <algorithm>

#include "agentspeed/discovery.hpp"
#include "agentspeed/fs_util.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;

namespace {

std::string root() { return fsx::normalize(fixtures::fixtureDir().string()); }

std::vector<std::string> relative(const std::vector<discovery::DiscoveredFile>& files, const std::string& base = root()) {
    std::vector<std::string> out;
    for (const auto& f : files) out.push_back(f.path.substr(base.size() + 1));
    std::sort(out.begin(), out.end());
    return out;
}

std::vector<std::string> expectedList(const char* section, const char* key = "discover") {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "parser-expected.json");
    std::vector<std::string> out;
    for (const auto& v : expected.root()[section][key].elements()) out.push_back(*v.string());
    return out;
}

std::vector<discovery::DiscoveredFile> enumerateAll(const std::vector<std::string>& roots, RootFamily family) {
    std::vector<discovery::DiscoveredFile> all;
    for (const auto& root : roots) {
        auto files = discovery::enumerate(root, family);
        all.insert(all.end(), files.begin(), files.end());
    }
    return all;
}

Environment only(std::string name, std::string value) {
    return [name, value](const std::string& asked) -> std::optional<std::string> {
        if (asked == name) return value;
        return std::nullopt;
    };
}

}  // namespace

TEST(Discovery, GeminiFindsSessionsAtFixedDepths) {
    EXPECT_EQ(relative(discovery::enumerate(root() + "/gemini/tmp", RootFamily::Gemini)), expectedList("gemini"));
}

TEST(Discovery, OmpFindsSessionsAndSubagentsOfEveryProfile) {
    const auto roots = paths::ompRoots(only("PI_CONFIG_DIR", "omp"), root());
    EXPECT_EQ(relative(enumerateAll(roots, RootFamily::Omp)), expectedList("omp"));
}

TEST(Discovery, PiFindsTheDefaultAndCustomSessionFolders) {
    const auto roots = paths::piRoots(only("PI_CODING_AGENT_DIR", root() + "/pi/agent"), root());
    EXPECT_EQ(relative(enumerateAll(roots, RootFamily::Pi)), expectedList("pi"));
}

TEST(Discovery, AntigravityFindsUuidDatabasesInEveryStore) {
    std::vector<std::string> conversations;
    for (const auto& store : paths::antigravityStores(root() + "/antigravity", std::nullopt)) {
        conversations.push_back(paths::antigravityConversationsOf(store));
    }
    EXPECT_EQ(relative(enumerateAll(conversations, RootFamily::Antigravity)), expectedList("antigravity"));
    EXPECT_TRUE(paths::antigravityStores(root() + "/antigravity", root() + "/antigravity/antigravity-backup").empty());
}

TEST(Discovery, WorkBuddyFindsMainSessionsAndTheirSubagentsOnly) {
    auto files = discovery::enumerate(root() + "/workbuddy/.workbuddy/projects", RootFamily::WorkBuddy);
    auto ai = discovery::enumerate(root() + "/workbuddy/.workbuddy-ai/projects", RootFamily::WorkBuddyAi);
    files.insert(files.end(), ai.begin(), ai.end());
    EXPECT_EQ(relative(files), expectedList("workbuddy"));
}

TEST(Discovery, DroidFindsSessionsInTheRootAndOneFolderDown) {
    EXPECT_EQ(relative(discovery::enumerate(root() + "/droid/.factory/sessions", RootFamily::Droid)), expectedList("droid"));
}

TEST(Discovery, OpenClawFindsAgentDatabasesAndTheirCodexHomes) {
    const auto folders = paths::openClawStateFolders([](const std::string&) { return std::nullopt; }, root() + "/openclaw");
    ASSERT_EQ(folders.size(), 2u);
    EXPECT_EQ(folders[0], root() + "/openclaw/.openclaw");
    EXPECT_EQ(folders[1], root() + "/openclaw/.openclaw-work");
    std::vector<discovery::DiscoveredFile> databases;
    for (const auto& d : paths::openClawDatabases(folders)) databases.push_back(discovery::DiscoveredFile{d, 0, 0});
    EXPECT_EQ(relative(databases), expectedList("openclaw"));
}

TEST(Discovery, HarnessesFindKimiAgentsAndGrokSessions) {
    EXPECT_EQ(discovery::enumerate(root() + "/kimi/sessions", RootFamily::Kimi).size(), 3u);
    EXPECT_EQ(discovery::enumerate(root() + "/grok/sessions", RootFamily::GrokBuild).size(), 3u);
}

TEST(Discovery, WatcherPathsFollowTheRules) {
    const auto f = root();
    // A path that does not exist (or has a link on the way) is never accepted.
    const auto claude = f + "/claude/projects";
    EXPECT_TRUE(discovery::isAcceptablePath(claude, claude + "/-Users-dev-projects-nova/s-cli/subagents/agent-abc.jsonl", RootFamily::Claude));
    EXPECT_FALSE(discovery::isAcceptablePath(claude, claude + "/-Users-dev-projects-nova/missing.jsonl", RootFamily::Claude));
    EXPECT_FALSE(discovery::isAcceptablePath(claude, f + "/codex/sessions/2026/10/20/rollout-2026-10-20T10-01-00-legacy.jsonl", RootFamily::Claude));
    EXPECT_TRUE(discovery::isAcceptablePath(f + "/codex/sessions", f + "/codex/sessions/2026/10/20/rollout-2026-10-20T10-01-00-legacy.jsonl", RootFamily::Codex));
    const auto dsh = f + "/dsh/sessions";
    EXPECT_TRUE(discovery::isAcceptablePath(dsh, dsh + "/--Users-dev-projects-quasar--/session-dsh-sub/session.v4.jsonl.zstd", RootFamily::DeepSeek));
    EXPECT_FALSE(discovery::isAcceptablePath(dsh, dsh + "/--Users-dev-projects-quasar--/session-dsh-sub/session.v4.jsonl.zstd", RootFamily::Claude));
    for (const auto& file : expectedList("droid")) {
        EXPECT_TRUE(discovery::isAcceptablePath(f + "/droid/.factory/sessions", f + "/" + file, RootFamily::Droid)) << file;
    }
    const auto halo = f + "/droid/.factory/sessions/-C-Users-dev-projects-halo/c3d2e5f4-2d80-4e3c-9f90-4b5c6d7e8f92";
    EXPECT_EQ(discovery::droidTranscriptOfSidecar(f + "/droid/.factory/sessions", halo + ".settings.json"), halo + ".jsonl");
    EXPECT_FALSE(discovery::isAcceptablePath(f + "/droid/.factory/sessions",
                                             f + "/droid/.factory/sessions/-Users-dev-projects-forge/a1f0c3d2-0b6e-4c1a-9d7e-2f3a4b5c6d70.jsonl.tmp",
                                             RootFamily::Droid));
}
