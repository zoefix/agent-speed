// CHARACTERS 1, 2 and 7: manifests, the catalog, archive layout, the zip reader, the store, installing a downloaded
// archive, the automatic download order and the vendor reading. Ported from macOS CharacterPackTests and
// CharacterAutoDownloadTests, plus the Linux zip reader.
#include <gtest/gtest.h>
#include <zlib.h>

#include <set>

#include "agentspeed/character/store.hpp"
#include "agentspeed/character/zip.hpp"
#include "agentspeed/price_book.hpp"
#include "character_support.hpp"
#include "character_zip_support.hpp"

using namespace agentspeed;
using namespace agentspeed::character;
namespace fs = std::filesystem;
using test_zip::ZipFile;
using test_zip::packZip;
using test_zip::manifestJSON;

namespace {

CharacterManifest manifest(const std::string& id = "claude", int packVersion = 1,
                           const std::map<std::string, std::string>& poses = {{"idle", "poses/idle"}},
                           std::optional<std::string> minApp = std::nullopt) {
    return CharacterManifest::decode(manifestJSON(id, packVersion, poses, minApp));
}

// `<root>/<folder>/` with manifest.json, preview.png and a rig.json per pose.
fs::path writePack(const fs::path& root, const std::string& manifestText, const std::string& folder) {
    const auto dir = root / folder;
    tiny_pack::write(dir / "manifest.json", manifestText);
    tiny_pack::write(dir / "preview.png", "\x89PNG");
    const auto m = CharacterManifest::decode(manifestText);
    for (const auto& [band, path] : m.poses) {
        tiny_pack::write(dir / path / "rig.json", R"({"format":"agent-speed-character","version":1})");
    }
    return dir;
}

template <class F>
std::optional<PackFailure> failure(F&& f) {
    try {
        f();
    } catch (const PackFailure& e) {
        return e;
    }
    return std::nullopt;
}

std::string entryJSON(const std::string& id = "claude",
                      const std::string& url = "https://github.com/zoefix/agent-speed/releases/download/characters/claude-1.zip",
                      const std::string& sha = std::string(64, 'a'), std::int64_t size = 10) {
    return R"({"id":")" + id + R"(","packVersion":1,"name":{"zh-Hant":"a","zh-Hans":"a","en":"a","ja":"a"},)"
           R"("vendors":["anthropic"],"accent":"#D97757","size":)" + std::to_string(size) + R"(,"sha256":")" + sha +
           R"(","url":")" + url + "\"}";
}

CharacterCatalog::Entry entryFor(const fs::path& archive, const std::string& id = "claude") {
    CharacterCatalog::Entry e;
    e.id = id;
    e.packVersion = 1;
    e.name = {"a", "a", "a", "a"};
    e.vendors = {"anthropic"};
    e.accent = "#D97757";
    e.size = static_cast<std::int64_t>(fs::file_size(archive));
    e.sha256 = sha256File(archive);
    e.url = "https://github.com/zoefix/agent-speed/releases/download/characters/" + id + "-1.zip";
    return e;
}

AgentRow agent(SourceKind source, std::optional<std::string> model, double rate) {
    AgentRow a;
    a.id = "a" + std::to_string(rate);
    a.source = source;
    a.model = std::move(model);
    a.liveOutput = rate;
    a.phase = Phase::Generating;
    return a;
}

}  // namespace

TEST(CharacterManifest, IdsAreLowercaseLettersDigitsAndDashes) {
    EXPECT_TRUE(isValidCharacterID("claude"));
    EXPECT_TRUE(isValidCharacterID("gpt-5"));
    EXPECT_FALSE(isValidCharacterID("Claude"));
    EXPECT_FALSE(isValidCharacterID("-x"));
    EXPECT_FALSE(isValidCharacterID("../x"));
    EXPECT_FALSE(isValidCharacterID(""));
    EXPECT_FALSE(isValidCharacterID(std::string(65, 'a')));
}

TEST(CharacterManifest, AValidManifestPasses) { EXPECT_NO_THROW(manifest().validate("1.7.0")); }

