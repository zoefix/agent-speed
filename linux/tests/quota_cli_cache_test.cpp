#include <fcntl.h>
#include <gtest/gtest.h>
#include <unistd.h>

#include "agentspeed/quota/cli_cache.hpp"
#include "agentspeed/quota/format.hpp"
#include "agentspeed/tz.hpp"
#include "quota_test_support.hpp"

// QUOTAS 5.6: Claude Code's usage cache through `claudeCodeCache` of spec/fixtures/quota-cases.json (the files, reads
// in a row, the joins with the bridge and each window's age), and the reader's cadence over a temporary file.
using namespace agentspeed;
using namespace agentspeed::quota;
using quota_test::section;

namespace {

constexpr std::int64_t kMinute = 60'000;
constexpr const char* kLanguages[] = {"zh-Hant", "zh-Hans", "en", "ja"};

json::Value cacheCases() { return section("claudeCodeCache"); }

std::string fileText(const char* name) {
    for (const auto& item : cacheCases()["files"].elements()) {
        if (item["name"].equals(name)) return *item["text"].string();
    }
    ADD_FAILURE() << "no file " << name;
    return {};
}

const fixtures::JsonFile& strings() {
    static const fixtures::JsonFile file(fixtures::specDir() / "strings.json");
    return file;
}

QuotaFormat formatFor(const std::string& language) {
    const auto lookup = [language](std::string_view key) { return *strings().root()[key][language].string(); };
    const auto zone = TimeZone::load(*quota_test::cases().root()["timeZone"].string());
    return QuotaFormat(QuotaStrings::from(lookup), language, [zone](std::int64_t ms) { return zone ? zone->offsetMs(ms) : 0; });
}

// The reader over `<temp>/.claude.json` with a clock the test moves.
class ReaderRig {
public:
    quota_test::TempDir dir;
    std::string file = (dir / ".claude.json").string();
    cli::CacheReader reader{file};
    std::int64_t now = quota_test::fixtureNow();
    std::int64_t mtimeMs = quota_test::fixtureNow();

    bool check() { return reader.refresh(now, true); }
    // A new version: its own modification time, as a text of the same size may follow within the clock's tick.
    void write(const std::string& text) {
        quota_test::writeFile(file, text);
        mtimeMs += 1000;
        quota_test::setMtimeMs(file, mtimeMs);
    }
    // Past the once-a-minute gate.
    void later() { now += kMinute + 1; }
};

void expectAges(const json::Value& c) {
    const auto snapshot = quota_test::snapshotOf(c["snapshot"]);
    ASSERT_TRUE(snapshot);
    const auto now = *c["nowMs"].truncated();
    const auto windows = ordered(snapshot->windows);
    const auto expected = c["expected"].elements();
    ASSERT_EQ(windows.size(), expected.size());
    for (std::size_t i = 0; i < windows.size(); ++i) {
        SCOPED_TRACE(englishLabel(windows[i]));
        EXPECT_EQ(englishLabel(windows[i]), *expected[i]["label"].string());
        EXPECT_EQ(timeOf(windows[i], *snapshot), *expected[i]["observedMs"].truncated());
        for (const auto* language : kLanguages) {
            EXPECT_EQ(formatFor(language).age(timeOf(windows[i], *snapshot), now), fixtures::optString(expected[i]["age"][language])) << language;
        }
    }
}

}  // namespace

TEST(ClaudeCodeCache, FilesOfTheFixtures) {
    const auto files = cacheCases()["files"].elements();
    ASSERT_GE(files.size(), 24u);
    for (const auto& item : files) {
        SCOPED_TRACE(*item["name"].string());
        const auto read = cli::readCache(*item["text"].string());
        if (item["expected"].isString()) {
            EXPECT_TRUE(item["expected"].equals("unparsed"));
            EXPECT_FALSE(read.parsed);
            EXPECT_FALSE(read.part);
            continue;
        }
        EXPECT_TRUE(read.parsed);
        quota_test::expectSnapshot(read.part, item["expected"]);
        // The cache part's windows have no time of their own: the part has one time.
        if (read.part) {
            for (const auto& w : read.part->windows) EXPECT_FALSE(w.observedMs);
        }
    }
}

