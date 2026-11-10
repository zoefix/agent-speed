#include <gtest/gtest.h>
#include <unistd.h>

#include <filesystem>
#include <fstream>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/location_entries.hpp"
#include "agentspeed/locations.hpp"
#include "fixture_support.hpp"

// METRICS 2.1: the location lists, their stored form, the editing rules and the roots they name (issue #45).
using namespace agentspeed;
namespace fs = std::filesystem;

namespace {

struct TempDir {
    fs::path path;
    TempDir() {
        std::string pattern = (fs::temp_directory_path() / "agentspeed-lists-XXXXXX").string();
        path = fs::path(::mkdtemp(pattern.data())).lexically_normal();
        // macOS-style /tmp links would make the expected paths differ from the resolved ones.
        path = fs::canonical(path);
    }
    ~TempDir() {
        std::error_code ignored;
        fs::remove_all(path, ignored);
    }
    std::string file(const std::string& name) const { return (path / name).string(); }
    void write(const std::string& name, std::string_view text = "") const {
        fs::create_directories(fs::path(file(name)).parent_path());
        std::ofstream(file(name), std::ios::binary) << text;
    }
    void folder(const std::string& name) const { fs::create_directories(file(name)); }
};

DataLocation at(const std::string& path) { return DataLocation::at(path); }
const DataLocation kAutomatic = DataLocation::automaticEntry();

Environment environmentOf(std::map<std::string, std::string> values) {
    return [values](const std::string& name) -> std::optional<std::string> {
        const auto it = values.find(name);
        return it == values.end() ? std::nullopt : std::optional<std::string>(it->second);
    };
}

// No OpenClaw state, so Codex's automatic entry is its sessions folder alone.
const Environment kIsolated = environmentOf({{"OPENCLAW_STATE_DIR", "/nonexistent/claw"}, {"CODEX_HOME", "/u/.codex"}});

AutomaticKeys keys(std::vector<std::string> values) { return AutomaticKeys{std::move(values)}; }

std::vector<std::string> rootsOf(const SourceLocations& l, RootFamily family) {
    std::vector<std::string> out;
    for (const auto& [f, path] : l.fileRoots()) {
        if (f == family) out.push_back(path);
    }
    return out;
}

}  // namespace

// MARK: - Lists

TEST(LocationLists, EveryListStartsAutomaticOnly) {
    const LocationLists lists;
    EXPECT_EQ(locationListOrder().size(), 19u);
    for (const auto family : locationListOrder()) {
        EXPECT_EQ(lists[family], LocationList{kAutomatic}) << listName(family);
        EXPECT_EQ(listNamed(listName(family)), family);
    }
    EXPECT_TRUE(lists.isAutomaticOnly());
    EXPECT_EQ(familyOf(SourceKind::ClaudeDesktop), RootFamily::Claude);
    EXPECT_EQ(listName(RootFamily::DeepSeek), "dsh");
    EXPECT_EQ(listName(RootFamily::WorkBuddyAi), "workbuddyAi");
    EXPECT_FALSE(listNamed("claudeCode").has_value());
}

TEST(LocationLists, ListsAreNormalized) {
    LocationLists lists;
    lists.set(RootFamily::Codex, {at("/w/codex/sessions/"), kAutomatic, at("/w/codex/./sessions"), at("relative"), at("/x/../y")});
    EXPECT_EQ(lists[RootFamily::Codex], (LocationList{kAutomatic, at("/w/codex/sessions"), at("/y")})) << "automatic first, each once, absolute";
    LocationList many;
    for (int i = 0; i < 12; ++i) many.push_back(at("/w/" + std::to_string(i)));
    lists.set(RootFamily::Codex, many);
    EXPECT_EQ(lists[RootFamily::Codex].size(), LocationLists::kMaxEntries);
    lists.set(RootFamily::Codex, {at("relative")});
    EXPECT_EQ(lists[RootFamily::Codex], LocationList{kAutomatic}) << "an empty list reads the automatic roots";
    EXPECT_TRUE(lists.isAutomaticOnly());
}

