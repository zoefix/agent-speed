#include <gtest/gtest.h>
#include <zlib.h>

#include "agentspeed/quota/desktop_entry.hpp"
#include "agentspeed/quota/desktop_reader.hpp"
#include "agentspeed/quota/http.hpp"
#include "quota_test_support.hpp"

// QUOTAS 4: the cache entry fixtures, the choosing rule and the reader's search, checks and budget over a temporary
// cache folder.
using namespace agentspeed;
using namespace agentspeed::quota;
using namespace agentspeed::quota::desktop;
using quota_test::section;
using namespace std::string_view_literals;

namespace {

constexpr std::int64_t kFixtureMtimeMs = 1794338773000;
const std::string kOrg = "0fba5d0f-43ab-4376-8ca3-f41e01ec4623";
const std::string kMaxBody = R"({"five_hour":{"utilization":23.5,"resets_at":"2026-11-10T22:00:00+00:00"},)"
                             R"("seven_day":{"utilization":41.2,"resets_at":"2026-11-14T01:00:00Z"}})";

std::string usageKey(const std::string& query = "") { return "1/0/https://claude.ai/api/organizations/" + kOrg + "/usage" + query; }

std::string le(std::uint64_t value, int bytes) {
    std::string out;
    for (int i = 0; i < bytes; ++i) out += static_cast<char>((value >> (8 * i)) & 0xFF);
    return out;
}

// The layout of QUOTAS 4.2, as the reference's build_entry writes it.
std::string buildEntry(const std::string& key, const std::string& body, const std::vector<std::pair<std::string, std::string>>& headers,
                       const std::string& status = "HTTP/1.1 200") {
    std::string info = status + '\0';
    for (std::size_t i = 0; i < headers.size(); ++i) {
        if (i > 0) info += '\0';
        info += headers[i].first + ":" + headers[i].second;
    }
    info += std::string("\0\0", 2);
    const std::string stream0 = le(info.size(), 4) + info + std::string("\x04\0\0\0", 4);
    const std::string head = le(0xFCFB6D1BA7725C30ULL, 8) + le(5, 4) + le(key.size(), 4) + le(0, 4) + le(0, 4);
    const std::string eof1 = le(0xF4FA6F45970D41D8ULL, 8) + le(1, 4) + le(0, 4) + le(0, 4) + le(0, 4);
    const std::string eof0 = le(0xF4FA6F45970D41D8ULL, 8) + le(3, 4) + le(0, 4) + le(stream0.size(), 4) + le(0, 4);
    return head + key + body + eof1 + stream0 + std::string(32, '\0') + eof0;
}

// An IMF-fixdate (`Tue, 10 Nov 2026 19:26:13 GMT`) for a time after 1970.
std::string dated(std::int64_t ms) {
    static const char* weekdays[] = {"Thu", "Fri", "Sat", "Sun", "Mon", "Tue", "Wed"};
    static const char* months[] = {"Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"};
    const std::int64_t day = ms / 86'400'000;
    const std::int64_t second = (ms / 1000) % 86'400;
    // H. Hinnant's civil_from_days.
    const std::int64_t z = day + 719468;
    const std::int64_t era = z / 146097;
    const auto doe = static_cast<unsigned>(z - era * 146097);
    const unsigned yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    const unsigned doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    const unsigned mp = (5 * doy + 2) / 153;
    const unsigned d = doy - (153 * mp + 2) / 5 + 1;
    const unsigned m = mp < 10 ? mp + 3 : mp - 9;
    const std::int64_t year = static_cast<std::int64_t>(yoe) + era * 400 + (m <= 2 ? 1 : 0);
    char text[64];
    std::snprintf(text, sizeof text, "%s, %02u %s %lld %02lld:%02lld:%02lld GMT", weekdays[day % 7], d, months[m - 1],
                  static_cast<long long>(year), static_cast<long long>(second / 3600), static_cast<long long>((second / 60) % 60),
                  static_cast<long long>(second % 60));
    return text;
}

std::string usageEntry(const std::string& query, std::int64_t dateMs, const std::string& body = kMaxBody) {
    return buildEntry(usageKey(query), body, {{"date", dated(dateMs)}, {"content-type", "application/json"}});
}

std::string gzip(const std::string& data) {
    z_stream s{};
    deflateInit2(&s, Z_DEFAULT_COMPRESSION, Z_DEFLATED, 16 + MAX_WBITS, 8, Z_DEFAULT_STRATEGY);
    std::string out(deflateBound(&s, static_cast<uLong>(data.size())) + 64, '\0');
    s.next_in = reinterpret_cast<Bytef*>(const_cast<char*>(data.data()));
    s.avail_in = static_cast<uInt>(data.size());
    s.next_out = reinterpret_cast<Bytef*>(out.data());
    s.avail_out = static_cast<uInt>(out.size());
    deflate(&s, Z_FINISH);
    out.resize(s.total_out);
    deflateEnd(&s);
    return out;
}

void put(const quota_test::TempDir& dir, const std::string& name, const std::string& bytes, std::int64_t mtimeMs) {
    quota_test::writeFile(dir / name, bytes);
    quota_test::setMtimeMs(dir / name, mtimeMs);
}

std::string hexName(int n) {
    char text[24];
    std::snprintf(text, sizeof text, "%016x_0", static_cast<unsigned>(n));
    return text;
}

// 2026-11-10T19:26:13Z, the fixtures' now.
constexpr std::int64_t kNow = 1794338773000;
constexpr std::int64_t kMinute = 60'000;

}  // namespace

