#pragma once

#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/quota/routes/environment.hpp"

// QUOTAS 9.10 Browsers on Linux: where each browser keeps its profiles, its Secret Service item and its version.
namespace agentspeed::quota::cookies {

// In the order they are tried.
enum class BrowserKind : unsigned char { Chrome, Edge, Brave, Vivaldi, Chromium, Firefox };
std::string_view browserName(BrowserKind kind);

struct ChromiumBrowser {
    BrowserKind kind = BrowserKind::Chrome;
    // The user data folders: the native package, then snap and flatpak.
    std::vector<std::string> userDataFolders;
    // The `application` attribute of its `v11` password item (Chromium's key_storage_libsecret), in the order asked.
    std::vector<std::string> secretApplications;
};
std::vector<ChromiumBrowser> chromiumBrowsers(const VendorEnvironment& env);

// The profiles' cookie stores: `Default`, then `Profile 1`, `Profile 2` … in number order; each its `Cookies`, else
// `Network/Cookies`.
std::vector<std::string> chromiumCookieStores(Files& files, const std::string& userData);
// The major version from `<userData>/Last Version` (`142.0.7444.60` → 142).
std::optional<int> chromiumMajorVersion(Files& files, const std::string& userData);

// The Firefox folders that may hold `profiles.ini`: `~/.mozilla/firefox`, the XDG one (`~/.config/mozilla/firefox`),
// snap and flatpak.
std::vector<std::string> firefoxRoots(const VendorEnvironment& env);
// Every `[Profile…]` section's `Path`, relative to `root` unless `IsRelative=0`.
std::vector<std::string> firefoxProfiles(std::string_view ini, const std::string& root);
// `LastVersion=144.0_20251020/20251020` of the profile's `compatibility.ini` → `144.0`.
std::optional<std::string> firefoxVersion(Files& files, const std::string& profile);

// What the browsers send on Linux: Chrome's reduced user agent always says `X11; Linux x86_64`; Firefox names the
// machine's architecture.
constexpr std::string_view kChromePlatformToken = "X11; Linux x86_64";
std::string firefoxPlatformToken(std::string_view architecture);

constexpr int kDefaultChromeMajor = 142;
constexpr std::string_view kDefaultFirefoxVersion = "144.0";

}  // namespace agentspeed::quota::cookies