TEST(LocationLists, ASingleFolderOfAnEarlierVersionBecomesTheWholeList) {
    const auto lists = LocationLists::fromLegacy({{RootFamily::Codex, "/w/codex/sessions"}, {RootFamily::OpenClaw, "/w/agent/openclaw-agent.sqlite"}});
    EXPECT_EQ(lists[RootFamily::Codex], LocationList{at("/w/codex/sessions")}) << "the automatic entry stays out";
    EXPECT_EQ(lists[RootFamily::OpenClaw], LocationList{at("/w/agent/openclaw-agent.sqlite")});
    EXPECT_EQ(lists[RootFamily::Claude], LocationList{kAutomatic});
    RootSettings single;
    single.droid = "/w/.factory";
    EXPECT_EQ(LocationLists(single)[RootFamily::Droid], LocationList{at("/w/.factory")});
}

// MARK: - Document

TEST(LocationLists, TheDocumentRoundTripsAndLeavesAutomaticOnlyListsOut) {
    LocationLists lists;
    lists.set(RootFamily::Codex, {kAutomatic, at("/Users/me/work/codex/sessions")});
    lists.set(RootFamily::Hermes, {at("/srv/hermes")});
    const auto text = lists.document();
    EXPECT_EQ(text, R"({"lists":{"codex":[{"automatic":true},{"path":"/Users/me/work/codex/sessions"}],"hermes":[{"path":"/srv/hermes"}]},"version":1})");
    const auto decoded = LocationLists::fromDocument(text);
    ASSERT_TRUE(decoded);
    EXPECT_EQ(*decoded, lists);
    EXPECT_EQ(LocationLists().document(), R"({"lists":{},"version":1})");
    EXPECT_TRUE(LocationLists::fromDocument(LocationLists().document())->isAutomaticOnly());
    LocationLists unicode;
    unicode.set(RootFamily::Kimi, {at("/home/我/工作 \"x\"/sessions")});
    EXPECT_EQ(*LocationLists::fromDocument(unicode.document()), unicode);
}

TEST(LocationLists, DecodingIsTolerant) {
    const auto json = R"({"version":7,"future":true,"lists":{
      "codex":[{"path":"relative"},{"kind":"other"},{"automatic":false},{"path":"/a"},{"path":"/a/"},{"automatic":true}],
      "claude":"not a list",
      "newSource":[{"path":"/n"}]
    }})";
    const auto decoded = LocationLists::fromDocument(json);
    ASSERT_TRUE(decoded);
    EXPECT_EQ((*decoded)[RootFamily::Codex], (LocationList{kAutomatic, at("/a")}));
    EXPECT_EQ((*decoded)[RootFamily::Claude], LocationList{kAutomatic});
    EXPECT_NE(decoded->document().find(R"("newSource":[{"path":"/n"}])"), std::string::npos) << "a list of an unknown source is kept";
    EXPECT_FALSE(LocationLists::fromDocument("[1]"));
    EXPECT_FALSE(LocationLists::fromDocument("not json"));
    EXPECT_TRUE(LocationLists::fromDocument("{}")->isAutomaticOnly());
}

// MARK: - Editing

TEST(LocationLists, AddingAppendsAndRefusesWhatIsAlreadyRead) {
    LocationLists lists;
    const auto automatic = keys({"/u/.codex/sessions", "/u/.openclaw/agents/a/agent/codex-home/sessions"});
    EXPECT_EQ(lists.add("/w/codex/sessions", RootFamily::Codex, automatic), AddOutcome::Added);
    EXPECT_EQ(lists[RootFamily::Codex], (LocationList{kAutomatic, at("/w/codex/sessions")}));
    EXPECT_EQ(lists.add("/w/codex/sessions/", RootFamily::Codex, automatic), AddOutcome::AlreadyListed);
    EXPECT_EQ(lists.add("/u/.codex/sessions", RootFamily::Codex, automatic), AddOutcome::AlreadyAutomatic);
    EXPECT_EQ(lists.add("/u/.openclaw/agents/a/agent/codex-home/sessions", RootFamily::Codex, automatic), AddOutcome::AlreadyAutomatic)
        << "OpenClaw's Codex homes are automatic Codex roots";
    EXPECT_EQ(lists.add("relative", RootFamily::Codex, automatic), AddOutcome::Invalid);
    EXPECT_EQ(lists[RootFamily::Codex].size(), 2u);
}