TEST(QuotaFixtures, DesktopEntries) {
    for (const auto& c : section("entries").elements()) {
        SCOPED_TRACE(*c["file"].string());
        const auto bytes = fixtures::readText(quota_test::entryFile(*c["file"].string()));
        ASSERT_FALSE(bytes.empty());
        const auto got = parseUsageEntry(bytes, kFixtureMtimeMs);
        const auto want = c["expected"];
        if (want.isNull()) {
            EXPECT_FALSE(got.has_value());
            continue;
        }
        ASSERT_TRUE(got.has_value());
        EXPECT_EQ(got->url, *want["url"].string());
        EXPECT_EQ(got->observedMs, *want["observedMs"].truncated());
        quota_test::expectWindows(got->windows, want["windows"]);
    }
}

TEST(QuotaFixtures, DesktopFileName) {
    const auto fixture = section("fileName");
    EXPECT_EQ(entryFileName(*fixture["key"].string()), *fixture["expected"].string());
    EXPECT_TRUE(isEntryFileName(*fixture["expected"].string()));
    EXPECT_FALSE(isEntryFileName("383706cf5aea4bcc_1"));
    EXPECT_FALSE(isEntryFileName("383706CF5AEA4BCC_0"));
    EXPECT_FALSE(isEntryFileName("index"));
}

TEST(QuotaFixtures, DesktopNewest) {
    const auto fixture = section("desktopNewest");
    const auto mtime = *fixture["mtimeMs"].truncated();
    std::vector<FoundEntry> found;
    for (const auto& file : fixture["files"].elements()) {
        const auto entry = parseUsageEntry(fixtures::readText(quota_test::entryFile(*file.string())), mtime);
        ASSERT_TRUE(entry.has_value()) << *file.string();
        found.push_back({*file.string(), *entry, mtime * 1'000'000});
    }
    const auto* best = newestEntry(found);
    ASSERT_NE(best, nullptr);
    const auto want = fixture["expected"];
    EXPECT_EQ(best->file, *want["file"].string());
    EXPECT_EQ(best->entry.observedMs, *want["observedMs"].truncated());
    quota_test::expectWindows(best->entry.windows, want["windows"]);
}