TEST(ClaudeCodeCache, ReadsInARowKeepTheValueOfAFileThatDoesNotParse) {
    ReaderRig rig;
    const auto texts = cacheCases()["reads"]["texts"].elements();
    const auto expected = cacheCases()["reads"]["expected"].elements();
    ASSERT_EQ(texts.size(), expected.size());
    for (std::size_t i = 0; i < texts.size(); ++i) {
        SCOPED_TRACE("read " + std::to_string(i));
        rig.write(*texts[i].string());
        rig.later();
        rig.check();
        EXPECT_EQ(rig.reader.reads(), static_cast<int>(i) + 1);
        quota_test::expectSnapshot(rig.reader.snapshot(), expected[i]);
    }
}

TEST(ClaudeCodeCache, JoinsWithTheBridge) {
    const auto joins = cacheCases()["joins"].elements();
    ASSERT_GE(joins.size(), 12u);
    for (const auto& item : joins) {
        SCOPED_TRACE(*item["name"].string());
        const auto bridge = quota_test::snapshotOf(item["bridge"]);
        const auto cache = quota_test::snapshotOf(item["cache"]);
        quota_test::expectSnapshot(cli::joined(bridge, cache), item["expected"]);
    }
}

TEST(ClaudeCodeCache, EachWindowHasItsOwnAge) {
    expectAges(cacheCases()["ages"]);
    expectAges(cacheCases()["cacheOnlyAges"]);
    // The joined snapshot shows Fable 7d at 100 % beside the bridge's 5h and 7d.
    const auto c = cacheCases()["ages"];
    const auto got = shown(*quota_test::snapshotOf(c["snapshot"]), *c["nowMs"].truncated());
    const auto want = c["shown"].elements();
    ASSERT_EQ(got.size(), want.size());
    for (std::size_t i = 0; i < got.size(); ++i) {
        SCOPED_TRACE(*want[i]["label"].string());
        EXPECT_EQ(englishLabel(got[i].window), *want[i]["label"].string());
        EXPECT_EQ(got[i].percent, *want[i]["percent"].truncated());
        EXPECT_DOUBLE_EQ(got[i].bar, *want[i]["bar"].number());
        EXPECT_EQ(got[i].resetsAtMs, fixtures::optInt(want[i]["resetsAtMs"]));
        EXPECT_EQ(got[i].passed, want[i]["passed"].isTrue());
    }
}

TEST(ClaudeCodeCache, ReadAtMostOnceAMinuteAndAgainWhenTheFileChanges) {
    ReaderRig rig;
    rig.write(fileText("observed"));
    // Switched off: nothing is read.
    EXPECT_FALSE(rig.reader.refresh(rig.now, false));
    EXPECT_EQ(rig.reader.reads(), 0);
    EXPECT_FALSE(rig.reader.snapshot());

    EXPECT_TRUE(rig.check());
    EXPECT_EQ(rig.reader.reads(), 1);
    ASSERT_TRUE(rig.reader.snapshot());
    EXPECT_EQ(rig.reader.snapshot()->windows.size(), 3u);

    // A change within a minute of the last read waits for the minute, then is read.
    rig.write(fileText("otherAccount"));
    rig.now += 10'000;
    EXPECT_FALSE(rig.check());
    EXPECT_EQ(rig.reader.reads(), 1);
    EXPECT_TRUE(rig.reader.snapshot());
    rig.later();
    EXPECT_TRUE(rig.check());
    EXPECT_EQ(rig.reader.reads(), 2);
    EXPECT_FALSE(rig.reader.snapshot()) << "another account's cache is no cache";
    // Unchanged: never read again.
    for (int k = 0; k < 3; ++k) {
        rig.later();
        EXPECT_FALSE(rig.check());
    }
    EXPECT_EQ(rig.reader.reads(), 2);

    rig.write(fileText("noOauthAccount"));
    rig.later();
    EXPECT_TRUE(rig.check());
    EXPECT_TRUE(rig.reader.snapshot());

    // A missing file has no cache, at once.
    ASSERT_TRUE(quota::files::removeFile(rig.file));
    EXPECT_TRUE(rig.check());
    EXPECT_FALSE(rig.reader.snapshot());

    // Switched off and on: read at the first check.
    rig.write(fileText("observed"));
    EXPECT_FALSE(rig.reader.refresh(rig.now, false));
    const auto reads = rig.reader.reads();
    EXPECT_TRUE(rig.check());
    EXPECT_EQ(rig.reader.reads(), reads + 1);
    EXPECT_TRUE(rig.reader.refresh(rig.now, false)) << "turning off clears the cache";
    EXPECT_FALSE(rig.reader.snapshot());
    EXPECT_EQ(rig.reader.reads(), reads + 1);
}

