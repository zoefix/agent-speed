#include "agentspeed/quota/vendor_service.hpp"

#include <algorithm>

#include "agentspeed/quota/sources.hpp"
#include "vendor_internal.hpp"

namespace agentspeed::quota {

namespace {

constexpr std::int64_t kLocalIntervalMs = 60'000;
// A local route counts as signed in while its snapshot is this fresh (QUOTAS 9.1).
constexpr std::int64_t kLocalSignedInMs = 24 * 3'600'000;

bool switchOn(const VendorInputs& in, std::string_view key) { return in.switchesOn.find(key) != in.switchesOn.end(); }

bool enabled(const VendorInputs& in, SourceKind source) { return in.enabledSources.count(source) > 0; }

}  // namespace

std::string_view vendorRouteId(VendorRoute route) {
    switch (route) {
        case VendorRoute::GrokBuild: return "grokBuild";
        case VendorRoute::GrokBuildLive: return "grokBuildLive";
        case VendorRoute::Devin: return "devin";
        case VendorRoute::Antigravity: return "antigravity";
        case VendorRoute::Kimi: return "kimi";
        case VendorRoute::Gemini: return "gemini";
        case VendorRoute::Droid: return "droid";
        case VendorRoute::Zcode: return "zcode";
        case VendorRoute::Qoder: return "qoder";
        case VendorRoute::WorkBuddy: return "workbuddy";
    }
    return "";
}

SourceKind vendorRouteSource(VendorRoute route) {
    switch (route) {
        case VendorRoute::GrokBuild:
        case VendorRoute::GrokBuildLive: return SourceKind::GrokBuild;
        case VendorRoute::Devin: return SourceKind::Devin;
        case VendorRoute::Antigravity: return SourceKind::Antigravity;
        case VendorRoute::Kimi: return SourceKind::Kimi;
        case VendorRoute::Gemini: return SourceKind::Gemini;
        case VendorRoute::Droid: return SourceKind::Droid;
        case VendorRoute::Zcode: return SourceKind::ZCode;
        case VendorRoute::Qoder: return SourceKind::Qoder;
        case VendorRoute::WorkBuddy: return SourceKind::WorkBuddy;
    }
    return SourceKind::GrokBuild;
}

bool isLocalRoute(VendorRoute route) { return route == VendorRoute::GrokBuild || route == VendorRoute::Devin; }

bool readsBrowser(VendorRoute route) { return route == VendorRoute::Qoder || route == VendorRoute::WorkBuddy; }

std::set<VendorRoute> hostRoutes() {
    return {VendorRoute::GrokBuild, VendorRoute::GrokBuildLive, VendorRoute::Antigravity, VendorRoute::Kimi,
            VendorRoute::Gemini, VendorRoute::Droid};
}

std::set<VendorRoute> activeRoutes(const VendorInputs& in) {
    std::set<VendorRoute> out;
    for (const auto route : kVendorRoutes) {
        const SourceKind source = vendorRouteSource(route);
        const auto* entry = routeOf(source);
        if (!entry || !switchOn(in, entry->switchKey)) continue;
        if (route == VendorRoute::Qoder) {
            if (!enabled(in, SourceKind::Qoder) && !enabled(in, SourceKind::QoderCli)) continue;
        } else if (!enabled(in, source)) {
            continue;
        }
        if (route == VendorRoute::GrokBuildLive && !switchOn(in, kGrokLiveSwitch)) continue;
        if (route == VendorRoute::Qoder && !in.qoderConsent) continue;
        if (route == VendorRoute::WorkBuddy && !in.workBuddyConsent) continue;
        out.insert(route);
    }
    return out;
}

VendorInputs hostInputs(const std::set<SourceKind>& sources, bool network, bool grokLive, std::set<SourceKind> running,
                        std::int64_t nowMs) {
    VendorInputs in;
    in.nowMs = nowMs;
    in.networkEnabled = network;
    in.running = std::move(running);
    for (const auto source : sources) {
        const auto* entry = routeOf(source);
        if (!entry) continue;
        in.enabledSources.insert(source);
        in.switchesOn.insert(std::string(entry->switchKey));
    }
    if (grokLive) in.switchesOn.insert(std::string(kGrokLiveSwitch));
    return in;
}

// ---- Readings ----

std::optional<Snapshot> VendorReadings::snapshot(SourceKind source) const {
    const SourceKind key = source == SourceKind::QoderCli ? SourceKind::Qoder : source;
    std::optional<Snapshot> newest;
    for (const auto& [route, reading] : routes) {
        if (vendorRouteSource(route) != key || !reading.snapshot) continue;
        if (!newest || reading.snapshot->observedMs > newest->observedMs) newest = reading.snapshot;
    }
    if (newest) newest->source = source;
    return newest;
}

bool VendorReadings::signedIn(SourceKind source) const {
    const SourceKind key = source == SourceKind::QoderCli ? SourceKind::Qoder : source;
    return std::any_of(routes.begin(), routes.end(),
                       [&](const auto& entry) { return vendorRouteSource(entry.first) == key && entry.second.signedIn; });
}

bool VendorReadings::runs(SourceKind source) const {
    return running.count(source) > 0 || (source == SourceKind::Antigravity && antigravityRuns);
}

std::optional<VendorStatus> VendorReadings::status(VendorRoute route) const {
    const auto found = routes.find(route);
    if (found == routes.end()) return std::nullopt;
    return found->second.status;
}

std::optional<VendorStatus> VendorReadings::status(SourceKind source) const {
    switch (source) {
        case SourceKind::GrokBuild: return status(VendorRoute::GrokBuild);
        case SourceKind::Devin: return status(VendorRoute::Devin);
        case SourceKind::Antigravity: return status(VendorRoute::Antigravity);
        case SourceKind::Kimi: return status(VendorRoute::Kimi);
        case SourceKind::Gemini: return status(VendorRoute::Gemini);
        case SourceKind::Droid: return status(VendorRoute::Droid);
        case SourceKind::ZCode: return status(VendorRoute::Zcode);
        case SourceKind::Qoder:
        case SourceKind::QoderCli: return status(VendorRoute::Qoder);
        case SourceKind::WorkBuddy: return status(VendorRoute::WorkBuddy);
        default: return std::nullopt;
    }
}

// ---- Service ----

VendorService::VendorService(VendorEnvironment env, http::Transport& transport, SecretStore& secrets, VendorServiceOptions options)
    : env_(std::move(env)), transport_(transport), secrets_(secrets), options_(std::move(options)), inbox_(std::make_shared<Inbox>()) {
    inbox_->wake = options_.wake;
    if (!env_.files) env_.files = systemFiles();
}

VendorService::~VendorService() = default;

bool VendorService::busy() const {
    return std::any_of(states_.begin(), states_.end(), [](const auto& entry) { return entry.second->inFlight; });
}

VendorService::RouteState& VendorService::state(VendorRoute route) {
    auto& slot = states_[route];
    if (!slot) slot = std::make_unique<RouteState>();
    return *slot;
}

cookies::SessionReader& VendorService::browser() {
    if (!options_.browser) options_.browser = std::make_shared<cookies::BrowserCookieReader>(env_, secrets_);
    return *options_.browser;
}

AntigravityLocator& VendorService::antigravity() {
    if (!options_.antigravity) options_.antigravity = std::make_shared<ProcAntigravityLocator>(env_);
    return *options_.antigravity;
}

void VendorService::forget(VendorRoute route) {
    states_.erase(route);
    ++generations_[route];
    if (route == VendorRoute::GrokBuild) grokLog_ = GrokLogReader();
    if (route == VendorRoute::Devin) devin_ = DevinPlanReader();
    if (route == VendorRoute::Antigravity) {
        antigravityRuns_ = false;
        forgottenTokens_.clear();
    }
}

void VendorService::zcodeKeyChanged() {
    forget(VendorRoute::Zcode);
    zcodeRefused_ = false;
    rebuild();
}

const VendorReadings& VendorService::tick(const VendorInputs& in) {
    inputs_ = in;
    drain();
    auto active = activeRoutes(in);
    for (auto it = active.begin(); it != active.end();) it = options_.available.count(*it) ? std::next(it) : active.erase(it);
    std::set<VendorRoute> turnedOn;
    for (const auto route : kVendorRoutes) {
        if (!active.count(route)) {
            if (active_.count(route) || states_.count(route)) forget(route);
        } else if (!active_.count(route)) {
            turnedOn.insert(route);
        }
    }
    const bool browserWasOn = active_.count(VendorRoute::Qoder) || active_.count(VendorRoute::WorkBuddy);
    const bool browserOn = active.count(VendorRoute::Qoder) || active.count(VendorRoute::WorkBuddy);
    // Turning the browser routes off and on again asks the Secret Service again.
    if (browserWasOn && !browserOn && options_.browser) options_.browser->resetRefusals();
    active_ = active;
    if (zcodeRegion_ && *zcodeRegion_ != in.zcodeRegion) forget(VendorRoute::Zcode);
    zcodeRegion_ = in.zcodeRegion;

    readLocal(in, active, turnedOn);
    for (const auto route : kVendorRoutes) {
        if (active.count(route) && !isLocalRoute(route)) plan(route, in);
    }
    drain();
    rebuild();
    return readings_;
}

// ---- Local files (QUOTAS 9.2 log, 9.3) ----

void VendorService::readLocal(const VendorInputs& in, const std::set<VendorRoute>& active, const std::set<VendorRoute>& turnedOn) {
    const bool due = in.readLocalNow || !localCheckedMs_ || in.nowMs - *localCheckedMs_ >= kLocalIntervalMs;
    for (const auto route : {VendorRoute::GrokBuild, VendorRoute::Devin}) {
        if (!active.count(route)) continue;
        if (!due && !turnedOn.count(route)) {
            auto& st = state(route);
            if (st.reading.snapshot) st.reading.signedIn = in.nowMs - st.reading.snapshot->observedMs <= kLocalSignedInMs;
            continue;
        }
        if (route == VendorRoute::GrokBuild) {
            grokLog_.refresh(env_);
            updateLocal(route, grokLog_.snapshot(), in.nowMs);
        } else {
            devin_.refresh(env_);
            updateLocal(route, devin_.snapshot(), in.nowMs);
        }
    }
    if (due) localCheckedMs_ = in.nowMs;
}

void VendorService::updateLocal(VendorRoute route, const std::optional<Snapshot>& snapshot, std::int64_t nowMs) {
    auto& st = state(route);
    st.reading.snapshot = snapshot;
    if (snapshot) {
        st.reading.status = VendorStatus{VendorStatusKind::Ok, snapshot->observedMs, {}};
    } else {
        st.reading.status = VendorStatus{VendorStatusKind::NoCredential, 0, {}};
    }
    st.reading.signedIn = snapshot && nowMs - snapshot->observedMs <= kLocalSignedInMs;
}

// ---- Results ----

void VendorService::drain() {
    std::vector<FetchResult> results;
    {
        std::lock_guard lock(inbox_->mutex);
        results.swap(inbox_->results);
    }
    for (auto& result : results) apply(std::move(result));
}

void VendorService::apply(FetchResult result) {
    if (generations_[result.route] != result.generation || !active_.count(result.route)) return;
    auto& st = state(result.route);
    st.inFlight = false;
    if (st.account && *st.account != result.credential.account) return;
    st.account = result.credential.account;
    st.cadence.record(result.outcome, result.startedMs, result.credential);
    if (result.geminiProject) st.geminiProject = std::make_pair(result.credential.account, *result.geminiProject);
    forgottenTokens_.insert(result.forgetTokens.begin(), result.forgetTokens.end());
    const std::string host = st.reading.host.value_or(result.host);
    const bool browserRoute = readsBrowser(result.route);
    switch (result.outcome) {
        case Cadence::Outcome::Success: {
            const std::int64_t observed = result.observedMs.value_or(result.startedMs);
            st.reading.snapshot = Snapshot{vendorRouteSource(result.route), observed, std::move(result.windows)};
            st.reading.status = VendorStatus{VendorStatusKind::Ok, observed, {}};
            if (result.route == VendorRoute::Antigravity || browserRoute) st.reading.signedIn = true;
            break;
        }
        case Cadence::Outcome::Denied:
            st.reading.status = VendorStatus{VendorStatusKind::Denied, 0, host};
            if (result.route == VendorRoute::Antigravity || browserRoute) st.reading.signedIn = false;
            break;
        case Cadence::Outcome::Failure:
            st.reading.status = VendorStatus{result.noCertificates ? VendorStatusKind::NoCertificates : VendorStatusKind::Failed, 0, host};
            if (result.route == VendorRoute::Antigravity) st.reading.signedIn = false;
            break;
    }
    if (result.outcome != Cadence::Outcome::Success && options_.log) options_.log(host, result.status);
}

void VendorService::rebuild() {
    VendorReadings next;
    for (const auto& [route, st] : states_) next.routes[route] = st->reading;
    next.antigravityRuns = antigravityRuns_;
    next.running = inputs_.running;
    readings_ = std::move(next);
}

}  // namespace agentspeed::quota
