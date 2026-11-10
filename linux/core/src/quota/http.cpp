#include "agentspeed/quota/http.hpp"

#include <array>
#include <cctype>
#include <charconv>

#include "agentspeed/rfc3339.hpp"

namespace agentspeed::quota::http {

namespace {

constexpr std::array<std::string_view, 8> kHosts = {
    "cli-chat-proxy.grok.com", "api.kimi.com", "cloudcode-pa.googleapis.com", "api.factory.ai",
    "api.z.ai",                "open.bigmodel.cn", "qoder.com",           "qoder.com.cn",
};
constexpr std::string_view kWorkBuddyHost = "www.workbuddy.cn";
constexpr std::string_view kLoopback = "127.0.0.1";

bool startsWith(std::string_view text, std::string_view prefix) { return text.substr(0, prefix.size()) == prefix; }

std::optional<int> number(std::string_view text) {
    int value = 0;
    if (text.empty()) return std::nullopt;
    const auto [end, ec] = std::from_chars(text.data(), text.data() + text.size(), value);
    if (ec != std::errc() || end != text.data() + text.size()) return std::nullopt;
    return value;
}

std::optional<int> monthIndex(std::string_view name) {
    constexpr std::array<std::string_view, 12> kMonths = {"Jan", "Feb", "Mar", "Apr", "May", "Jun",
                                                          "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"};
    for (std::size_t i = 0; i < kMonths.size(); ++i) {
        if (kMonths[i] == name) return static_cast<int>(i) + 1;
    }
    return std::nullopt;
}

std::vector<std::string_view> fields(std::string_view text) {
    std::vector<std::string_view> out;
    std::size_t i = 0;
    while (i < text.size()) {
        while (i < text.size() && (text[i] == ' ' || text[i] == ',')) ++i;
        const std::size_t start = i;
        while (i < text.size() && text[i] != ' ' && text[i] != ',') ++i;
        if (i > start) out.push_back(text.substr(start, i - start));
    }
    return out;
}

std::optional<std::int64_t> civilMs(int year, int month, int day, std::string_view clock) {
    if (clock.size() != 8 || clock[2] != ':' || clock[5] != ':') return std::nullopt;
    const auto h = number(clock.substr(0, 2)), m = number(clock.substr(3, 2)), s = number(clock.substr(6, 2));
    if (!h || !m || !s || *h > 23 || *m > 59 || *s > 60 || month < 1 || month > 12 || day < 1 || day > 31) return std::nullopt;
    const auto days = daysFromCivil(year, static_cast<unsigned>(month), static_cast<unsigned>(day));
    return ((days * 24 + *h) * 60 + *m) * 60'000 + static_cast<std::int64_t>(*s) * 1000;
}

}  // namespace

bool allowedHost(std::string_view host) {
    if (host == kWorkBuddyHost) return true;
    for (const auto h : kHosts) {
        if (h == host) return true;
    }
    return false;
}

std::optional<std::string> hostOf(std::string_view url) {
    std::string_view rest;
    if (startsWith(url, "https://")) {
        rest = url.substr(8);
    } else if (startsWith(url, "http://")) {
        rest = url.substr(7);
    } else {
        return std::nullopt;
    }
    const auto end = rest.find_first_of("/?#");
    const auto authority = rest.substr(0, end);
    if (authority.empty() || authority.find('@') != std::string_view::npos) return std::nullopt;
    const auto colon = authority.find(':');
    const auto host = authority.substr(0, colon);
    if (host.empty()) return std::nullopt;
    for (const char c : host) {
        if (!(std::isalnum(static_cast<unsigned char>(c)) || c == '.' || c == '-')) return std::nullopt;
    }
    if (colon != std::string_view::npos) {
        const auto port = number(authority.substr(colon + 1));
        if (!port || *port < 1 || *port > 65535) return std::nullopt;
    }
    return std::string(host);
}

bool permitted(const Request& request) {
    const auto host = hostOf(request.url);
    if (!host) return false;
    if (request.loopback) {
        if (*host != kLoopback) return false;
        const auto rest = request.url.substr(request.url.find("//") + 2);
        return rest.find(':') != std::string::npos && rest.find(':') < rest.find('/');
    }
    if (!startsWith(request.url, "https://")) return false;
    const auto rest = std::string_view(request.url).substr(8);
    const auto authority = rest.substr(0, rest.find_first_of("/?#"));
    if (authority.find(':') != std::string_view::npos) return false;
    return allowedHost(*host);
}

std::optional<std::int64_t> parseHttpDate(std::string_view text) {
    const auto f = fields(text);
    // IMF-fixdate: Sun, 06 Nov 1994 08:49:37 GMT
    if (f.size() == 6 && f[5] == "GMT") {
        const auto day = number(f[1]);
        const auto month = monthIndex(f[2]);
        const auto year = number(f[3]);
        if (day && month && year) return civilMs(*year, *month, *day, f[4]);
        return std::nullopt;
    }
    // RFC 850: Sunday, 06-Nov-94 08:49:37 GMT
    if (f.size() == 4 && f[3] == "GMT" && f[1].size() == 9 && f[1][2] == '-' && f[1][6] == '-') {
        const auto day = number(f[1].substr(0, 2));
        const auto month = monthIndex(f[1].substr(3, 3));
        const auto yy = number(f[1].substr(7, 2));
        if (day && month && yy) return civilMs(*yy < 70 ? 2000 + *yy : 1900 + *yy, *month, *day, f[2]);
        return std::nullopt;
    }
    // asctime: Sun Nov  6 08:49:37 1994
    if (f.size() == 5) {
        const auto month = monthIndex(f[1]);
        const auto day = number(f[2]);
        const auto year = number(f[4]);
        if (day && month && year) return civilMs(*year, *month, *day, f[3]);
    }
    return std::nullopt;
}

}  // namespace agentspeed::quota::http