TEST(CharacterManifest, BadManifestsAreRefused) {
    auto m = manifest();
    m.format = "other";
    EXPECT_EQ(failure([&] { m.validate(std::nullopt); })->kind(), PackError::Format);
    EXPECT_EQ(failure([] { manifest("claude", 1, {{"boost", "poses/boost"}}).validate(std::nullopt); })->kind(),
              PackError::MissingIdle);
    auto unsafe = failure([] { manifest("claude", 1, {{"idle", "../poses"}}).validate(std::nullopt); });
    ASSERT_TRUE(unsafe);
    EXPECT_EQ(unsafe->kind(), PackError::UnsafePath);
    EXPECT_EQ(unsafe->detail(), "../poses");
    auto newer = failure([] { manifest("claude", 1, {{"idle", "poses/idle"}}, "9.0.0").validate("1.7.0"); });
    ASSERT_TRUE(newer);
    EXPECT_EQ(newer->kind(), PackError::NewerApp);
    EXPECT_NO_THROW(manifest("claude", 1, {{"idle", "poses/idle"}}, "9.0.0").validate("0.0.0"));
    EXPECT_EQ(failure([] { (void)CharacterManifest::decode("{}"); })->kind(), PackError::Format);
}

TEST(CharacterManifest, AMissingBandUsesTheNearestLowerOne) {
    const auto m = manifest("claude", 1, {{"idle", "poses/idle"}, {"boost", "poses/boost"}});
    EXPECT_EQ(m.pose(Band::Idle), "poses/idle");
    EXPECT_EQ(m.pose(Band::Cruise), "poses/idle");
    EXPECT_EQ(m.pose(Band::Boost), "poses/boost");
    EXPECT_EQ(m.pose(Band::Redline), "poses/boost");
}

TEST(CharacterManifest, AccentsParse) {
    const auto rgb = CharacterManifest::rgb("#D97757");
    ASSERT_TRUE(rgb);
    EXPECT_NEAR(rgb->r, 217.0 / 255, 1e-9);
    EXPECT_FALSE(CharacterManifest::rgb("D97757"));
    EXPECT_FALSE(CharacterManifest::rgb("#D9775G"));
}

TEST(CharacterManifest, ArtistLinksAreHttpsOnly) {
    EXPECT_EQ(CharacterArtist({"a", "https://example.com/x"}).link(), "https://example.com/x");
    EXPECT_FALSE(CharacterArtist({"a", "http://example.com/x"}).link());
    EXPECT_FALSE(CharacterArtist({"a", "javascript:alert(1)"}).link());
    EXPECT_FALSE(CharacterArtist({"a", std::nullopt}).link());
}

TEST(CharacterCatalog, InvalidAndDuplicateEntriesAreDropped) {
    const std::string json = R"({"format":"agent-speed-character-catalog","version":1,"characters":[)" + entryJSON() + "," +
                             entryJSON() + "," + entryJSON("evil", "https://example.com/x.zip") + "," +
                             entryJSON("short", "https://github.com/x", "abc") + "," +
                             entryJSON("huge", "https://github.com/x", std::string(64, 'a'), (std::int64_t{200} << 20) + 1) +
                             "," + entryJSON("gpt") + "]}";
    const auto catalog = CharacterCatalog::load(json);
    ASSERT_EQ(catalog.characters.size(), 2u);
    EXPECT_EQ(catalog.characters[0].id, "claude");
    EXPECT_EQ(catalog.characters[1].id, "gpt");
}

TEST(CharacterCatalog, OtherFormatsAreRefused) {
    EXPECT_EQ(failure([] { (void)CharacterCatalog::load(R"({"format":"x","version":1,"characters":[]})"); })->kind(),
              PackError::Format);
    EXPECT_EQ(failure([] { (void)CharacterCatalog::load(std::string((1 << 20) + 1, ' ')); })->kind(), PackError::TooLarge);
}