TEST(ClaudeCodeCache, AFileThatDoesNotParseIsReadAgainAtTheNextCheck) {
    ReaderRig rig;
    const auto observed = fileText("observed");
    rig.write(observed);
    ASSERT_TRUE(rig.check());
    const auto value = rig.reader.snapshot();
    ASSERT_TRUE(value);

    // Caught mid-write: the value stays, and the same bytes are read again a minute later.
    rig.write(observed.substr(0, observed.size() / 2));
    rig.later();
    EXPECT_FALSE(rig.check());
    EXPECT_EQ(rig.reader.snapshot(), value);
    EXPECT_EQ(rig.reader.reads(), 2);
    rig.now += 30'000;
    EXPECT_FALSE(rig.check());
    EXPECT_EQ(rig.reader.reads(), 2) << "still once a minute";
    rig.later();
    EXPECT_FALSE(rig.check());
    EXPECT_EQ(rig.reader.reads(), 3);
    EXPECT_EQ(rig.reader.snapshot(), value);

    // An empty file is the same; the next whole file is read.
    rig.write("");
    rig.later();
    EXPECT_FALSE(rig.check());
    EXPECT_EQ(rig.reader.snapshot(), value);
    rig.write(fileText("utilizationWithoutWindows"));
    rig.later();
    EXPECT_TRUE(rig.check());
    ASSERT_TRUE(rig.reader.snapshot());
    EXPECT_TRUE(rig.reader.snapshot()->windows.empty());
    // A whole file stays unread while it is unchanged.
    rig.later();
    EXPECT_FALSE(rig.check());
    EXPECT_EQ(rig.reader.reads(), 5);
}

TEST(ClaudeCodeCache, AFileOverSixtyFourMebibytesIsNotRead) {
    ReaderRig rig;
    rig.write(fileText("observed"));
    ASSERT_TRUE(rig.check());
    ASSERT_TRUE(rig.reader.snapshot());
    // A sparse file one byte over the limit: refused by its size, before any byte is read.
    const int fd = ::open(rig.file.c_str(), O_WRONLY | O_CLOEXEC);
    ASSERT_GE(fd, 0);
    ASSERT_EQ(::ftruncate(fd, static_cast<off_t>(cli::CacheReader::kFileLimit + 1)), 0);
    ::close(fd);
    rig.later();
    EXPECT_TRUE(rig.check());
    EXPECT_FALSE(rig.reader.snapshot());
    // Unchanged, it is not tried again.
    rig.later();
    EXPECT_FALSE(rig.check());
    EXPECT_EQ(rig.reader.reads(), 2);
    EXPECT_TRUE(cli::readCache(std::string(cli::CacheReader::kFileLimit + 1, ' ')).parsed);
}

TEST(ClaudeCodeCache, OnlyTheTopLevelKeysCount) {
    // The decoy under `projects` is skipped; a nested account uuid does not stand for the signed-in one.
    auto read = cli::readCache(R"({"projects":{"/a":{"cachedUsageUtilization":{"fetchedAtMs":1794330373747,"utilization":{}}}}})");
    EXPECT_TRUE(read.parsed);
    EXPECT_FALSE(read.part);
    read = cli::readCache(R"({"cachedUsageUtilization":{"fetchedAtMs":1794330373747,"accountUuid":"a","utilization":{}},)"
                          R"("projects":{"/a":{"oauthAccount":{"accountUuid":"b"}}}})");
    ASSERT_TRUE(read.part);
    EXPECT_EQ(read.part->observedMs, 1794330373747);
    // The last of two equal keys counts, as in Python's json.
    read = cli::readCache(R"({"oauthAccount":{"accountUuid":"b"},"cachedUsageUtilization":{"fetchedAtMs":1794330373747,"accountUuid":"a",)"
                          R"("utilization":{}},"oauthAccount":{"accountUuid":"a"}})");
    EXPECT_TRUE(read.part);
    // The range is checked before the fraction goes: just under 2000 is out, just under 2101 is in.
    read = cli::readCache(R"({"cachedUsageUtilization":{"fetchedAtMs":946684799999.5,"utilization":{}}})");
    EXPECT_FALSE(read.part);
    read = cli::readCache(R"({"cachedUsageUtilization":{"fetchedAtMs":4133980799999.5,"utilization":{}}})");
    ASSERT_TRUE(read.part);
    EXPECT_EQ(read.part->observedMs, 4133980799999);
    read = cli::readCache(R"({"cachedUsageUtilization":{"fetchedAtMs":1e300,"utilization":{}}})");
    EXPECT_FALSE(read.part);
}
