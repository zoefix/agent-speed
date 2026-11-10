#pragma once

#include <cstdint>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "agentspeed/quota/cookies/browsers.hpp"
#include "agentspeed/quota/cookies/header.hpp"
#include "agentspeed/quota/routes/environment.hpp"
#include "agentspeed/quota/routes/fingerprint.hpp"
#include "agentspeed/quota/secrets.hpp"

// QUOTAS 9.9, 9.10: a site's browser session, read right before its request and kept for it alone.
namespace agentspeed::quota::cookies {

// One profile's session for a site: the `Cookie` header and that browser's user agent.
struct BrowserSession {
    std::string site;
    Secret cookie;
    std::string userAgent;
    BrowserKind browser = BrowserKind::Chrome;
    // A new sign-in is a new account (QUOTAS 9.1).
    Fingerprint fingerprint() const { return cookie.fingerprint(); }
};

class SessionReader {
public:
    virtual ~SessionReader() = default;
    // The session of the first site any profile has, by the newest `last_access_utc`; nullopt when none has one.
    virtual std::optional<BrowserSession> session(const std::vector<std::string>& sites, std::int64_t nowMs) = 0;
    // The user turned the browser routes off and on again: refused browsers are asked again.
    virtual void resetRefusals() = 0;
};

// Chrome, Edge, Brave, Vivaldi, Chromium (each profile), then Firefox (each profile of each `profiles.ini`). Only the
// sites' rows are read, from private copies. Among the profiles with an unexpired cookie, the most recently used one
// wins (chosen before anything is decrypted), and only its browser's Secret Service item is asked for, and only when a
// `v11` row needs it. A browser whose item the user refused is not asked again until `resetRefusals`.
class BrowserCookieReader final : public SessionReader {
public:
    BrowserCookieReader(VendorEnvironment env, SecretStore& secrets);
    std::optional<BrowserSession> session(const std::vector<std::string>& sites, std::int64_t nowMs) override;
    void resetRefusals() override;

private:
    struct Candidate;
    std::vector<Candidate> candidates(const std::vector<std::string>& sites, std::int64_t nowS);
    std::optional<std::vector<Cookie>> readable(Candidate& candidate);
    std::vector<std::string> v11Keys(const ChromiumBrowser& browser);
    std::string userAgent(const Candidate& candidate);

    VendorEnvironment env_;
    SecretStore& secrets_;
    std::mutex mutex_;
    std::set<BrowserKind> refused_;
};

}  // namespace agentspeed::quota::cookies