TEST(CharacterCatalog, OnlyReleaseHostsOverHttps) {
    EXPECT_TRUE(CharacterCatalog::permits("https://github.com/a"));
    EXPECT_TRUE(CharacterCatalog::permits("https://objects.githubusercontent.com/a"));
    EXPECT_TRUE(CharacterCatalog::permits("https://release-assets.githubusercontent.com/a?x=1"));
    EXPECT_TRUE(CharacterCatalog::permits("https://GitHub.com:443/a"));
    EXPECT_FALSE(CharacterCatalog::permits("http://github.com/a"));
    EXPECT_FALSE(CharacterCatalog::permits("https://raw.githubusercontent.com/a"));
    EXPECT_FALSE(CharacterCatalog::permits("https://user@github.com/a"));
    EXPECT_FALSE(CharacterCatalog::permits("https://github.com:8443/a"));
    EXPECT_FALSE(CharacterCatalog::permits("https://github.com.evil.example/a"));
}

TEST(CharacterCatalog, ArchiveEntriesMustBeSafeAndHoldAManifest) {
    EXPECT_NO_THROW(pack_layout::validate({{"manifest.json"}, {"poses/idle/rig.json"}, {"poses/"}}));
    auto e = failure([] { pack_layout::validate({{"manifest.json"}, {"../x"}}); });
    ASSERT_TRUE(e);
    EXPECT_EQ(e->kind(), PackError::UnsafePath);
    EXPECT_EQ(e->detail(), "../x");
    EXPECT_EQ(failure([] { pack_layout::validate({{"manifest.json"}, {"/etc/passwd"}}); })->kind(), PackError::UnsafePath);
    EXPECT_EQ(failure([] { pack_layout::validate({{"manifest.json"}, {"a\\b"}}); })->kind(), PackError::UnsafePath);
    EXPECT_EQ(failure([] { pack_layout::validate({{"manifest.json"}, {"link", 0, true}}); })->kind(), PackError::SymbolicLink);
    EXPECT_EQ(failure([] { pack_layout::validate({{"poses/idle/rig.json"}}); })->kind(), PackError::MissingFile);
    EXPECT_EQ(failure([] { pack_layout::validate({{"manifest.json", pack_layout::kMaxExpandedSize}, {"b", 1}}); })->kind(),
              PackError::TooLarge);
}

TEST(CharacterZip, ReadsAndExtractsStoredAndDeflatedEntries) {
    const auto dir = tiny_pack::tempDir("zip");
    const auto archive = packZip().write(dir / "pack.zip");
    ZipArchive zip(archive);
    ASSERT_EQ(zip.entries().size(), 5u);
    EXPECT_EQ(zip.entries()[4].path, "poses/idle/atlas.png");
    EXPECT_EQ(zip.entries()[4].uncompressedSize, 5000u);
    const auto out = dir / "out";
    fs::create_directory(out);
    zip.extractAll(out);
    EXPECT_EQ(fixtures::readText(out / "poses/idle/atlas.png"), std::string(5000, 'x'));
    EXPECT_NO_THROW((void)CharacterStore::manifest(out, std::nullopt));
}

TEST(CharacterZip, RefusesLinksTraversalAndBrokenArchives) {
    const auto dir = tiny_pack::tempDir("zip");
    auto z = packZip();
    z.items.push_back({"escape", "/etc", false, true});
    auto e = failure([&] { ZipArchive(z.write(dir / "link.zip")).extractAll(dir); });
    ASSERT_TRUE(e);
    EXPECT_EQ(e->kind(), PackError::SymbolicLink);

    z = packZip();
    z.items.push_back({"../outside.txt", "x"});
    EXPECT_EQ(failure([&] { ZipArchive(z.write(dir / "dots.zip")).extractAll(dir / "nowhere"); })->kind(), PackError::UnsafePath);
    EXPECT_FALSE(fs::exists(dir / "outside.txt"));

    tiny_pack::write(dir / "junk.zip", "not a zip at all, just some text that is long enough");
    EXPECT_EQ(failure([&] { ZipArchive{dir / "junk.zip"}; })->kind(), PackError::Format);

    // A corrupted payload fails its CRC.
    auto bytes = packZip().bytes();
    const auto at = bytes.find(std::string(1, '\x89') + "PNG");
    bytes[at + 1] = 'Q';
    tiny_pack::write(dir / "crc.zip", bytes);
    const auto out = dir / "crc";
    fs::create_directory(out);
    EXPECT_EQ(failure([&] { ZipArchive(dir / "crc.zip").extractAll(out); })->kind(), PackError::Format);
}

