#include "agentspeed/quota/cookies/reader.hpp"

#include <algorithm>

#include "agentspeed/quota/cookies/crypto.hpp"
#include "agentspeed/quota/cookies/header.hpp"
#include "agentspeed/quota/cookies/stores.hpp"
#include "agentspeed/quota/vendors/parsers.hpp"

namespace agentspeed::quota::cookies {

namespace {

constexpr std::size_t kIniBytes = 1 << 20;

// Chromium's `v11` keys of one browser, asked for one item at a time and only while none of the earlier ones worked.
class V11Keys {
public:
    V11Keys(const ChromiumBrowser& browser, SecretStore& secrets, std::set<BrowserKind>& refused)
        : browser_(browser), secrets_(secrets), refused_(refused) {}
    ~V11Keys() {
        for (auto& key : keys_) quota::wipe(key);
    }
    V11Keys(const V11Keys&) = delete;
    V11Keys& operator=(const V11Keys&) = delete;

    const std::string* at(std::size_t index) {
        while (keys_.size() <= index && asked_ < browser_.secretApplications.size() && !refused_.count(browser_.kind)) {
            fetch(browser_.secretApplications[asked_++]);
        }
        return index < keys_.size() ? &keys_[index] : nullptr;
    }

private:
    void fetch(const std::string& application) {
        auto found = secrets_.lookup({{"application", application}});
        if (found.result == SecretResult::Refused) refused_.insert(browser_.kind);
        if (found.result == SecretResult::Found && !found.secret.empty()) {
            if (auto key = cbcKey(found.secret, kLinuxIterations)) keys_.push_back(std::move(*key));
        }
        quota::wipe(found.secret);
    }