TEST(LocationLists, TheDefaultLocationBringsTheAutomaticEntryBack) {
    auto lists = LocationLists::fromLegacy({{RootFamily::Codex, "/w/codex/sessions"}});
    EXPECT_EQ(lists.add("/u/.codex/sessions", RootFamily::Codex, keys({"/u/.codex/sessions"})), AddOutcome::RestoredAutomatic);
    EXPECT_EQ(lists[RootFamily::Codex], (LocationList{kAutomatic, at("/w/codex/sessions")})) << "the automatic entry comes back first";
}

TEST(LocationLists, WhereTheAutomaticSetIsWiderTheDefaultIsAddedAsAFolder) {
    for (const auto family : {RootFamily::Omp, RootFamily::Antigravity, RootFamily::Pi, RootFamily::OpenClaw}) {
        auto lists = LocationLists::fromLegacy({{family, "/w/other"}});
        EXPECT_EQ(lists.add("/u/default", family, keys({"/u/default", "/u/profile"})), AddOutcome::Added) << listName(family);
        EXPECT_EQ(lists[family], (LocationList{at("/w/other"), at("/u/default")}));
    }
    std::vector<RootFamily> wider;
    for (const auto family : locationListOrder()) {
        if (automaticIsWiderThanDefault(family)) wider.push_back(family);
    }
    EXPECT_EQ(wider, (std::vector<RootFamily>{RootFamily::Omp, RootFamily::Antigravity, RootFamily::Pi, RootFamily::OpenClaw}));
}

TEST(LocationLists, AtMostEightEntriesAndAtLeastOne) {
    LocationLists lists;
    const auto automatic = keys({"/u/.dsh/sessions"});
    for (std::size_t i = 1; i < LocationLists::kMaxEntries; ++i) {
        EXPECT_EQ(lists.add("/w/" + std::to_string(i), RootFamily::DeepSeek, automatic), AddOutcome::Added);
    }
    EXPECT_EQ(lists[RootFamily::DeepSeek].size(), 8u);
    EXPECT_FALSE(lists.canAdd(RootFamily::DeepSeek));
    EXPECT_EQ(lists.add("/w/9", RootFamily::DeepSeek, automatic), AddOutcome::Full);
    lists.remove(RootFamily::DeepSeek, kAutomatic);
    EXPECT_EQ(lists.add("/u/.dsh/sessions", RootFamily::DeepSeek, automatic), AddOutcome::RestoredAutomatic) << "below the limit again";
    EXPECT_TRUE(lists.canReset(RootFamily::DeepSeek));
    lists.reset(RootFamily::DeepSeek);
    EXPECT_EQ(lists[RootFamily::DeepSeek], LocationList{kAutomatic});
    EXPECT_FALSE(lists.canReset(RootFamily::DeepSeek));
    EXPECT_FALSE(lists.canRemove(RootFamily::DeepSeek));
    lists.remove(RootFamily::DeepSeek, kAutomatic);
    EXPECT_EQ(lists[RootFamily::DeepSeek], LocationList{kAutomatic}) << "the only entry stays";
}

TEST(LocationLists, RemovingTheAutomaticEntryKeepsTheFolders) {
    LocationLists lists;
    lists.set(RootFamily::Claude, {kAutomatic, at("/w/a"), at("/w/b")});
    lists.remove(RootFamily::Claude, at("/w/a"));
    lists.remove(RootFamily::Claude, kAutomatic);
    EXPECT_EQ(lists[RootFamily::Claude], LocationList{at("/w/b")});
    EXPECT_FALSE(lists.canRemove(RootFamily::Claude));
}

