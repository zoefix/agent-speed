#include <gtest/gtest.h>
#include <openssl/evp.h>

#include "agentspeed/quota/cookies/browsers.hpp"
#include "agentspeed/quota/cookies/crypto.hpp"
#include "agentspeed/quota/cookies/reader.hpp"
#include "agentspeed/quota/cookies/stores.hpp"
#include "agentspeed/quota/routes/fingerprint.hpp"
#include "agentspeed/quota/sources.hpp"
#include "agentspeed/quota/vendor_service.hpp"
#include "agentspeed/sqlite.hpp"
#include "fixture_support.hpp"
#include "quota_vendor_support.hpp"

// QUOTAS 9.10 on Linux: browser cookie stores built in a temporary home, read through private copies.
namespace {

namespace cookies = agentspeed::quota::cookies;
namespace sqlite = agentspeed::sqlite;
using agentspeed::quota::VendorEnvironment;
using quota2_test::FakeSecrets;
using quota2_test::TempHome;

constexpr std::int64_t kNowMs = 1'794'338'773'000;
constexpr std::int64_t kNowS = kNowMs / 1000;

std::int64_t chromeUs(std::int64_t unixS) { return unixS * 1'000'000 + cookies::kWindowsEpochOffsetUs; }

// What Chromium writes: prefix, AES-128-CBC(PBKDF2(password)), the host hash first from meta version 24.
std::string chromeEncrypt(std::string_view plain, std::string_view password, std::string_view prefix,
                          std::optional<std::string_view> host) {
    const auto key = *cookies::cbcKey(password, cookies::kLinuxIterations);
    const std::string body = (host ? agentspeed::quota::sha256(*host) : std::string()) + std::string(plain);
    const std::string iv(16, ' ');
    EVP_CIPHER_CTX* ctx = EVP_CIPHER_CTX_new();
    EVP_EncryptInit_ex(ctx, EVP_aes_128_cbc(), nullptr, reinterpret_cast<const unsigned char*>(key.data()),
                       reinterpret_cast<const unsigned char*>(iv.data()));
    std::string out(body.size() + 32, '\0');
    int a = 0, b = 0;
    EVP_EncryptUpdate(ctx, reinterpret_cast<unsigned char*>(out.data()), &a, reinterpret_cast<const unsigned char*>(body.data()),
                      static_cast<int>(body.size()));
    EVP_EncryptFinal_ex(ctx, reinterpret_cast<unsigned char*>(out.data()) + a, &b);
    EVP_CIPHER_CTX_free(ctx);
    out.resize(static_cast<std::size_t>(a + b));
    return std::string(prefix) + out;
}

struct ChromeRow {
    std::string host, name, value, encrypted;
    std::int64_t expiresS = 0, lastAccessS = 0;
};

void chromeStore(const std::string& path, int metaVersion, const std::vector<ChromeRow>& rows, bool keepWal = false,
                 std::unique_ptr<sqlite::Connection>* open = nullptr) {
    std::filesystem::create_directories(std::filesystem::path(path).parent_path());
    auto db = std::make_unique<sqlite::Connection>(sqlite::Connection::open(path, sqlite::OpenMode::ReadWrite));
    if (keepWal) db->execute("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
    db->execute("CREATE TABLE meta(key LONGVARCHAR NOT NULL UNIQUE PRIMARY KEY, value LONGVARCHAR)");
    db->execute("INSERT INTO meta VALUES('version', '" + std::to_string(metaVersion) + "')");
    db->execute(
        "CREATE TABLE cookies(creation_utc INTEGER NOT NULL, host_key TEXT NOT NULL, top_frame_site_key TEXT NOT NULL DEFAULT '', "
        "name TEXT NOT NULL, value TEXT NOT NULL, encrypted_value BLOB NOT NULL DEFAULT '', path TEXT NOT NULL DEFAULT '/', "
        "expires_utc INTEGER NOT NULL, last_access_utc INTEGER NOT NULL)");
    for (const auto& row : rows) {
        auto insert = db->prepare(
            "INSERT INTO cookies(creation_utc, host_key, name, value, encrypted_value, expires_utc, last_access_utc) "
            "VALUES(1, ?1, ?2, ?3, ?4, ?5, ?6)");
        insert.bind(1, std::string_view(row.host));
        insert.bind(2, std::string_view(row.name));
        insert.bind(3, std::string_view(row.value));
        insert.bindBlob(4, std::string_view(row.encrypted));
        insert.bind(5, row.expiresS == 0 ? std::int64_t{0} : chromeUs(row.expiresS));
        insert.bind(6, chromeUs(row.lastAccessS));
        insert.step();
    }
    if (open) *open = std::move(db);
}

void firefoxStore(const std::string& path, const std::vector<ChromeRow>& rows) {
    std::filesystem::create_directories(std::filesystem::path(path).parent_path());
    auto db = sqlite::Connection::open(path, sqlite::OpenMode::ReadWrite);
    db.execute(
        "CREATE TABLE moz_cookies(id INTEGER PRIMARY KEY, originAttributes TEXT NOT NULL DEFAULT '', name TEXT, value TEXT, "
        "host TEXT, path TEXT, expiry INTEGER, lastAccessed INTEGER, creationTime INTEGER)");
    for (const auto& row : rows) {
        auto insert = db.prepare("INSERT INTO moz_cookies(name, value, host, path, expiry, lastAccessed) VALUES(?1, ?2, ?3, '/', ?4, ?5)");
        insert.bind(1, std::string_view(row.name));
        insert.bind(2, std::string_view(row.value));
        insert.bind(3, std::string_view(row.host));
        insert.bind(4, row.expiresS);
        insert.bind(5, row.lastAccessS * 1'000'000);
        insert.step();
    }
}

struct Fixture {
    TempHome home;
    FakeSecrets secrets;
    std::shared_ptr<quota2_test::CountingFiles> files = std::make_shared<quota2_test::CountingFiles>();
    VendorEnvironment env() const {
        auto e = VendorEnvironment::fixed(home.path.string(), {}, files);
        e.tempFolder = home.file("tmp");
        e.machine = "aarch64";
        return e;
    }
    Fixture() { std::filesystem::create_directories(home.file("tmp")); }
    std::vector<std::string> leftovers() const { return fixtures::listFiles(home.path / "tmp"); }
};

const std::vector<std::string> kQoderSites = {"qoder.com", "qoder.com.cn"};

TEST(QuotaCookies, NewestProfileWinsAndOnlyItsBrowserIsAsked) {
    Fixture f;
    // Chrome's Default profile: a v10 cookie used an hour ago.
    chromeStore(f.home.file(".config/google-chrome/Default/Cookies"), 23,
                {{".qoder.com", "sid", "", chromeEncrypt("chrome-v10", "peanuts", "v10", std::nullopt), 0, kNowS - 3600}});
    // Brave's second profile: a v11 cookie (host hash, Chrome 130+) used a minute ago.
    chromeStore(f.home.file(".config/BraveSoftware/Brave-Browser/Profile 2/Cookies"), 24,
                {{"qoder.com", "sid", "", chromeEncrypt("brave-v11", "brave-secret", "v11", "qoder.com"), kNowS + 86400, kNowS - 60},
                 {"qoder.com", "lang", "en", "", 0, kNowS - 120}});
    f.home.write(".config/BraveSoftware/Brave-Browser/Last Version", "141.1.84.2\n");
    f.secrets.items[{{"application", "brave"}}] = {agentspeed::quota::SecretResult::Found, "brave-secret"};

    cookies::BrowserCookieReader reader(f.env(), f.secrets);
    const auto session = reader.session(kQoderSites, kNowMs);
    ASSERT_TRUE(session.has_value());
    EXPECT_EQ(session->site, "qoder.com");
    EXPECT_EQ(session->cookie.value(), "sid=brave-v11; lang=en");
    EXPECT_EQ(session->browser, cookies::BrowserKind::Brave);
    EXPECT_EQ(session->userAgent,
              "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36");
    ASSERT_EQ(f.secrets.lookups.size(), 1u);
    EXPECT_EQ(f.secrets.lookups[0], (agentspeed::quota::SecretAttributes{{"application", "brave"}}));
    EXPECT_TRUE(f.leftovers().empty());
}

TEST(QuotaCookies, ChromeV10WithoutAnySecretAndTheEmptyKeyFallback) {
    Fixture f;
    chromeStore(f.home.file(".config/google-chrome/Default/Cookies"), 24,
                {{".workbuddy.cn", "a", "", chromeEncrypt("peanut-value", "peanuts", "v10", ".workbuddy.cn"), 0, kNowS - 10},
                 {"www.workbuddy.cn", "b", "", chromeEncrypt("empty-key", "", "v10", "www.workbuddy.cn"), 0, kNowS - 20},
                 {"www.workbuddy.cn", "c", "", chromeEncrypt("forged", "peanuts", "v10", "other.cn"), 0, kNowS - 30},
                 {"www.workbuddy.cn", "d", "", "v20" + std::string(48, 'x'), 0, kNowS - 40}});
    f.home.write(".config/google-chrome/Last Version", "142.0.7444.60");
    cookies::BrowserCookieReader reader(f.env(), f.secrets);
    const auto session = reader.session({"workbuddy.cn"}, kNowMs);
    ASSERT_TRUE(session.has_value());
    // The forged host hash and the app-bound v20 value are dropped.
    EXPECT_EQ(session->cookie.value(), "a=peanut-value; b=empty-key");
    EXPECT_TRUE(f.secrets.lookups.empty());
}

TEST(QuotaCookies, ARefusedBrowserIsLeftOutUntilReset) {
    Fixture f;
    chromeStore(f.home.file(".config/google-chrome/Default/Cookies"), 23,
                {{"qoder.com", "sid", "", chromeEncrypt("chrome", "chrome-secret", "v11", std::nullopt), 0, kNowS - 10}});
    chromeStore(f.home.file(".config/chromium/Default/Cookies"), 23, {{"qoder.com", "sid", "chromium-plain", "", 0, kNowS - 500}});
    f.secrets.items[{{"application", "chrome"}}] = {agentspeed::quota::SecretResult::Refused, ""};

    cookies::BrowserCookieReader reader(f.env(), f.secrets);
    auto session = reader.session(kQoderSites, kNowMs);
    ASSERT_TRUE(session.has_value());
    EXPECT_EQ(session->cookie.value(), "sid=chromium-plain");
    EXPECT_EQ(f.secrets.lookups.size(), 1u);

    session = reader.session(kQoderSites, kNowMs);
    ASSERT_TRUE(session.has_value());
    EXPECT_EQ(f.secrets.lookups.size(), 1u) << "a refusal is not asked again";

    f.secrets.items[{{"application", "chrome"}}] = {agentspeed::quota::SecretResult::Found, "chrome-secret"};
    reader.resetRefusals();
    session = reader.session(kQoderSites, kNowMs);
    ASSERT_TRUE(session.has_value());
    EXPECT_EQ(session->cookie.value(), "sid=chrome");
    EXPECT_EQ(f.secrets.lookups.size(), 2u);
}

TEST(QuotaCookies, TheNewestCookieIsChosenBeforeAnythingIsDecrypted) {
    Fixture f;
    chromeStore(f.home.file(".config/google-chrome/Default/Cookies"), 23,
                {{"qoder.com", "sid", "", chromeEncrypt("chrome", "chrome-secret", "v11", std::nullopt), 0, kNowS - 900}});
    f.home.write(".mozilla/firefox/profiles.ini", "[General]\nStartWithLastProfile=1\n\n[Profile0]\nName=default\nIsRelative=1\nPath=abcd.default-release\n");
    firefoxStore(f.home.file(".mozilla/firefox/abcd.default-release/cookies.sqlite"), {{"qoder.com.cn", "sid", "firefox", "", kNowS + 600, kNowS - 5}});
    f.home.write(".mozilla/firefox/abcd.default-release/compatibility.ini", "[Compatibility]\nLastVersion=144.0.2_20251030000000/20251030000000\n");
    cookies::BrowserCookieReader reader(f.env(), f.secrets);
    const auto session = reader.session(kQoderSites, kNowMs);
    ASSERT_TRUE(session.has_value());
    EXPECT_EQ(session->site, "qoder.com.cn");
    EXPECT_EQ(session->cookie.value(), "sid=firefox");
    EXPECT_EQ(session->browser, cookies::BrowserKind::Firefox);
    EXPECT_EQ(session->userAgent, "Mozilla/5.0 (X11; Linux aarch64; rv:144.0) Gecko/20100101 Firefox/144.0");
    EXPECT_TRUE(f.secrets.lookups.empty()) << "Chrome's item is never asked for: Firefox's cookie is newer";
}

TEST(QuotaCookies, ControlCharactersAreNeverSent) {
    Fixture f;
    chromeStore(f.home.file(".config/chromium/Default/Cookies"), 23,
                {{"qoder.com", "ok", "fine", "", 0, kNowS},
                 {"qoder.com", "bad", "line\nbreak", "", 0, kNowS},
                 {"qoder.com", "semi;colon", "x", "", 0, kNowS},
                 {"qoder.com", "del", std::string("a\x7f", 2), "", 0, kNowS},
                 {"qoder.com", "tab", "a\tb", "", 0, kNowS}});
    cookies::BrowserCookieReader reader(f.env(), f.secrets);
    const auto session = reader.session(kQoderSites, kNowMs);
    ASSERT_TRUE(session.has_value());
    EXPECT_EQ(session->cookie.value(), "ok=fine");
}

TEST(QuotaCookies, OnlyTheSitesUnexpiredRowsFromAPrivateCopyWithItsWal) {
    Fixture f;
    std::unique_ptr<sqlite::Connection> open;
    const auto store = f.home.file(".config/microsoft-edge/Profile 1/Network/Cookies");
    chromeStore(store, 23,
                {{"qoder.com", "live", "1", "", kNowS + 10, kNowS},
                 {"qoder.com", "gone", "2", "", kNowS - 10, kNowS},
                 {"notqoder.com", "evil", "3", "", 0, kNowS},
                 {"qoder.com.evil.net", "evil2", "4", "", 0, kNowS}},
                true, &open);
    ASSERT_TRUE(std::filesystem::exists(store + "-wal"));
    const auto before = fixtures::listFiles(f.home.path / ".config/microsoft-edge");
    cookies::BrowserCookieReader reader(f.env(), f.secrets);
    const auto session = reader.session(kQoderSites, kNowMs);
    ASSERT_TRUE(session.has_value());
    EXPECT_EQ(session->cookie.value(), "live=1");
    EXPECT_EQ(session->browser, cookies::BrowserKind::Edge);
    EXPECT_EQ(fixtures::listFiles(f.home.path / ".config/microsoft-edge"), before) << "the browser's files are never opened by SQLite";
    EXPECT_TRUE(f.leftovers().empty());
    // Nothing outside the home was looked at.
    for (const auto& path : f.files->touched) EXPECT_EQ(path.rfind(f.home.path.string(), 0), 0u) << path;
}

TEST(QuotaCookies, NoSessionWithoutAnUnexpiredCookie) {
    Fixture f;
    chromeStore(f.home.file(".config/google-chrome/Default/Cookies"), 23, {{"qoder.com", "old", "1", "", kNowS - 1, kNowS}});
    cookies::BrowserCookieReader reader(f.env(), f.secrets);
    EXPECT_FALSE(reader.session(kQoderSites, kNowMs).has_value());
    EXPECT_TRUE(f.leftovers().empty());
}

TEST(QuotaCookies, FirefoxProfilesIni) {
    const auto profiles = cookies::firefoxProfiles(
        "[Install4F96D1932A9F858E]\nDefault=x.default\n[Profile1]\nName=a\nIsRelative=1\nPath=a.default\n\n"
        "[Profile0]\nName=b\nIsRelative=0\nPath=/data/ff/b\n[General]\nVersion=2\n",
        "/home/u/.mozilla/firefox");
    EXPECT_EQ(profiles, (std::vector<std::string>{"/home/u/.mozilla/firefox/a.default", "/data/ff/b"}));
    EXPECT_EQ(cookies::firefoxPlatformToken("x86_64"), "X11; Linux x86_64");
    EXPECT_EQ(cookies::firefoxPlatformToken("aarch64"), "X11; Linux aarch64");
    EXPECT_EQ(cookies::firefoxPlatformToken("evil) Chrome"), "X11; Linux x86_64");
}

TEST(QuotaCookies, BrowserFoldersOnLinux) {
    auto env = VendorEnvironment::fixed("/home/u", {{"XDG_CONFIG_HOME", "/home/u/.cfg"}});
    const auto browsers = cookies::chromiumBrowsers(env);
    ASSERT_EQ(browsers.size(), 5u);
    EXPECT_EQ(browsers[0].userDataFolders.front(), "/home/u/.cfg/google-chrome");
    EXPECT_EQ(browsers[1].userDataFolders.front(), "/home/u/.cfg/microsoft-edge");
    EXPECT_EQ(browsers[2].userDataFolders.front(), "/home/u/.cfg/BraveSoftware/Brave-Browser");
    EXPECT_EQ(browsers[3].userDataFolders.front(), "/home/u/.cfg/vivaldi");
    EXPECT_EQ(browsers[4].userDataFolders.front(), "/home/u/.cfg/chromium");
    EXPECT_EQ(browsers[4].userDataFolders[1], "/home/u/snap/chromium/common/chromium");
    EXPECT_EQ(browsers[0].secretApplications, (std::vector<std::string>{"chrome"}));
    const auto firefox = cookies::firefoxRoots(env);
    EXPECT_EQ(firefox.front(), "/home/u/.mozilla/firefox");
    EXPECT_NE(std::find(firefox.begin(), firefox.end(), "/home/u/snap/firefox/common/.mozilla/firefox"), firefox.end());
}

TEST(QuotaCookies, TheServiceSendsOnlyTheConsentedSitesCookiesFromTheRealStore) {
    Fixture f;
    f.home.write(".mozilla/firefox/profiles.ini", "[Profile0]\nIsRelative=1\nPath=p.default\n");
    firefoxStore(f.home.file(".mozilla/firefox/p.default/cookies.sqlite"),
                 {{"qoder.com", "sid", "q-session", "", kNowS + 600, kNowS - 5},
                  {"www.workbuddy.cn", "wb", "w-session", "", kNowS + 600, kNowS - 5}});
    quota2_test::FakeTransport transport;
    transport.handler = [](const agentspeed::quota::http::Request&) {
        return quota2_test::answer(200, R"({"totalQuota": {"quotaSummary": {"usedValue": 1, "limitValue": 4}}})", kNowMs);
    };
    agentspeed::quota::VendorServiceOptions options;
    options.available = {agentspeed::quota::VendorRoute::Qoder, agentspeed::quota::VendorRoute::WorkBuddy};
    agentspeed::quota::VendorService service(f.env(), transport, f.secrets, options);
    agentspeed::quota::VendorInputs in;
    in.nowMs = kNowMs;
    in.networkEnabled = true;
    in.enabledSources = {agentspeed::SourceKind::Qoder, agentspeed::SourceKind::WorkBuddy};
    in.running = in.enabledSources;
    in.switchesOn = {"quotaQoder", "quotaWorkBuddy"};
    in.qoderConsent = true;  // WorkBuddy's dialog was not accepted
    const auto& readings = service.tick(in);
    ASSERT_EQ(transport.sent.size(), 1u);
    EXPECT_EQ(transport.sent[0].url, "https://qoder.com/api/v2/me/usages/big_model_credits");
    EXPECT_EQ(quota2_test::header(transport.sent[0], "Cookie"), "sid=q-session");
    ASSERT_TRUE(readings.snapshot(agentspeed::SourceKind::Qoder).has_value());
    EXPECT_DOUBLE_EQ(readings.snapshot(agentspeed::SourceKind::Qoder)->windows.at(0).usedPercent, 25);
    EXPECT_TRUE(f.leftovers().empty());
}

}  // namespace
