#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/report.hpp"
#include "agentspeed/report_body.hpp"
#include "agentspeed/report_scheduler.hpp"

// The client's side of a service (REPORTING 2–4, 8): service URLs, request bodies, replies and their reactions, and what
// the user confirmed.
namespace agentspeed::report {

constexpr std::string_view kDiscoveryPath = "/.well-known/agent-speed-report";

enum class UrlProblem { Invalid, NotHttps };

// A normalized service URL (REPORTING 2.1, 2.2).
class ServiceUrl {
public:
    // REPORTING 2.1: trims, prefixes https:// when no scheme is given, then normalizes.
    static std::optional<ServiceUrl> parse(std::string_view input, UrlProblem* problem = nullptr);
    // A discovery document's `api`: http only when the document came over loopback http.
    static std::optional<ServiceUrl> parseApi(std::string_view text, bool discoveryIsLoopbackHttp);

    // scheme://host[:port][path]: lower-case host (IDNA A-labels), the port only when not the default, no trailing /.
    const std::string& text() const { return text_; }
    const std::string& scheme() const { return scheme_; }
    const std::string& host() const { return host_; }
    std::optional<int> port() const { return port_; }
    const std::string& path() const { return path_; }
    bool loopbackHttp() const { return scheme_ == "http"; }
    std::string authority() const { return port_ ? host_ + ":" + std::to_string(*port_) : host_; }
    std::string origin() const { return scheme_ + "://" + authority(); }
    std::string discoveryUrl() const { return text_ + std::string(kDiscoveryPath); }
    std::string endpoint(std::string_view path) const { return text_ + std::string(path); }
    bool operator==(const ServiceUrl& other) const { return text_ == other.text_; }

private:
    ServiceUrl(std::string scheme, std::string host, std::optional<int> port, std::string path);
    std::string scheme_, host_, path_, text_;
    std::optional<int> port_;
};

// A link of $defs/httpsUrl (REPORTING 2.2, 2.3), as given, or nullopt.
std::optional<std::string> httpsLink(std::string_view text);

// Why a fetched discovery document was not accepted (DESIGN 11, the fetch outcomes); `field` is for logs only.
struct DiscoveryProblem {
    enum class Kind { NotSupported, Invalid } kind = Kind::Invalid;
    std::string field;
};
// The body fetched from `service`'s discovery URL.
std::optional<Discovery> parseDiscoveryBody(std::string_view body, const ServiceUrl& service, DiscoveryProblem* problem = nullptr);
// What the preview assumes before a service's document is known: the API at the service URL, every section, the
// protocol's defaults and the taskScore extension (DESIGN 11, Preview window).
Discovery assumedDiscovery(const ServiceUrl& service, const std::string& name);
// The document as the client understood it, for storage; it parses back to the same values.
std::string discoveryJson(const Discovery& discovery);

// What the user confirmed in the consent sheet (REPORTING 3.3), stored with the identity.
struct ConsentPin {
    std::string name, terms, privacy, api;
    std::vector<std::string> sections;  // sorted
    std::optional<std::int64_t> retentionDays;
    std::string apiKeyMode = "none";

    static ConsentPin of(const Discovery& discovery);
    // True when `current` changes name, terms, privacy or api, adds a section, raises or removes retentionDays, or
    // switches apiKey.mode to required without a key.
    bool needsReview(const Discovery& current, bool hasApiKey) const;
    bool operator==(const ConsentPin&) const = default;
};

// One-click consent (REPORTING 3.3): the document adds nothing the invitation did not say.
bool invitationCovers(const Discovery& discovery, const ServiceUrl& service, std::string_view presetDomain, std::string_view username);

UsernameRules usernameRules(const Discovery& discovery);

// Request bodies (REPORTING 4.1, 4.4, 4.5).
std::string registrationBody(std::string_view deviceId, std::string_view username, const PublicKey& key, const ClientInfo& client, bool transfer);
std::string renameBody(std::string_view username);
std::string rotationBody(const PublicKey& key);

// A response of the service (REPORTING 8), validated: only known shapes and clamped numbers are kept.
struct Reply {
    int status = 0;
    std::optional<std::string> result, deviceId, username, profileUrl;
    bool transferred = false;
    std::optional<int> intervalSeconds, rank;
    std::optional<std::string> code, reason, field, errorUsername;
    std::optional<std::int64_t> serverTime;
    std::vector<int> supported;
    std::optional<int> retryAfterSeconds;  // from Retry-After or error.retryAfterSeconds, unclamped
    std::optional<std::string> logMessage;   // error.message without control characters, at most 200 characters

    bool success() const { return status >= 200 && status <= 299; }
    // Header names in lower case. A body that is not the expected JSON leaves only the status.
    static Reply parse(int status, const std::map<std::string, std::string>& headers, std::string_view body, std::int64_t nowSeconds);
};

// Seconds, or an HTTP date relative to `nowSeconds`.
std::optional<int> retryAfter(std::string_view value, std::int64_t nowSeconds);
bool isErrorCode(std::string_view text);
std::string sanitized(std::string_view text, std::size_t cap);

// What needs the user (DESIGN 11, the red statuses).
enum class Problem { None, ApiKey, UsernameTaken, UsernameRejected, KeyMismatch, Rejected, Upgrade, Gone, Forbidden };

// How the client reacts to a report's outcome (REPORTING 8.3, METRICS 11.5).
struct Reaction {
    enum class Kind {
        None,
        CorrectClock,         // timestamp_out_of_window: keep serverTime − local time (METRICS 11.6)
        ReRegister,           // unknown_device: register again (at most once per hour)
        RecheckRegistration,  // signature_invalid without a pending key
        UsePendingKey,        // signature_invalid with a pending key: attempted again at once, signed with it
        Rename,               // username_mismatch: rename to the user's chosen name once
        NewRun,               // seq_conflict
        RefreshDiscovery,     // version_unsupported (upgrade), payload_too_large
        Strike,               // stops reporting after three in a row
        NeedsUser,
    } kind = Kind::None;
    std::optional<std::int64_t> serverTime;
    std::optional<std::string> registered;
    bool upgrade = false, misconfigured = false;
    Problem problem = Problem::None;
};

// The scheduler's outcome and the reaction for a report attempt; a null reply is a transport failure.
std::pair<Outcome, Reaction> classify(const Reply* reply, bool hasPendingKey);
// The code a response without a usable error body stands for (REPORTING 8.3, last paragraph).
std::string statusCode(int status);

}  // namespace agentspeed::report