TEST(LocationLists, LocationsThatNameTheSameRootAreTheSame) {
    TempDir directory;
    const auto root = directory.path.string();
    directory.write(".factory/sessions/keep");
    directory.write("oc/opencode.db");
    directory.write("z/.zcode/cli/db/db.sqlite");
    directory.write("claw/agents/main/agent/openclaw-agent.sqlite");
    const std::vector<std::tuple<RootFamily, std::string, std::string>> cases = {
        {RootFamily::WorkBuddy, "/w/.workbuddy", "/w/.workbuddy/projects"},
        {RootFamily::WorkBuddyAi, "/w/.workbuddy-ai/", "/w/.workbuddy-ai/projects"},
        {RootFamily::Kimi, "/w/.kimi-code", "/w/.kimi-code/sessions"},
        {RootFamily::GrokBuild, "/w/.grok", "/w/.grok/sessions"},
        {RootFamily::Antigravity, "/w/.gemini/antigravity-cli", "/w/.gemini/antigravity-cli/conversations"},
        {RootFamily::Droid, root + "/.factory", root + "/.factory/sessions"},
        {RootFamily::OpenCode, root + "/oc", root + "/oc/opencode.db"},
        {RootFamily::ZCode, root + "/z", root + "/z/.zcode/cli/db/db.sqlite"},
        {RootFamily::ZCode, root + "/z/.zcode/cli/db", root + "/z/.zcode/cli/db/db.sqlite"},
        {RootFamily::OpenClaw, root + "/claw/agents/main/agent", root + "/claw/agents/main/agent/openclaw-agent.sqlite"},
    };
    for (const auto& [family, first, second] : cases) {
        SCOPED_TRACE(first);
        LocationLists lists;
        EXPECT_EQ(lists.add(first, family, keys({"/elsewhere"})), AddOutcome::Added);
        EXPECT_EQ(lists.add(second, family, keys({"/elsewhere"})), AddOutcome::AlreadyListed);
    }
    LocationLists different;
    EXPECT_EQ(different.add("/w/.hermes", RootFamily::Hermes, keys({"/elsewhere"})), AddOutcome::Added);
    EXPECT_EQ(different.add("/w/.hermes/logs", RootFamily::Hermes, keys({"/elsewhere"})), AddOutcome::Added)
        << "a home and its logs folder differ: the home has profiles";
}

TEST(LocationLists, ASymlinkToAListedFolderIsTheSameLocation) {
    TempDir directory;
    directory.folder("real/sessions");
    fs::create_directory_symlink(directory.file("real"), directory.file("link"));
    LocationLists lists;
    EXPECT_EQ(lists.add(directory.file("real/sessions"), RootFamily::Codex, keys({"/elsewhere"})), AddOutcome::Added);
    EXPECT_EQ(lists.add(directory.file("link/sessions"), RootFamily::Codex, keys({"/elsewhere"})), AddOutcome::AlreadyListed);
}

TEST(LocationLists, AutomaticKeysFollowTheEnvironment) {
    const auto environment = environmentOf({{"CODEX_HOME", "/nonexistent/u/.codex"}, {"OPENCLAW_STATE_DIR", "/nonexistent/claw"}});
    const LocationLists lists;
    EXPECT_EQ(automaticKeys(RootFamily::Codex, lists, environment, "/nonexistent/h").keys, std::vector<std::string>{"/nonexistent/u/.codex/sessions"});
    EXPECT_EQ(automaticKeys(RootFamily::OpenClaw, lists, environment, "/nonexistent/h").keys, std::vector<std::string>{"/nonexistent/claw"});
    EXPECT_EQ(automaticKeys(RootFamily::Hermes, lists, environment, "/nonexistent/h").keys.front(), "/nonexistent/h/.hermes");
    EXPECT_EQ(automaticKeys(RootFamily::OpenCode, lists, environment, "/nonexistent/h").keys,
              std::vector<std::string>{"/nonexistent/h/.local/share/opencode/opencode.db"});
    EXPECT_EQ(automaticKeys(RootFamily::WorkBuddy, lists, environment, "/nonexistent/h").keys,
              std::vector<std::string>{"/nonexistent/h/.workbuddy/projects"});
    EXPECT_EQ(automaticKeys(RootFamily::Antigravity, lists, environment, "/nonexistent/h").keys.front(),
              "/nonexistent/h/.gemini/antigravity-cli/conversations");
}

// MARK: - Roots

TEST(LocationLists, ASourceReadsTheUnionOfItsEntriesInListOrder) {
    LocationLists lists;
    lists.set(RootFamily::Codex, {kAutomatic, at("/w/codex/sessions"), at("/u/.codex/sessions/"), at("/w/b")});
    const auto both = SourceLocations::resolve(lists, kIsolated, "/u");
    EXPECT_EQ(rootsOf(both, RootFamily::Codex), (std::vector<std::string>{"/u/.codex/sessions", "/w/codex/sessions", "/w/b"}))
        << "a root named twice is read once";
    LocationLists without;
    without.set(RootFamily::Codex, {at("/w/a"), at("/w/b")});
    const auto folders = SourceLocations::resolve(without, kIsolated, "/u");
    EXPECT_EQ(rootsOf(folders, RootFamily::Codex), (std::vector<std::string>{"/w/a", "/w/b"}));
    EXPECT_FALSE(folders.readsSame(both));
    EXPECT_TRUE(SourceLocations::resolve(lists, kIsolated, "/u").readsSame(both)) << "the same lists: nothing to read again";
}

