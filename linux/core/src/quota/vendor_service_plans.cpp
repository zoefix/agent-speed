#include <algorithm>

#include "agentspeed/quota/routes/credentials.hpp"
#include "agentspeed/quota/routes/requests.hpp"
#include "agentspeed/quota/vendor_service.hpp"
#include "agentspeed/quota/vendors/parsers.hpp"
#include "vendor_internal.hpp"

// Which request a round starts (QUOTAS 9.1-9.10): each route reads its credential, decides whether to ask, and hands
// the request to its chain.
namespace agentspeed::quota {

namespace {

// Grok's token is not used within a minute of its expiry (QUOTAS 9.2).
constexpr std::int64_t kGrokExpiryMarginMs = 60'000;

const std::vector<std::string>& cookieSites(VendorRoute route) {
    static const std::vector<std::string> kQoder = {std::string(requests::hosts::kQoder), std::string(requests::hosts::kQoderCn)};
    static const std::vector<std::string> kWorkBuddy = {"workbuddy.cn"};
    static const std::vector<std::string> kNone;
    if (route == VendorRoute::Qoder) return kQoder;
    if (route == VendorRoute::WorkBuddy) return kWorkBuddy;
    return kNone;
}

VendorStatus statusOf(VendorStatusKind kind, std::string host = {}) { return VendorStatus{kind, 0, std::move(host)}; }

}  // namespace

std::string VendorService::hostOf(VendorRoute route, const VendorInputs& in) const {
    switch (route) {
        case VendorRoute::GrokBuild:
        case VendorRoute::GrokBuildLive: return std::string(requests::hosts::kGrok);
        case VendorRoute::Devin:
        case VendorRoute::Antigravity: return std::string(requests::hosts::kLoopback);
        case VendorRoute::Kimi: return std::string(requests::hosts::kKimi);
        case VendorRoute::Gemini: return std::string(requests::hosts::kGemini);
        case VendorRoute::Droid: return std::string(requests::hosts::kFactory);
        case VendorRoute::Zcode: return std::string(requests::zaiHost(in.zcodeRegion));
        case VendorRoute::Qoder: return std::string(requests::hosts::kQoder);
        case VendorRoute::WorkBuddy: return std::string(requests::hosts::kWorkBuddy);
    }
    return {};
}

bool VendorService::asks(VendorRoute route, const VendorInputs& in) const {
    if (in.dashboardOpen) return true;
    if (route == VendorRoute::Qoder) return in.running.count(SourceKind::Qoder) || in.running.count(SourceKind::QoderCli);
    if (route == VendorRoute::Antigravity && antigravityRuns_) return true;
    return in.running.count(vendorRouteSource(route)) > 0;
}

// Whether a request may go out now for `credential`; otherwise the status says why not. Another account drops the old
// one's numbers.
bool VendorService::gate(VendorRoute route, const VendorInputs& in, const CredentialId& credential, RouteState& st,
                         std::optional<std::string> host) {
    if (st.account && *st.account != credential.account) {
        st.reading.snapshot.reset();
        st.reading.status.reset();
        st.geminiProject.reset();
    }
    st.account = credential.account;
    const std::string where = host ? *host : hostOf(route, in);
    st.reading.host = where;
    if (!in.networkEnabled) {
        st.reading.status = statusOf(VendorStatusKind::NetworkOff);
        return false;
    }
    if (st.cadence.isDenied(credential)) {
        st.reading.status = statusOf(VendorStatusKind::Denied, where);
        return false;
    }
    if (!asks(route, in)) {
        if (!st.reading.status || st.reading.status->kind == VendorStatusKind::NetworkOff) {
            st.reading.status = statusOf(VendorStatusKind::NotRunning);
        }
        return false;
    }
    return st.cadence.isDue(in.nowMs, credential);
}

// The routes that only look for their credential when a request could go out say why nothing goes out.
void VendorService::passiveStatus(VendorRoute route, const VendorInputs& in, RouteState& st) {
    if (!in.networkEnabled) {
        st.reading.status = statusOf(VendorStatusKind::NetworkOff);
    } else if (!asks(route, in)) {
        if (!st.reading.status || st.reading.status->kind == VendorStatusKind::NetworkOff) {
            st.reading.status = statusOf(VendorStatusKind::NotRunning);
        }
    }
}

void VendorService::plan(VendorRoute route, const VendorInputs& in) {
    auto& st = state(route);
    if (st.inFlight) return;
    // With the network off nothing is asked, so no credential, browser or process is looked at either.
    if (!in.networkEnabled) {
        st.reading.status = statusOf(VendorStatusKind::NetworkOff);
        return;
    }
    switch (route) {
        case VendorRoute::GrokBuildLive:
        case VendorRoute::Kimi:
        case VendorRoute::Gemini: planFileToken(route, in); break;
        case VendorRoute::Droid: planDroid(in); break;
        case VendorRoute::Zcode: planZcode(in); break;
        case VendorRoute::Antigravity: planAntigravity(in); break;
        case VendorRoute::Qoder:
        case VendorRoute::WorkBuddy: planBrowser(route, in); break;
        case VendorRoute::GrokBuild:
        case VendorRoute::Devin: break;
    }
}

// Grok live, Kimi, Gemini: the sign-in file is read when it changed (for the signed-in state: only its fingerprint and
// expiry are kept) and right before a request.
void VendorService::planFileToken(VendorRoute route, const VendorInputs& in) {
    auto& st = state(route);
    auto& files = env_.fs();
    std::optional<std::string> path;
    std::optional<Token> (*parse)(std::string_view) = nullptr;
    std::int64_t margin = 0;
    switch (route) {
        case VendorRoute::GrokBuildLive:
            path = grokCredentialFile(env_);
            parse = grokToken;
            margin = kGrokExpiryMarginMs;
            break;
        case VendorRoute::Kimi:
            path = kimiCredentialFile(env_);
            parse = kimiToken;
            break;
        default:
            path = geminiCredentialFile(env_);
            parse = geminiToken;
            break;
    }
    const auto info = path ? files.stat(*path) : std::nullopt;
    if (!info || !info->regular) {
        st.probe.reset();
        st.reading.status = statusOf(VendorStatusKind::NoCredential);
        st.reading.signedIn = false;
        return;
    }
    std::optional<Token> token;
    if (!st.probe || !st.probe->sameFile(*path, *info)) {
        token = readToken(files, *path, parse);
        if (token && !headerSafe(token->value.value())) token.reset();
        st.probe = FileProbe{*path, info->length, info->writeNs, std::nullopt, std::nullopt};
        if (token) {
            st.probe->account = token->value.fingerprint();
            st.probe->expiresAtMs = token->expiresAtMs;
        }
    }
    if (!st.probe->account) {
        st.reading.status = statusOf(VendorStatusKind::NoCredential);
        st.reading.signedIn = false;
        return;
    }
    const auto expires = st.probe->expiresAtMs;
    if (expires && in.nowMs >= *expires - margin) {
        // The token's own expiry has passed: no request, and the last snapshot stays with its age.
        st.reading.status = statusOf(VendorStatusKind::Expired);
        st.reading.signedIn = false;
        return;
    }
    st.reading.signedIn = true;
    const CredentialId credential{*st.probe->account, st.probe->version()};
    if (!gate(route, in, credential, st)) return;
    if (!token) {
        token = readToken(files, *path, parse);
        if (!token || !headerSafe(token->value.value()) || token->value.fingerprint() != credential.account) {
            st.probe.reset();  // it changed in between: looked at again next round
            return;
        }
    }
    FetchResult base;
    base.route = route;
    base.generation = generations_[route];
    base.credential = credential;
    base.startedMs = in.nowMs;
    base.host = hostOf(route, in);
    st.inFlight = true;
    const fetch::Context context{&transport_, inbox_};
    if (route == VendorRoute::Gemini) {
        std::optional<std::optional<std::string>> known;
        if (st.geminiProject && st.geminiProject->first == credential.account) known = st.geminiProject->second;
        fetch::gemini(context, std::move(base), token->value.value(), std::move(known));
    } else if (route == VendorRoute::Kimi) {
        fetch::single(context, std::move(base), requests::kimiUsages(token->value.value()),
                      [](const json::Value& body, std::int64_t) { return std::optional(vendors::kimiWindows(body)); });
    } else {
        fetch::single(context, std::move(base), requests::grokBilling(token->value.value()),
                      [](const json::Value& body, std::int64_t) { return vendors::grokFromResponse(body); });
    }
}

// Droid: `FACTORY_API_KEY` from the environment, else the `.env` line, both looked at each round (an environment
// value is already in memory; the file only when it changed).
void VendorService::planDroid(const VendorInputs& in) {
    auto& st = state(VendorRoute::Droid);
    auto& files = env_.fs();
    std::optional<Secret> key;
    std::int64_t version = 0;
    if (auto variable = env_.get("FACTORY_API_KEY")) {
        wipe(*variable);
        key = droidKey(env_);
        st.probe.reset();
    } else {
        const auto path = droidEnvFile(env_);
        const auto info = files.stat(path);
        if (!info || !info->regular) {
            st.probe.reset();
        } else {
            if (!st.probe || !st.probe->sameFile(path, *info)) {
                key = droidKey(env_);
                st.probe = FileProbe{path, info->length, info->writeNs, std::nullopt, std::nullopt};
                if (key && headerSafe(key->value())) st.probe->account = key->fingerprint();
            }
            version = st.probe->version();
        }
    }
    std::optional<Fingerprint> account;
    if (key && headerSafe(key->value())) {
        account = key->fingerprint();
    } else if (!key && st.probe) {
        account = st.probe->account;
    }
    if (!account) {
        st.reading.status = statusOf(VendorStatusKind::NoCredential);
        st.reading.signedIn = false;
        return;
    }
    st.reading.signedIn = true;
    const CredentialId credential{*account, version};
    if (!gate(VendorRoute::Droid, in, credential, st)) return;
    if (!key) key = droidKey(env_);
    if (!key || !headerSafe(key->value()) || key->fingerprint() != *account) {
        st.probe.reset();
        return;
    }
    FetchResult base;
    base.route = VendorRoute::Droid;
    base.generation = generations_[VendorRoute::Droid];
    base.credential = credential;
    base.startedMs = in.nowMs;
    base.host = hostOf(VendorRoute::Droid, in);
    st.inFlight = true;
    fetch::single(fetch::Context{&transport_, inbox_}, std::move(base), requests::droidLimits(key->value()),
                  [](const json::Value& body, std::int64_t nowMs) { return std::optional(vendors::droidWindows(body, nowMs)); });
}

// ZCode: the secret store is read only when a request could go out; a refusal is not asked again until the key
// changes or the app restarts.
void VendorService::planZcode(const VendorInputs& in) {
    auto& st = state(VendorRoute::Zcode);
    if (!in.networkEnabled || !asks(VendorRoute::Zcode, in)) {
        passiveStatus(VendorRoute::Zcode, in, st);
        return;
    }
    if (st.cadence.lastOutcome() == Cadence::Outcome::Denied || !st.cadence.waited(in.nowMs)) return;
    if (zcodeRefused_) return;
    auto lookup = secrets_.lookup(zcodeKeyAttributes());
    if (lookup.result == SecretResult::Refused) zcodeRefused_ = true;
    Secret key(lookup.result == SecretResult::Found ? std::move(lookup.secret) : std::string());
    wipe(lookup.secret);
    if (key.empty() || !headerSafe(key.value())) {
        // No key saved: the key field itself says what to do.
        st.reading.status.reset();
        st.reading.signedIn = false;
        return;
    }
    st.reading.signedIn = true;
    const CredentialId credential{fingerprintOf(key.value() + "|" + std::string(requests::zaiRegionId(in.zcodeRegion))), 0};
    if (!gate(VendorRoute::Zcode, in, credential, st)) return;
    FetchResult base;
    base.route = VendorRoute::Zcode;
    base.generation = generations_[VendorRoute::Zcode];
    base.credential = credential;
    base.startedMs = in.nowMs;
    base.host = hostOf(VendorRoute::Zcode, in);
    st.inFlight = true;
    fetch::single(fetch::Context{&transport_, inbox_}, std::move(base), requests::zaiLimits(key.value(), in.zcodeRegion),
                  [](const json::Value& body, std::int64_t nowMs) { return vendors::zaiWindows(body, nowMs); });
}

// Antigravity: the servers are looked for only when a request could go out.
void VendorService::planAntigravity(const VendorInputs& in) {
    auto& st = state(VendorRoute::Antigravity);
    if (!in.networkEnabled) {
        st.reading.status = statusOf(VendorStatusKind::NetworkOff);
        return;
    }
    if (!in.dashboardOpen && !in.running.count(SourceKind::Antigravity)) {
        antigravityRuns_ = false;
        st.reading.status = statusOf(VendorStatusKind::NotRunning);
        return;
    }
    if (!st.cadence.waited(in.nowMs)) return;
    auto servers = antigravity().servers();
    antigravityRuns_ = !servers.empty();
    std::set<std::int64_t> pids;
    for (const auto& server : servers) pids.insert(server.pid);
    for (auto it = forgottenTokens_.begin(); it != forgottenTokens_.end();) it = pids.count(*it) ? std::next(it) : forgottenTokens_.erase(it);
    if (servers.empty()) {
        st.reading.status = statusOf(VendorStatusKind::NotRunning);
        st.reading.signedIn = false;
        return;
    }
    std::string identity;
    for (auto& server : servers) {
        identity += std::to_string(server.pid) + ":";
        for (auto& endpoint : server.endpoints) {
            if (forgottenTokens_.count(server.pid) && endpoint.token) {
                wipe(*endpoint.token);
                endpoint.token.reset();
            }
            identity += endpoint.scheme + std::to_string(endpoint.port) + endpoint.token.value_or("") + ",";
        }
        identity += ";";
    }
    const CredentialId credential{fingerprintOf(identity), 0};
    wipe(identity);
    if (!gate(VendorRoute::Antigravity, in, credential, st)) return;
    FetchResult base;
    base.route = VendorRoute::Antigravity;
    base.generation = generations_[VendorRoute::Antigravity];
    base.credential = credential;
    base.startedMs = in.nowMs;
    base.host = std::string(requests::hosts::kLoopback);
    st.inFlight = true;
    fetch::antigravity(fetch::Context{&transport_, inbox_}, std::move(base), std::move(servers));
}

// Qoder, WorkBuddy: the browser is read only when a request is due: every 5 minutes, 15 after a failure or a refusal
// (a refused session is asked again only once its cookie changed).
void VendorService::planBrowser(VendorRoute route, const VendorInputs& in) {
    auto& st = state(route);
    if (!in.networkEnabled || !asks(route, in)) {
        passiveStatus(route, in, st);
        return;
    }
    const std::int64_t wait = st.cadence.lastOutcome() == Cadence::Outcome::Success ? Cadence::kIntervalMs : Cadence::kFailureWaitMs;
    if (st.checkedMs && in.nowMs - *st.checkedMs < wait) return;
    st.checkedMs = in.nowMs;
    const bool wasDenied = st.cadence.lastOutcome() == Cadence::Outcome::Denied && st.cadence.credential().has_value();
    const CredentialId refused = wasDenied ? *st.cadence.credential() : CredentialId{};
    auto session = browser().session(cookieSites(route), in.nowMs);
    if (!session || !headerSafe(session->cookie.value()) || !headerSafe(session->userAgent)) {
        st.reading.status = statusOf(VendorStatusKind::NoBrowser);
        st.reading.signedIn = false;
        st.cadence.record(Cadence::Outcome::Failure, in.nowMs, st.cadence.credential().value_or(CredentialId{}));
        return;
    }
    const CredentialId credential{session->fingerprint(), 0};
    const std::string host = route == VendorRoute::Qoder ? session->site : hostOf(route, in);
    if (wasDenied && refused == credential) {
        st.reading.status = statusOf(VendorStatusKind::Denied, host);
        st.reading.signedIn = false;
        return;
    }
    if (st.account && *st.account != credential.account) st.reading.snapshot.reset();
    st.account = credential.account;
    st.reading.host = host;
    st.reading.signedIn = true;
    std::optional<http::Request> request;
    fetch::Parse parse;
    if (route == VendorRoute::Qoder) {
        request = requests::qoderCredits(session->cookie.value(), session->site);
        parse = [](const json::Value& body, std::int64_t) { return std::optional(vendors::qoderWindows(body)); };
    } else {
        request = requests::workBuddySummary(session->cookie.value(), session->userAgent);
        parse = [](const json::Value& body, std::int64_t) { return vendors::workBuddyWindows(body); };
    }
    if (!request) {
        st.reading.status = statusOf(VendorStatusKind::NoBrowser);
        st.reading.signedIn = false;
        return;
    }
    FetchResult base;
    base.route = route;
    base.generation = generations_[route];
    base.credential = credential;
    base.startedMs = in.nowMs;
    base.host = host;
    st.inFlight = true;
    fetch::single(fetch::Context{&transport_, inbox_}, std::move(base), std::move(*request), std::move(parse));
}

}  // namespace agentspeed::quota