TEST(QuotaDesktop, KeysAndHeaders) {
    EXPECT_EQ(urlOfKey("1/0/https://claude.ai/x"), "https://claude.ai/x");
    EXPECT_EQ(urlOfKey("12/345/https://claude.ai/x"), "https://claude.ai/x");
    EXPECT_EQ(urlOfKey("1/https://claude.ai/x"), "1/https://claude.ai/x");
    EXPECT_EQ(urlOfKey("1/0/_dk_https://claude.ai https://claude.ai https://claude.ai/y"), "https://claude.ai/y");
    EXPECT_TRUE(isUsageUrl("https://claude.ai/api/organizations/" + kOrg + "/usage"));
    EXPECT_TRUE(isUsageUrl("https://claude.ai/api/organizations/" + kOrg + "/usage?cedar_ember=1&skip_spend=1"));
    EXPECT_FALSE(isUsageUrl("https://claude.ai/api/organizations/" + kOrg + "/usage/x"));
    EXPECT_FALSE(isUsageUrl("https://claude.ai/api/organizations/not-a-uuid/usage"));
    EXPECT_FALSE(isUsageUrl("http://claude.ai/api/organizations/" + kOrg + "/usage"));
    const auto info = parseResponseInfo("junk\0HTTP/1.1 200 OK\0Date: x\0CONTENT-Encoding:  ZSTD \0date:y\0\0z:1"sv);
    ASSERT_TRUE(info.has_value());
    EXPECT_EQ(info->status, 200);
    EXPECT_EQ(info->header("date"), "x");
    EXPECT_EQ(info->header("content-encoding"), "ZSTD");
    EXPECT_EQ(info->header("z"), std::nullopt);
    EXPECT_EQ(codingOf(info->header("content-encoding")), Coding::Zstd);
    EXPECT_EQ(codingOf(std::nullopt), Coding::Identity);
    EXPECT_EQ(codingOf(std::string("")), Coding::Identity);
    EXPECT_EQ(codingOf(std::string("deflate")), std::nullopt);
    EXPECT_FALSE(parseResponseInfo(std::string("no status line")).has_value());
}

TEST(QuotaDesktop, DecodersStopAtTheLimit) {
    const std::string big(2u << 20, '0');
    const auto packed = gzip(big);
    EXPECT_FALSE(decodeBody(packed, Coding::Gzip, kBodyLimit).has_value());
    const auto ok = decodeBody(gzip("{\"a\":1}") + gzip("{}"), Coding::Gzip, kBodyLimit);
    ASSERT_TRUE(ok.has_value());
    EXPECT_EQ(*ok, "{\"a\":1}{}");
    EXPECT_FALSE(decodeBody(packed.substr(0, packed.size() / 2), Coding::Gzip, kBodyLimit).has_value());
    EXPECT_FALSE(decodeBody(gzip("{}") + "trailing", Coding::Gzip, kBodyLimit).has_value());
    EXPECT_FALSE(decodeBody("not zstd", Coding::Zstd, kBodyLimit).has_value());
    EXPECT_FALSE(decodeBody("", Coding::Brotli, kBodyLimit).has_value());
}

TEST(QuotaDesktop, BodyWindows) {
    json::Document doc;
    ASSERT_TRUE(doc.parse(R"({"five_hour": {"utilization": "5"}, "seven_day": {"utilization": 10, "resets_at": "soon"},
        "seven_day_opus": {"utilization": 3}, "seven_day_sonnet": {"utilization": 4},
        "limits": [{"kind": "weekly_scoped", "percent": 9, "scope": {"model": {"display_name": "opus"}}},
                   {"kind": "weekly_scoped", "percent": 8, "scope": {"model": {"display_name": "  "}}},
                   {"kind": "weekly_all", "percent": 7}]})"));
    const auto windows = usageWindows(doc.root());
    ASSERT_EQ(windows.size(), 3u);
    EXPECT_EQ(windows[0].kind, Kind::SevenDay);
    EXPECT_EQ(windows[0].resetsAtMs, std::nullopt);
    EXPECT_EQ(windows[1].name, "opus");
    EXPECT_DOUBLE_EQ(windows[1].usedPercent, 9);
    EXPECT_EQ(windows[2].name, "Sonnet");
}

TEST(QuotaDesktop, SearchReadsNewestHeadersFirstAndStopsAtTheFirstUsageEntry) {
    quota_test::TempDir dir;
    put(dir, hexName(1), buildEntry("1/0/https://claude.ai/api/organizations/" + kOrg + "/members", "{}", {}), kNow - kMinute);
    put(dir, hexName(2), usageEntry("?a=1", kNow - 5 * kMinute), kNow - 5 * kMinute);
    put(dir, hexName(3), usageEntry("?b=1", kNow - 2 * kMinute), kNow - 10 * kMinute);
    put(dir, hexName(4), usageEntry("?c=1", kNow), kNow - 8 * 24 * 60 * kMinute);
    put(dir, "0000000000000005_1", usageEntry("?d=1", kNow), kNow);
    put(dir, "the-real-index", "x", kNow);
    Reader reader(dir.str());
    EXPECT_TRUE(reader.tick({kNow, true, false, false}));
    EXPECT_EQ(reader.searches(), 1);
    EXPECT_EQ(reader.lastSearchReads(), 2);
    EXPECT_EQ(reader.candidates(), std::vector<std::string>{hexName(2)});
    ASSERT_TRUE(reader.snapshot().has_value());
    EXPECT_EQ(reader.snapshot()->source, SourceKind::ClaudeDesktop);
    EXPECT_EQ(reader.snapshot()->observedMs, (kNow - 5 * kMinute) / 1000 * 1000);
    EXPECT_EQ(reader.snapshot()->windows.size(), 2u);
    EXPECT_EQ(reader.snapshotFile(), hexName(2));
}

