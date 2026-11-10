#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// QUOTAS 9.10 Hosts: the `Cookie` header of one site from a browser store's rows.
namespace agentspeed::quota::cookies {

// One row of a browser store. `value` is plain text; a Chromium row may carry `encrypted` instead.
struct Cookie {
    std::string host;
    std::string name;
    std::string value;
    std::string encrypted;
    std::int64_t expiresS = 0;      // Unix seconds; 0 for a session cookie
    std::int64_t lastAccessMs = 0;  // Unix milliseconds; 0 when the store keeps none
    bool needsDecryption() const { return value.empty() && !encrypted.empty(); }
};

// `host_key` is the site or one of its subdomains (a leading dot allowed).
bool hostMatches(std::string_view host, std::string_view site);
bool live(const Cookie& cookie, std::int64_t nowS);
// The rows a header would use: the site's, unexpired, in database order.
std::vector<Cookie> usable(const std::vector<Cookie>& cookies, std::string_view site, std::int64_t nowS);
// A name or value that could break the header (a control character, `;` or `=` in a name) is never sent.
bool sendable(const Cookie& cookie);
// `name=value; …` of the usable, sendable rows; nullopt when none is left.
std::optional<std::string> joinHeader(const std::vector<Cookie>& cookies, std::string_view site, std::int64_t nowS);

// Overwrites the values and encrypted values before the rows go.
void wipe(std::vector<Cookie>& cookies);

}  // namespace agentspeed::quota::cookies