TEST(CharacterStore, InstallListsAndRemoves) {
    const auto root = tiny_pack::tempDir("store");
    const CharacterStore store(root / "characters", std::nullopt, "1.7.0");
    EXPECT_TRUE(store.installed().empty());
    const auto unpacked = writePack(root, manifestJSON(), "incoming");
    const auto installed = store.install(unpacked, "claude");
    EXPECT_EQ(installed.folder.filename(), "claude");
    ASSERT_EQ(store.installed().size(), 1u);
    EXPECT_EQ(store.installed()[0].id(), "claude");
    EXPECT_FALSE(fs::exists(unpacked));
    store.install(writePack(root, manifestJSON("claude", 2), "incoming2"), "claude");
    EXPECT_EQ(store.installed("claude")->manifest.packVersion, 2);
    for (const auto& e : fs::directory_iterator(root / "characters")) EXPECT_EQ(e.path().filename(), "claude");
    store.remove("claude");
    EXPECT_TRUE(store.installed().empty());
    EXPECT_EQ(failure([&] { store.remove("../x"); })->kind(), PackError::InvalidID);
}

TEST(CharacterStore, APackWithAnotherIDIsRefused) {
    const auto root = tiny_pack::tempDir("store");
    const CharacterStore store(root / "characters");
    const auto unpacked = writePack(root, manifestJSON("gpt"), "incoming");
    EXPECT_EQ(failure([&] { store.install(unpacked, "claude"); })->kind(), PackError::Mismatch);
}

TEST(CharacterStore, LinksInsideAPackAreRefused) {
    const auto root = tiny_pack::tempDir("store");
    const CharacterStore store(root / "characters");
    const auto unpacked = writePack(root, manifestJSON(), "incoming");
    fs::create_symlink("/etc", unpacked / "escape");
    auto e = failure([&] { store.install(unpacked, "claude"); });
    ASSERT_TRUE(e);
    EXPECT_EQ(e->kind(), PackError::SymbolicLink);
    EXPECT_EQ(e->detail(), "escape");
}

TEST(CharacterStore, DevelopmentPacksAreListedFirstAndHideInstalledOnes) {
    const auto root = tiny_pack::tempDir("store");
    writePack(root / "dev", manifestJSON("claude", 9), "claude");
    writePack(root / "characters", manifestJSON("claude"), "claude");
    writePack(root / "characters", manifestJSON("gpt"), "gpt");
    writePack(root / "characters", manifestJSON("broken", 1, {{"idle", "missing"}}), "broken");
    fs::remove_all(root / "characters/broken/missing");
    const CharacterStore store(root / "characters", root / "dev");
    const auto packs = store.installed();
    ASSERT_EQ(packs.size(), 2u);
    EXPECT_EQ(packs[0].id(), "claude");
    EXPECT_TRUE(packs[0].isDevelopment);
    EXPECT_EQ(packs[0].manifest.packVersion, 9);
    EXPECT_EQ(packs[1].id(), "gpt");
}

TEST(CharacterStore, AFolderNamedDifferentlyFromItsIDIsIgnored) {
    const auto root = tiny_pack::tempDir("store");
    writePack(root, manifestJSON("gpt"), "claude");
    EXPECT_TRUE(CharacterStore(root).installed().empty());
}