TEST(QuotaDesktop, CandidatesAreCheckedEveryMinuteAndDroppedWhenGone) {
    quota_test::TempDir dir;
    put(dir, hexName(2), usageEntry("", kNow - 5 * kMinute), kNow - 5 * kMinute);
    Reader reader(dir.str());
    reader.tick({kNow, true, false, false});
    ASSERT_TRUE(reader.snapshot().has_value());
    put(dir, hexName(2), usageEntry("", kNow + 20'000), kNow + 20'000);
    EXPECT_FALSE(reader.tick({kNow + 30'000, true, false, false}));
    EXPECT_EQ(reader.checks(), 1);
    EXPECT_TRUE(reader.tick({kNow + kMinute, true, false, false}));
    EXPECT_EQ(reader.checks(), 2);
    EXPECT_EQ(reader.snapshot()->observedMs, (kNow + 20'000) / 1000 * 1000);
    std::filesystem::remove(dir / hexName(2));
    EXPECT_TRUE(reader.tick({kNow + 2 * kMinute, true, false, false}));
    EXPECT_FALSE(reader.snapshot().has_value());
    EXPECT_TRUE(reader.candidates().empty());
    EXPECT_EQ(reader.searches(), 1);
}

TEST(QuotaDesktop, DesktopStartingSearchesAgainAndTheNewestObservationWins) {
    quota_test::TempDir dir;
    put(dir, hexName(2), usageEntry("?a=1", kNow - 5 * kMinute), kNow - 5 * kMinute);
    Reader reader(dir.str());
    reader.tick({kNow, true, false, false});
    // A newer file holding an older response does not win over a newer response.
    put(dir, hexName(3), usageEntry("?b=1", kNow - 9 * kMinute), kNow + 1000);
    EXPECT_FALSE(reader.tick({kNow + 5000, true, true, true}));
    EXPECT_EQ(reader.searches(), 2);
    EXPECT_EQ(reader.candidates().size(), 2u);
    EXPECT_EQ(reader.snapshotFile(), hexName(2));
    // A newer response without windows replaces the older one: the account lost its limits.
    put(dir, hexName(4), usageEntry("?c=1", kNow + 9000, R"({"five_hour": null, "seven_day": null, "extra": [1]})"), kNow + 9000);
    EXPECT_TRUE(reader.tick({kNow + 10'000, true, true, true}));
    EXPECT_EQ(reader.snapshotFile(), hexName(4));
    ASSERT_TRUE(reader.snapshot().has_value());
    EXPECT_TRUE(reader.snapshot()->windows.empty());
    EXPECT_EQ(reader.snapshot()->observedMs, (kNow + 9000) / 1000 * 1000);
}

TEST(QuotaDesktop, WhileDesktopRunsAStaleCandidateIsLookedForEveryHalfHour) {
    quota_test::TempDir dir;
    put(dir, hexName(2), usageEntry("", kNow - 25 * kMinute), kNow - 25 * kMinute);
    Reader reader(dir.str());
    reader.tick({kNow, true, true, false});
    EXPECT_EQ(reader.searches(), 1);
    reader.tick({kNow + 29 * kMinute, true, true, false});
    EXPECT_EQ(reader.searches(), 1);
    reader.tick({kNow + 31 * kMinute, true, false, false});
    EXPECT_EQ(reader.searches(), 1) << "Desktop does not run";
    reader.tick({kNow + 31 * kMinute, true, true, false});
    EXPECT_EQ(reader.searches(), 2);
    // A fresh candidate (Desktop refreshed it, seen by the minute's check) needs no search.
    put(dir, hexName(2), usageEntry("", kNow + 50 * kMinute), kNow + 50 * kMinute);
    reader.tick({kNow + 50 * kMinute, true, true, false});
    EXPECT_EQ(reader.snapshot()->observedMs, kNow + 50 * kMinute);
    reader.tick({kNow + 61 * kMinute, true, true, false});
    reader.tick({kNow + 70 * kMinute, true, true, false});
    EXPECT_EQ(reader.searches(), 2);
}

TEST(QuotaDesktop, DisabledReadsNothingAndForgets) {
    quota_test::TempDir dir;
    put(dir, hexName(2), usageEntry("", kNow - kMinute), kNow - kMinute);
    Reader off(dir.str());
    EXPECT_FALSE(off.tick({kNow, false, true, true}));
    EXPECT_EQ(off.searches(), 0);
    EXPECT_EQ(off.checks(), 0);
    EXPECT_FALSE(off.snapshot().has_value());

    Reader reader(dir.str());
    reader.tick({kNow, true, false, false});
    ASSERT_TRUE(reader.snapshot().has_value());
    EXPECT_TRUE(reader.tick({kNow + kMinute, false, true, true}));
    EXPECT_FALSE(reader.snapshot().has_value());
    EXPECT_TRUE(reader.candidates().empty());
    reader.tick({kNow + 2 * kMinute, false, true, false});
    EXPECT_EQ(reader.searches(), 1);
    EXPECT_EQ(reader.checks(), 1);
    EXPECT_TRUE(reader.tick({kNow + 3 * kMinute, true, false, false}));
    EXPECT_EQ(reader.searches(), 2);
}

TEST(QuotaDesktop, AMissingFolderIsSearchedOnceItAppears) {
    quota_test::TempDir home;
    const auto folder = home / "Claude" / "Cache" / "Cache_Data";
    Reader reader(folder.string());
    EXPECT_FALSE(reader.tick({kNow, true, false, false}));
    EXPECT_EQ(reader.searches(), 0);
    std::filesystem::create_directories(folder);
    quota_test::writeFile(folder / hexName(2), usageEntry("", kNow));
    quota_test::setMtimeMs(folder / hexName(2), kNow);
    reader.tick({kNow + 5000, true, false, false});
    EXPECT_EQ(reader.searches(), 0);
    EXPECT_TRUE(reader.tick({kNow + kMinute, true, false, false}));
    EXPECT_EQ(reader.searches(), 1);
    EXPECT_TRUE(reader.snapshot().has_value());
}

TEST(QuotaDesktop, SearchStopsAfterFourThousandHeaders) {
    quota_test::TempDir dir;
    const auto other = buildEntry("1/0/https://claude.ai/api/bootstrap", "{}", {});
    for (int i = 0; i < Reader::kMaxHeaderReads + 5; ++i) put(dir, hexName(100 + i), other, kNow - kMinute);
    put(dir, hexName(2), usageEntry("", kNow - 2 * kMinute), kNow - 2 * kMinute);
    Reader reader(dir.str());
    reader.tick({kNow, true, false, false});
    EXPECT_EQ(reader.lastSearchReads(), Reader::kMaxHeaderReads);
    EXPECT_TRUE(reader.candidates().empty());
    EXPECT_FALSE(reader.snapshot().has_value());
}

TEST(QuotaDesktop, EntriesWithoutADateUseTheFileTime) {
    const auto bytes = buildEntry(usageKey(), kMaxBody, {});
    const auto entry = parseUsageEntry(bytes, 1234);
    ASSERT_TRUE(entry.has_value());
    EXPECT_EQ(entry->observedMs, 1234);
    const auto undated = buildEntry(usageKey(), kMaxBody, {{"date", "yesterday"}});
    EXPECT_EQ(parseUsageEntry(undated, 99)->observedMs, 99);
    EXPECT_FALSE(parseUsageEntry(buildEntry(usageKey(), "[1]", {}), 0).has_value());
    EXPECT_FALSE(parseUsageEntry(buildEntry(usageKey(), "{\"a\": \"\xff\"}", {}), 0).has_value());
    EXPECT_EQ(http::parseHttpDate(dated(kNow)), kNow);
}
