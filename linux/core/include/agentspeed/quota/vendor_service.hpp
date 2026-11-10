#pragma once

#include <array>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <string_view>

#include "agentspeed/model.hpp"
#include "agentspeed/quota/cookies/reader.hpp"
#include "agentspeed/quota/http.hpp"
#include "agentspeed/quota/routes/antigravity.hpp"
#include "agentspeed/quota/routes/cadence.hpp"
#include "agentspeed/quota/routes/environment.hpp"
#include "agentspeed/quota/routes/local.hpp"
#include "agentspeed/quota/routes/requests.hpp"
#include "agentspeed/quota/secrets.hpp"
#include "agentspeed/quota/window.hpp"

// QUOTAS 9: the phase 2 readers behind one service. Local files when they change (checked every 60 s), the loopback
// language server, and the vendor requests with the agents' own sign-ins, the user's ZCode key and the browser's
// session, each under the rules of 9.1 and NETWORK 9. Credentials are read right before their request and live only
// inside it.
//
// Threading: every call, and the transport's completions, on one thread (the app's quota worker; the relay's loop).
// A completion is queued and applied by the next `tick`; `wake` asks for that tick soon.
namespace agentspeed::quota {

// One reader per switch. Grok Build's log and its live request share the `grokBuild` snapshot (the newer wins);
// Qoder's one account serves both Qoder sources.
enum class VendorRoute : std::uint8_t { GrokBuild, GrokBuildLive, Devin, Antigravity, Kimi, Gemini, Droid, Zcode, Qoder, WorkBuddy };
inline constexpr std::array<VendorRoute, 10> kVendorRoutes = {
    VendorRoute::GrokBuild, VendorRoute::GrokBuildLive, VendorRoute::Devin, VendorRoute::Antigravity, VendorRoute::Kimi,
    VendorRoute::Gemini,    VendorRoute::Droid,         VendorRoute::Zcode, VendorRoute::Qoder,       VendorRoute::WorkBuddy,
};
std::string_view vendorRouteId(VendorRoute route);
SourceKind vendorRouteSource(VendorRoute route);
// No network: Grok Build's log, Devin.
bool isLocalRoute(VendorRoute route);
// The browser's session (QUOTAS 9.10): off by default, on only after the user agreed.
bool readsBrowser(VendorRoute route);
// The CLI routes a remote host reads itself (REMOTE 11): Grok Build (log and live), Antigravity, Kimi, Gemini, Droid.
std::set<VendorRoute> hostRoutes();

// What a round may do.
struct VendorInputs {
    std::int64_t nowMs = 0;
    bool networkEnabled = false;
    // DESIGN 5 Data sources.
    std::set<SourceKind> enabledSources;
    // The quota switches that are on: the keys of sources.hpp (`quotaGrokBuild`, `quotaQoder`, …) and
    // `kGrokLiveSwitch`.
    std::set<std::string, std::less<>> switchesOn;
    // The user agreed to read the site's browser session (QUOTAS 9.10, the dialog of DESIGN 5).
    bool qoderConsent = false;
    bool workBuddyConsent = false;
    requests::ZaiRegion zcodeRegion = requests::ZaiRegion::Global;
    // The sources whose app or agent runs (QUOTAS 6, METRICS 10.5).
    std::set<SourceKind> running;
    bool dashboardOpen = false;
    // Local files are read again at once (a switch turned on, Settings opened).
    bool readLocalNow = false;
};

// REMOTE 11.1 on the host: the config's `sources` (enabled, quota switch on), `network` and `grokLive` as inputs.
VendorInputs hostInputs(const std::set<SourceKind>& sources, bool network, bool grokLive, std::set<SourceKind> running,
                        std::int64_t nowMs);

// QUOTAS 9.1: a route reads only while its source is enabled (Qoder: either Qoder source) and its switch is on; Grok's
// live request also needs `quotaGrokBuild`; a browser route also needs the consent.
std::set<VendorRoute> activeRoutes(const VendorInputs& inputs);

// A route as the UI shows it.
struct RouteReading {
    std::optional<Snapshot> snapshot;
    std::optional<VendorStatus> status;  // none: nothing to say (the ZCode key field says what to do)
    bool signedIn = false;
    std::optional<std::string> host;  // the host a refusal or failure concerns
    bool operator==(const RouteReading&) const = default;
};

struct VendorReadings {
    std::map<VendorRoute, RouteReading> routes;
    // An Antigravity language server runs (QUOTAS 9.4 Runs).
    bool antigravityRuns = false;
    std::set<SourceKind> running;
    bool operator==(const VendorReadings&) const = default;