    const ChromiumBrowser& browser_;
    SecretStore& secrets_;
    std::set<BrowserKind>& refused_;
    std::vector<std::string> keys_;
    std::size_t asked_ = 0;
};

std::optional<std::string> decryptWith(const Cookie& cookie, const std::string& key, int metaVersion) {
    return decryptCbc(cookie.encrypted, key, metaVersion, std::string_view(cookie.host));
}

}  // namespace

struct BrowserCookieReader::Candidate {
    BrowserKind browser = BrowserKind::Chrome;
    std::string site;
    std::vector<Cookie> cookies;
    int metaVersion = 0;
    std::optional<ChromiumBrowser> chromium;
    std::string userData;
    std::string firefoxProfile;
    std::int64_t newestAccessMs = 0;
};

BrowserCookieReader::BrowserCookieReader(VendorEnvironment env, SecretStore& secrets) : env_(std::move(env)), secrets_(secrets) {}

void BrowserCookieReader::resetRefusals() {
    std::lock_guard lock(mutex_);
    refused_.clear();
}

std::vector<BrowserCookieReader::Candidate> BrowserCookieReader::candidates(const std::vector<std::string>& sites,
                                                                            std::int64_t nowS) {
    std::vector<Candidate> found;
    const auto add = [&](Candidate candidate) {
        auto rows = usable(candidate.cookies, candidate.site, nowS);
        wipe(candidate.cookies);
        if (rows.empty()) return;
        candidate.newestAccessMs = 0;
        for (const auto& row : rows) candidate.newestAccessMs = std::max(candidate.newestAccessMs, row.lastAccessMs);
        candidate.cookies = std::move(rows);
        found.push_back(std::move(candidate));
    };
    auto& files = env_.fs();
    for (const auto& browser : chromiumBrowsers(env_)) {
        if (refused_.count(browser.kind)) continue;
        for (const auto& userData : browser.userDataFolders) {
            for (const auto& store : chromiumCookieStores(files, userData)) {
                auto rows = readChromeStore(env_, store, sites);
                if (!rows) continue;
                for (const auto& site : sites) {
                    Candidate candidate;
                    candidate.browser = browser.kind;
                    candidate.site = site;
                    candidate.cookies = std::move(rows->sites[site]);
                    candidate.metaVersion = rows->metaVersion;
                    candidate.chromium = browser;
                    candidate.userData = userData;
                    add(std::move(candidate));
                }
            }
        }
    }
    std::vector<std::string> profiles;
    for (const auto& root : firefoxRoots(env_)) {
        const auto ini = files.read(fsx::join(root, "profiles.ini"), kIniBytes);
        if (!ini) continue;
        for (auto& profile : firefoxProfiles(*ini, root)) {
            if (std::find(profiles.begin(), profiles.end(), profile) == profiles.end()) profiles.push_back(std::move(profile));
        }
    }
    for (const auto& profile : profiles) {
        const auto store = fsx::join(profile, "cookies.sqlite");
        const auto info = files.stat(store);
        if (!info || !info->regular) continue;
        auto rows = readFirefoxStore(env_, store, sites);
        if (!rows) continue;
        for (const auto& site : sites) {
            Candidate candidate;
            candidate.browser = BrowserKind::Firefox;
            candidate.site = site;
            candidate.cookies = std::move((*rows)[site]);
            candidate.firefoxProfile = profile;
            add(std::move(candidate));
        }
    }
    return found;
}

std::optional<std::vector<Cookie>> BrowserCookieReader::readable(Candidate& candidate) {
    const bool encrypted = std::any_of(candidate.cookies.begin(), candidate.cookies.end(),
                                       [](const Cookie& c) { return c.needsDecryption(); });
    if (!encrypted) return candidate.cookies;
    if (!candidate.chromium) return std::nullopt;
    auto v10 = cbcKey(kLinuxV10Password, kLinuxIterations);
    auto empty = cbcKey("", kLinuxIterations);
    V11Keys v11(*candidate.chromium, secrets_, refused_);
    std::vector<Cookie> out;
    for (const auto& cookie : candidate.cookies) {
        if (!cookie.needsDecryption()) {
            out.push_back(cookie);
            continue;
        }
        const auto prefix = std::string_view(cookie.encrypted).substr(0, 3);
        std::optional<std::string> plain;
        if (prefix == "v10") {
            if (v10) plain = decryptWith(cookie, *v10, candidate.metaVersion);
        } else if (prefix == "v11") {
            for (std::size_t i = 0; !plain; ++i) {
                const auto* key = v11.at(i);
                if (!key) break;
                plain = decryptWith(cookie, *key, candidate.metaVersion);
            }
        } else {
            continue;  // `v20` and anything else cannot be read
        }
        // Chromium wrote some values with an empty password while the keyring was not available.
        if (!plain && empty) plain = decryptWith(cookie, *empty, candidate.metaVersion);
        if (!plain) continue;
        Cookie clear = cookie;
        clear.value = std::move(*plain);
        quota::wipe(clear.encrypted);
        out.push_back(std::move(clear));
    }
    if (v10) quota::wipe(*v10);
    if (empty) quota::wipe(*empty);
    if (out.empty()) return std::nullopt;
    return out;
}

std::string BrowserCookieReader::userAgent(const Candidate& candidate) {
    auto& files = env_.fs();
    if (candidate.browser == BrowserKind::Firefox) {
        // Firefox names its major version alone (`144.0`), also in a dot release.
        auto version = firefoxVersion(files, candidate.firefoxProfile).value_or(std::string(kDefaultFirefoxVersion));
        version = version.substr(0, version.find('.')) + ".0";
        return vendors::firefoxUserAgent(firefoxPlatformToken(env_.architecture()), version);
    }
    return vendors::chromeUserAgent(kChromePlatformToken, chromiumMajorVersion(files, candidate.userData).value_or(kDefaultChromeMajor));
}

std::optional<BrowserSession> BrowserCookieReader::session(const std::vector<std::string>& sites, std::int64_t nowMs) {
    std::lock_guard lock(mutex_);
    const std::int64_t nowS = nowMs / 1000;
    auto list = candidates(sites, nowS);
    // The most recently used profile first; equal times keep the browser order.
    std::stable_sort(list.begin(), list.end(),
                     [](const Candidate& a, const Candidate& b) { return a.newestAccessMs > b.newestAccessMs; });
    std::optional<BrowserSession> found;
    for (auto& candidate : list) {
        if (found) break;
        if (candidate.chromium && refused_.count(candidate.browser)) continue;
        auto cookies = readable(candidate);
        if (!cookies) continue;
        auto header = joinHeader(*cookies, candidate.site, nowS);
        wipe(*cookies);
        if (!header) continue;
        BrowserSession session;
        session.site = candidate.site;
        session.cookie = Secret(std::move(*header));
        session.userAgent = userAgent(candidate);
        session.browser = candidate.browser;
        found = std::move(session);
    }
    for (auto& candidate : list) wipe(candidate.cookies);
    return found;
}

}  // namespace agentspeed::quota::cookies
