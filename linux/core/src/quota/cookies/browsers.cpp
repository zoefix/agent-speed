#include "agentspeed/quota/cookies/browsers.hpp"

#include <algorithm>
#include <charconv>
#include <limits>

namespace agentspeed::quota::cookies {

namespace {

constexpr std::size_t kVersionFileBytes = 64;
constexpr std::size_t kCompatibilityBytes = 64 << 10;

std::string_view trim(std::string_view text) {
    const auto first = text.find_first_not_of(" \t\r\n");
    if (first == std::string_view::npos) return {};
    return text.substr(first, text.find_last_not_of(" \t\r\n") - first + 1);
}

std::vector<std::string_view> lines(std::string_view text) {
    std::vector<std::string_view> out;
    std::size_t start = 0;
    while (start < text.size()) {
        auto end = text.find_first_of("\r\n", start);
        if (end == std::string_view::npos) end = text.size();
        if (end > start) out.push_back(text.substr(start, end - start));
        start = end + 1;
    }
    return out;
}

std::optional<int> profileNumber(std::string_view name) {
    int value = 0;
    const auto digits = name.substr(8);
    const auto [end, ec] = std::from_chars(digits.data(), digits.data() + digits.size(), value);
    if (ec != std::errc() || end != digits.data() + digits.size()) return std::nullopt;
    return value;
}

}  // namespace

std::string_view browserName(BrowserKind kind) {
    switch (kind) {
        case BrowserKind::Chrome: return "Chrome";
        case BrowserKind::Edge: return "Microsoft Edge";
        case BrowserKind::Brave: return "Brave";
        case BrowserKind::Vivaldi: return "Vivaldi";
        case BrowserKind::Chromium: return "Chromium";
        case BrowserKind::Firefox: return "Firefox";
    }
    return "";
}

std::vector<ChromiumBrowser> chromiumBrowsers(const VendorEnvironment& env) {
    const auto config = env.configHome();
    const auto flatpak = [&](std::string_view app, std::string_view folder) {
        return env.inHome(".var/app/" + std::string(app) + "/config/" + std::string(folder));
    };
    return {
        {BrowserKind::Chrome, {fsx::join(config, "google-chrome"), flatpak("com.google.Chrome", "google-chrome")}, {"chrome"}},
        {BrowserKind::Edge,
         {fsx::join(config, "microsoft-edge"), flatpak("com.microsoft.Edge", "microsoft-edge")},
         {"microsoft-edge", "chromium"}},
        {BrowserKind::Brave,
         {fsx::join(config, "BraveSoftware/Brave-Browser"), env.inHome("snap/brave/current/.config/BraveSoftware/Brave-Browser"),
          flatpak("com.brave.Browser", "BraveSoftware/Brave-Browser")},
         {"brave"}},
        {BrowserKind::Vivaldi, {fsx::join(config, "vivaldi"), flatpak("com.vivaldi.Vivaldi", "vivaldi")}, {"vivaldi", "chrome"}},
        {BrowserKind::Chromium,
         {fsx::join(config, "chromium"), env.inHome("snap/chromium/common/chromium"), flatpak("org.chromium.Chromium", "chromium")},
         {"chromium"}},
    };
}

std::vector<std::string> chromiumCookieStores(Files& files, const std::string& userData) {
    std::vector<std::string> profiles;
    for (const auto& name : files.list(userData)) {
        if (name.rfind("Profile ", 0) == 0) profiles.push_back(name);
    }
    std::sort(profiles.begin(), profiles.end(), [](const std::string& a, const std::string& b) {
        const int na = profileNumber(a).value_or(std::numeric_limits<int>::max());
        const int nb = profileNumber(b).value_or(std::numeric_limits<int>::max());
        return na != nb ? na < nb : a < b;
    });
    profiles.insert(profiles.begin(), "Default");
    std::vector<std::string> out;
    for (const auto& profile : profiles) {
        const auto folder = fsx::join(userData, profile);
        for (const auto* name : {"Cookies", "Network/Cookies"}) {
            const auto store = fsx::join(folder, name);
            const auto info = files.stat(store);
            if (info && info->regular) {
                out.push_back(store);
                break;
            }
        }
    }
    return out;
}

std::optional<int> chromiumMajorVersion(Files& files, const std::string& userData) {
    const auto text = files.read(fsx::join(userData, "Last Version"), kVersionFileBytes);
    if (!text) return std::nullopt;
    const auto version = trim(*text);
    const auto major = version.substr(0, version.find('.'));
    int value = 0;
    const auto [end, ec] = std::from_chars(major.data(), major.data() + major.size(), value);
    if (ec != std::errc() || end != major.data() + major.size() || value <= 0 || value > 9999) return std::nullopt;
    return value;
}

std::vector<std::string> firefoxRoots(const VendorEnvironment& env) {
    return {
        env.inHome(".mozilla/firefox"),
        fsx::join(env.configHome(), "mozilla/firefox"),
        env.inHome("snap/firefox/common/.mozilla/firefox"),
        env.inHome(".var/app/org.mozilla.firefox/.mozilla/firefox"),
        env.inHome(".var/app/org.mozilla.firefox/config/mozilla/firefox"),
    };
}

std::vector<std::string> firefoxProfiles(std::string_view ini, const std::string& root) {
    std::vector<std::string> folders;
    bool inProfile = false, relative = true;
    std::optional<std::string> path;
    const auto flush = [&] {
        if (inProfile && path && !path->empty()) folders.push_back(relative ? fsx::join(root, *path) : *path);
        path.reset();
        relative = true;
    };
    for (const auto raw : lines(ini)) {
        const auto line = trim(raw);
        if (!line.empty() && line.front() == '[') {
            flush();
            inProfile = line.rfind("[Profile", 0) == 0;
            continue;
        }
        const auto equals = line.find('=');
        if (equals == std::string_view::npos) continue;
        const auto key = line.substr(0, equals);
        const auto value = line.substr(equals + 1);
        if (key == "Path") path = std::string(value);
        if (key == "IsRelative") relative = value != "0";
    }
    flush();
    std::vector<std::string> unique;
    for (auto& folder : folders) {
        if (std::find(unique.begin(), unique.end(), folder) == unique.end()) unique.push_back(std::move(folder));
    }
    return unique;
}

std::optional<std::string> firefoxVersion(Files& files, const std::string& profile) {
    const auto text = files.read(fsx::join(profile, "compatibility.ini"), kCompatibilityBytes);
    if (!text) return std::nullopt;
    for (const auto raw : lines(*text)) {
        const auto line = trim(raw);
        constexpr std::string_view kKey = "LastVersion=";
        if (line.substr(0, kKey.size()) != kKey) continue;
        const auto rest = line.substr(kKey.size());
        const auto value = rest.substr(0, rest.find('_'));
        if (value.empty() || value.size() > 32) return std::nullopt;
        for (const char c : value) {
            if (!((c >= '0' && c <= '9') || c == '.')) return std::nullopt;
        }
        return std::string(value);
    }
    return std::nullopt;
}

std::string firefoxPlatformToken(std::string_view architecture) {
    std::string arch(architecture.empty() ? std::string_view("x86_64") : architecture);
    for (const char c : arch) {
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) return "X11; Linux x86_64";
    }
    return "X11; Linux " + arch;
}

}  // namespace agentspeed::quota::cookies