TEST(LocationLists, EachUserLocationNamesItsSourcesRoot) {
    LocationLists lists;
    lists.set(RootFamily::WorkBuddy, {kAutomatic, at("/w/.workbuddy")});
    lists.set(RootFamily::Kimi, {at("/w/.kimi-code"), at("/w/other/sessions")});
    lists.set(RootFamily::Antigravity, {at("/w/.gemini/antigravity-ide/conversations"), at("/w/.gemini/antigravity-backup")});
    lists.set(RootFamily::Droid, {kAutomatic, at("/w/droid")});
    const auto l = SourceLocations::resolve(lists, kIsolated, "/u");
    EXPECT_EQ(rootsOf(l, RootFamily::WorkBuddy), (std::vector<std::string>{"/u/.workbuddy/projects", "/w/.workbuddy/projects"}));
    EXPECT_EQ(rootsOf(l, RootFamily::Kimi), (std::vector<std::string>{"/w/.kimi-code/sessions", "/w/other/sessions"}));
    EXPECT_EQ(rootsOf(l, RootFamily::Antigravity), std::vector<std::string>{"/w/.gemini/antigravity-ide/conversations"})
        << "a *-backup store is never read";
    EXPECT_EQ(rootsOf(l, RootFamily::Droid), (std::vector<std::string>{"/u/.factory/sessions", "/w/droid"}));
}

TEST(LocationLists, CodexReadsOpenClawsCodexHomesOnlyWithTheAutomaticEntry) {
    const auto state = (fixtures::fixtureDir() / "openclaw/.openclaw").string();
    const auto environment = environmentOf({{"OPENCLAW_STATE_DIR", state}, {"CODEX_HOME", "/u/.codex"}});
    const auto homes = paths::openClawCodexRoots(paths::openClawDatabases(std::vector<std::string>{state}));
    ASSERT_FALSE(homes.empty());
    LocationLists lists;
    lists.set(RootFamily::Codex, {kAutomatic, at("/w/codex")});
    auto expected = std::vector<std::string>{"/u/.codex/sessions"};
    for (const auto& home : homes) expected.push_back(fsx::normalize(home));
    expected.push_back("/w/codex");
    EXPECT_EQ(rootsOf(SourceLocations::resolve(lists, environment, "/u"), RootFamily::Codex), expected);
    EXPECT_EQ(automaticKeys(RootFamily::Codex, lists, environment, "/u").keys.size(), 1 + homes.size());
    lists.set(RootFamily::Codex, {at("/w/codex")});
    EXPECT_EQ(rootsOf(SourceLocations::resolve(lists, environment, "/u"), RootFamily::Codex), std::vector<std::string>{"/w/codex"});
}

TEST(LocationLists, GeminiSettingsFollowTheRootTheyBelongTo) {
    LocationLists lists;
    lists.set(RootFamily::Gemini, {kAutomatic, at("/w/.gemini/tmp"), at("/w/plain")});
    const auto l = SourceLocations::resolve(lists, kIsolated, "/u");
    EXPECT_EQ(l.geminiRoots, (std::vector<std::string>{"/u/.gemini/tmp", "/w/.gemini/tmp", "/w/plain"}));
    EXPECT_EQ(l.geminiSettings, (std::map<std::string, std::string>{{"/u/.gemini/tmp", "/u/.gemini/settings.json"},
                                                                     {"/w/.gemini/tmp", "/w/.gemini/settings.json"},
                                                                     {"/w/plain", "/w/plain/settings.json"}}));
}

