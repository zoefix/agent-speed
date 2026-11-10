#pragma once

#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <utility>
#include <vector>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/routes/fingerprint.hpp"
#include "agentspeed/quota/vendor_service.hpp"

// The vendor service's state and its request chains (vendor_service*.cpp, vendor_fetch.cpp).
namespace agentspeed::quota {

// What is kept of a sign-in file between requests: its version and the token's fingerprint and expiry, never the token.
struct FileProbe {
    std::string path;
    std::int64_t length = 0;
    std::int64_t writeNs = 0;
    std::optional<Fingerprint> account;
    std::optional<std::int64_t> expiresAtMs;
    bool sameFile(const std::string& other, const fsx::Stat& info) const {
        return path == other && length == info.length && writeNs == info.writeNs;
    }
    std::int64_t version() const { return writeNs ^ (length << 1); }
};

struct VendorService::RouteState {
    Cadence cadence;
    // The credential the snapshot belongs to.
    std::optional<Fingerprint> account;
    RouteReading reading;
    // Browser routes: when the session was last read (only when a request is due).
    std::optional<std::int64_t> checkedMs;
    // Gemini: the project of the current token, learned once per token.
    std::optional<std::pair<Fingerprint, std::optional<std::string>>> geminiProject;
    std::optional<FileProbe> probe;
    bool inFlight = false;
};

struct VendorService::FetchResult {
    VendorRoute route = VendorRoute::GrokBuildLive;
    std::uint64_t generation = 0;
    CredentialId credential;
    std::int64_t startedMs = 0;
    Cadence::Outcome outcome = Cadence::Outcome::Failure;
    std::vector<Window> windows;
    std::optional<std::int64_t> observedMs;
    int status = 0;
    bool noCertificates = false;
    // Gemini: the project `loadCodeAssist` gave (none when it named none).
    std::optional<std::optional<std::string>> geminiProject;
    // Antigravity: processes whose token a 401/403 made the reader forget.
    std::set<std::int64_t> forgetTokens;
    std::string host;
};

struct VendorService::Inbox {
    std::mutex mutex;
    std::vector<FetchResult> results;
    std::function<void()> wake;
};

namespace fetch {

struct Context {
    http::Transport* transport = nullptr;
    std::weak_ptr<VendorService::Inbox> inbox;
};

// Turns a 2xx JSON object into windows; nullopt when it is not the expected answer.
using Parse = std::function<std::optional<std::vector<Window>>(const json::Value& body, std::int64_t nowMs)>;

void single(const Context& context, VendorService::FetchResult base, http::Request request, Parse parse);
// `loadCodeAssist` when the token's project is not known yet, then `retrieveUserQuota`.
void gemini(const Context& context, VendorService::FetchResult base, std::string token,
            std::optional<std::optional<std::string>> project);
// The servers' addresses in order, until one answers.
void antigravity(const Context& context, VendorService::FetchResult base, std::vector<AntigravityServer> servers);

}  // namespace fetch

}  // namespace agentspeed::quota