TEST(CharacterInstaller, VerifiesSizeAndChecksumBeforeUnpacking) {
    const auto root = tiny_pack::tempDir("install");
    const CharacterStore store(root / "characters", std::nullopt, "1.7.0");
    const auto archive = packZip().write(root / "dl" / "pack.zip");
    auto entry = entryFor(archive);
    auto wrongSize = entry;
    wrongSize.size += 1;
    EXPECT_EQ(installArchive(store, wrongSize, archive), InstallError::Checksum);
    auto wrongSha = entry;
    wrongSha.sha256 = std::string(64, '0');
    EXPECT_EQ(installArchive(store, wrongSha, archive), InstallError::Checksum);
    EXPECT_TRUE(store.installed().empty());
    auto upper = entry;
    for (auto& c : upper.sha256) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
    EXPECT_EQ(installArchive(store, upper, archive), std::nullopt);
    ASSERT_EQ(store.installed().size(), 1u);
    // Only the installed folder remains: no staging or unpack folders.
    std::vector<std::string> names;
    for (const auto& e : fs::directory_iterator(root / "characters")) names.push_back(e.path().filename().string());
    EXPECT_EQ(names, std::vector<std::string>{"claude"});
}

TEST(CharacterInstaller, RefusesAnArchiveOfAnotherPackOrWithALink) {
    const auto root = tiny_pack::tempDir("install");
    const CharacterStore store(root / "characters");
    const auto other = packZip("gpt").write(root / "gpt.zip");
    EXPECT_EQ(installArchive(store, entryFor(other, "claude"), other), InstallError::InvalidPack);
    auto z = packZip();
    z.items.push_back({"poses/idle/evil", "/etc/passwd", false, true});
    const auto linked = z.write(root / "linked.zip");
    EXPECT_EQ(installArchive(store, entryFor(linked), linked), InstallError::InvalidPack);
    EXPECT_TRUE(store.installed().empty());
    for (const auto& e : fs::directory_iterator(root / "characters")) ADD_FAILURE() << "left behind " << e.path();
}

TEST(CharacterAutoDownload, LiveVendorsComeFirstThenCatalogOrder) {
    CharacterCatalog catalog;
    for (const auto& [id, vendor] : std::vector<std::pair<std::string, std::string>>{
             {"claude", "anthropic"}, {"deepseek", "deepseek"}, {"gemini", "google"}, {"gpt", "openai"}, {"kimi", "moonshot"}}) {
        CharacterCatalog::Entry e;
        e.id = id;
        e.vendors = {vendor};
        catalog.characters.push_back(e);
    }
    EXPECT_EQ(auto_download::queue(catalog, {}, {}, {}, {"openai", "anthropic"}),
              (std::vector<std::string>{"gpt", "claude", "deepseek", "gemini", "kimi"}));
    EXPECT_EQ(auto_download::queue(catalog, {"claude"}, {"gemini"}, {"kimi"}, {"anthropic"}),
              (std::vector<std::string>{"deepseek", "gpt"}));
}

TEST(CharacterAutoDownload, VendorsAreOrderedByLiveRate) {
    VendorReading reading{{{"openai", 40}, {"anthropic", 120}, {"google", 0}, {"moonshot", 40}}, std::nullopt, "moonshot"};
    EXPECT_EQ(reading.vendorsByRate(), (std::vector<std::string>{"anthropic", "moonshot", "openai"}));
}

TEST(CharacterVendors, RatesSumPerVendorAndTheFastestModelIsNamed) {
    const auto prices = pricing::PriceBook::bundled();
    const auto reading = VendorReading::of({agent(SourceKind::ClaudeCode, "claude-opus-4-5", 40),
                                            agent(SourceKind::ClaudeCode, "claude-sonnet-4-5", 30),
                                            agent(SourceKind::Codex, "gpt-5.1-codex", 55), agent(SourceKind::Omp, std::nullopt, 9)},
                                           prices.get());
    EXPECT_EQ(reading.rates.at("anthropic"), 70);
    EXPECT_EQ(reading.rates.at("openai"), 55);
    EXPECT_EQ(reading.rates.size(), 2u);
    EXPECT_EQ(reading.fastestVendor, "openai");
    ASSERT_TRUE(reading.fastestModel);
    EXPECT_NE(*reading.fastestModel, "gpt-5.1-codex");
}