TEST(LocationLists, DatabaseSourcesReadEachDatabaseOnce) {
    TempDir directory;
    directory.write("a/opencode.db");
    directory.write("b/opencode.db");
    fs::create_directory_symlink(directory.file("a"), directory.file("c"));
    LocationLists lists;
    lists.set(RootFamily::OpenCode, {at(directory.file("a")), at(directory.file("a/opencode.db")), at(directory.file("b")), at(directory.file("c"))});
    lists.set(RootFamily::ZCode, {kAutomatic, at("/w/zcode")});
    const auto l = SourceLocations::resolve(lists, kIsolated, "/u");
    EXPECT_EQ(l.openCodeRoots, (std::vector<std::string>{directory.file("a"), directory.file("b")}))
        << "the folder and its file, and a link to it, are one database";
    EXPECT_EQ(l.zcodeRoots, (std::vector<std::string>{"/u/.zcode/cli/db", "/w/zcode"}));
}

TEST(LocationLists, OpenClawAndHermesReadEveryEntry) {
    LocationLists lists;
    lists.set(RootFamily::OpenClaw, {kAutomatic, at("/w/agent/openclaw-agent.sqlite"), at("/u/.openclaw/")});
    lists.set(RootFamily::Hermes, {kAutomatic, at("/w/hermes/logs"), at("/u/.hermes")});
    const auto l = SourceLocations::resolve(lists, environmentOf({{"OPENCLAW_STATE_DIR", "/u/.openclaw"}}), "/u");
    EXPECT_EQ(l.openClawRoots, (std::vector<std::string>{"/u/.openclaw", "/w/agent/openclaw-agent.sqlite"}));
    EXPECT_EQ(l.hermesRoots, (std::vector<HermesRoot>{{"/u/.hermes", false}, {"/w/hermes/logs", true}}));
}

// MARK: - Settings entries (DESIGN 5)

TEST(LocationLists, EntriesNameWhatTheyReadAndWhetherItExists) {
    TempDir directory;
    const auto home = directory.path.string();
    directory.folder(".gemini/antigravity");
    directory.folder("work/sessions");
    directory.write("claw/agents/a/agent/openclaw-agent.sqlite");
    LocationLists lists;
    lists.set(RootFamily::Codex, {kAutomatic, at(home + "/work/sessions"), at("/nonexistent/sessions")});
    lists.set(RootFamily::OpenClaw, {at(home + "/claw/agents/a/agent/openclaw-agent.sqlite")});
    const auto environment = environmentOf({{"OPENCLAW_STATE_DIR", "/nonexistent/claw"}});
    const auto codex = locationEntries(RootFamily::Codex, lists, environment, home);
    ASSERT_EQ(codex.size(), 3u);
    EXPECT_TRUE(codex[0].location.automatic);
    EXPECT_FALSE(codex[0].exists) << "~/.codex/sessions is missing";
    EXPECT_FALSE(codex[0].reveal);
    EXPECT_TRUE(codex[1].exists);
    EXPECT_EQ(codex[1].reveal, home + "/work/sessions");
    EXPECT_FALSE(codex[2].exists);
    EXPECT_EQ(abbreviatedPath(codex[1].shown.front().first, home), "~/work/sessions");
    EXPECT_EQ(abbreviatedPath("/nonexistent/sessions", home), "/nonexistent/sessions");
    const auto antigravity = locationEntries(RootFamily::Antigravity, lists, environment, home);
    ASSERT_EQ(antigravity.size(), 1u);
    ASSERT_EQ(antigravity[0].shown.size(), 1u) << "the stores found";
    EXPECT_EQ(antigravity[0].shown[0].first, home + "/.gemini/antigravity");
    EXPECT_TRUE(antigravity[0].exists);
    EXPECT_EQ(automaticShown(RootFamily::Antigravity, environment, "/nonexistent").size(), 3u) << "all three when none is found";
    const auto claw = locationEntries(RootFamily::OpenClaw, lists, environment, home);
    ASSERT_EQ(claw.size(), 1u);
    EXPECT_TRUE(claw[0].isFile);
    EXPECT_TRUE(claw[0].exists);
    EXPECT_EQ(automaticShown(RootFamily::OpenClaw, environment, home), std::vector<std::string>{"/nonexistent/claw"});
    EXPECT_EQ(automaticShown(RootFamily::Omp, environment, "/h"), std::vector<std::string>{"/h/.omp/agent/sessions"}) << "omp: its default only";
}