    // The source's snapshot: Grok Build's newer of log and live, Qoder's for both Qoder sources.
    std::optional<Snapshot> snapshot(SourceKind source) const;
    bool signedIn(SourceKind source) const;
    bool runs(SourceKind source) const;
    // The status line under a switch.
    std::optional<VendorStatus> status(VendorRoute route) const;
    std::optional<VendorStatus> status(SourceKind source) const;
};

struct VendorServiceOptions {
    // The routes this service may run: all on this computer, `hostRoutes()` on a remote host.
    std::set<VendorRoute> available{kVendorRoutes.begin(), kVendorRoutes.end()};
    // Null: the real readers.
    std::shared_ptr<cookies::SessionReader> browser;
    std::shared_ptr<AntigravityLocator> antigravity;
    // A completion is waiting for `tick` (may be called inside `tick` with a synchronous transport).
    std::function<void()> wake;
    // A failed request: its host and status (0 without an answer). Nothing else is ever logged.
    std::function<void(std::string_view host, int status)> log;
};

// A remote host has no secret store: its routes never need one (REMOTE 11).
class NoSecrets final : public SecretStore {
public:
    SecretLookup lookup(const SecretAttributes&) override { return {SecretResult::Unavailable, {}}; }
    bool store(const SecretAttributes&, const std::string&, const std::string&) override { return false; }
    bool remove(const SecretAttributes&) override { return false; }
};

class VendorService {
public:
    VendorService(VendorEnvironment env, http::Transport& transport, SecretStore& secrets, VendorServiceOptions options = {});
    ~VendorService();
    VendorService(const VendorService&) = delete;
    VendorService& operator=(const VendorService&) = delete;

    // One round: applies finished requests, forgets the routes that are off, reads the local files when due, starts
    // the due requests (and applies those a synchronous transport finished at once).
    const VendorReadings& tick(const VendorInputs& inputs);
    // The ZCode key was saved or removed: the route forgets its state and reads the key when its request is next due.
    void zcodeKeyChanged();
    const VendorReadings& readings() const { return readings_; }
    // A request is in flight.
    bool busy() const;

    struct Inbox;
    struct FetchResult;
    struct RouteState;

private:
    void forget(VendorRoute route);
    void readLocal(const VendorInputs& in, const std::set<VendorRoute>& active, const std::set<VendorRoute>& turnedOn);
    void updateLocal(VendorRoute route, const std::optional<Snapshot>& snapshot, std::int64_t nowMs);
    void plan(VendorRoute route, const VendorInputs& in);
    void planFileToken(VendorRoute route, const VendorInputs& in);
    void planDroid(const VendorInputs& in);
    void planZcode(const VendorInputs& in);
    void planAntigravity(const VendorInputs& in);
    void planBrowser(VendorRoute route, const VendorInputs& in);
    bool gate(VendorRoute route, const VendorInputs& in, const CredentialId& credential, RouteState& state,
              std::optional<std::string> host = std::nullopt);
    void passiveStatus(VendorRoute route, const VendorInputs& in, RouteState& state);
    bool asks(VendorRoute route, const VendorInputs& in) const;
    std::string hostOf(VendorRoute route, const VendorInputs& in) const;
    void drain();
    void apply(FetchResult result);
    void rebuild();
    RouteState& state(VendorRoute route);
    cookies::SessionReader& browser();
    AntigravityLocator& antigravity();

    VendorEnvironment env_;
    http::Transport& transport_;
    SecretStore& secrets_;
    VendorServiceOptions options_;
    std::shared_ptr<Inbox> inbox_;
    std::map<VendorRoute, std::unique_ptr<RouteState>> states_;
    std::map<VendorRoute, std::uint64_t> generations_;
    std::set<VendorRoute> active_;
    GrokLogReader grokLog_;
    DevinPlanReader devin_;
    std::optional<std::int64_t> localCheckedMs_;
    std::set<std::int64_t> forgottenTokens_;
    bool antigravityRuns_ = false;
    bool zcodeRefused_ = false;
    std::optional<requests::ZaiRegion> zcodeRegion_;
    VendorInputs inputs_;
    VendorReadings readings_;
};

}  // namespace agentspeed::quota