TEST(CharacterVendors, ModelFamiliesNameTheirVendor) {
    const std::vector<std::pair<std::optional<std::string>, std::optional<std::string>>> cases = {
        {"doubao-seed-1.6", "bytedance"}, {"seed-oss-36b", "bytedance"}, {"bytedance/UI-TARS-1.5-7B", "bytedance"},
        {"meta-llama/Llama-3.3-70B-Instruct", "meta"}, {"llama3.2:latest", "meta"}, {"sonar-pro", "perplexity"},
        {"pplx-70b-online", "perplexity"}, {"perplexity/r1-1776", "perplexity"}, {"ERNIE-4.5-Turbo", "baidu"},
        {"hunyuan-t1-latest", "tencent"}, {"rwkv-7-world", "rwkv"}, {"codellama-13b", std::nullopt},
        {"seedream", std::nullopt}, {"jev", std::nullopt}, {std::nullopt, std::nullopt}, {"", std::nullopt},
    };
    for (const auto& [model, vendor] : cases) EXPECT_EQ(vendors::family(model), vendor) << model.value_or("nil");
    const auto reading = VendorReading::of(
        {agent(SourceKind::Omp, "meta-llama/llama-4-maverick", 20), agent(SourceKind::Gemini, "ernie-x1", 8)}, nullptr);
    EXPECT_EQ(reading.rates, (std::map<std::string, double>{{"meta", 20}, {"baidu", 8}}));
    EXPECT_EQ(reading.fastestVendor, "meta");
}

TEST(CharacterVendors, UnknownModelsFallBackToTheSourcesVendor) {
    const auto reading = VendorReading::of(
        {agent(SourceKind::Gemini, "some-future-model", 12), agent(SourceKind::Kimi, std::nullopt, 0)}, nullptr);
    EXPECT_EQ(reading.rates, (std::map<std::string, double>{{"google", 12}}));
    EXPECT_EQ(reading.fastestModel, "some-future-model");
    EXPECT_EQ(reading.fastestVendor, "google");
}

// Every roster character's spec names the vendor its models resolve to, so Auto picks her.
TEST(CharacterVendors, EveryRosterCharacterIsPickedForItsModels) {
    const auto specs = fixtures::specDir() / ".." / "characters" / "specs";
    if (!fs::exists(specs)) GTEST_SKIP() << "characters/specs not synced";
    std::map<std::string, std::vector<std::string>> vendorsOf;
    for (const auto& file : fs::directory_iterator(specs)) {
        if (file.path().extension() != ".json") continue;
        fixtures::JsonFile doc(file.path());
        ASSERT_TRUE(doc.ok);
        std::vector<std::string> list;
        for (const auto& v : doc.root()["vendors"].elements()) list.push_back(*v.string());
        vendorsOf[*doc.root()["id"].string()] = list;
    }
    const auto prices = pricing::PriceBook::bundled();
    const std::vector<std::pair<std::string, std::string>> cases = {
        {"claude-opus-4-5", "claude"}, {"gpt-5.1-codex", "gpt"}, {"grok-4", "grok"}, {"gemini-2.5-pro", "gemini"},
        {"deepseek-chat", "deepseek"}, {"kimi-k2-thinking", "kimi"}, {"glm-4.6", "glm"}, {"qwen3-coder-plus", "qwen"},
        {"doubao-seed-1.6", "seed"}, {"MiniMax-M2", "minimax"}, {"meta-llama/llama-4-maverick", "llama"},
        {"mistral-large-latest", "mistral"}, {"sonar-pro", "perplexity"}, {"ernie-4.5-turbo", "wenxin"},
        {"hunyuan-t1-latest", "hunyuan"}, {"rwkv-7-world", "rwkv"},
    };
    for (const auto& [model, id] : cases) {
        const auto vendor = VendorReading::of({agent(SourceKind::Omp, model, 30)}, prices.get()).fastestVendor;
        std::vector<std::string> standing;
        for (const auto& [pack, list] : vendorsOf) {
            if (std::find(list.begin(), list.end(), vendor.value_or("")) != list.end()) standing.push_back(pack);
        }
        EXPECT_EQ(standing, std::vector<std::string>{id}) << model << " -> " << vendor.value_or("nil");
    }
    EXPECT_TRUE(vendorsOf["jev"].empty());
}
