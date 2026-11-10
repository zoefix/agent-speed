#pragma once

#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "agentspeed/quota/sources.hpp"
#include "agentspeed/quota/vendor_service.hpp"
#include "quota_vendor_support.hpp"

// The vendor service in a temporary home with every agent signed in, a scripted transport, a fake browser and a fake
// language server list.
namespace quota2_test {

using agentspeed::SourceKind;
using agentspeed::quota::VendorInputs;
using agentspeed::quota::VendorRoute;
using agentspeed::quota::VendorService;
using agentspeed::quota::VendorStatusKind;

constexpr std::int64_t kNow = 1'794'338'773'000;  // 2026-11-10 19:26:13 UTC
constexpr std::int64_t kMinute = 60'000;

class FakeSessions final : public agentspeed::quota::cookies::SessionReader {
public:
    struct Entry {
        std::string site, cookie, userAgent;
    };
    // By the first site asked for (`qoder.com`, `workbuddy.cn`).
    std::map<std::string, Entry> sessions;
    int calls = 0;
    int resets = 0;

    std::optional<agentspeed::quota::cookies::BrowserSession> session(const std::vector<std::string>& sites, std::int64_t) override {
        ++calls;
        const auto found = sites.empty() ? sessions.end() : sessions.find(sites.front());
        if (found == sessions.end()) return std::nullopt;
        agentspeed::quota::cookies::BrowserSession out;
        out.site = found->second.site;
        out.cookie = agentspeed::quota::Secret(found->second.cookie);
        out.userAgent = found->second.userAgent;
        return out;
    }
    void resetRefusals() override { ++resets; }
};

class FakeLocator final : public agentspeed::quota::AntigravityLocator {
public:
    std::vector<agentspeed::quota::AntigravityServer> list;
    int calls = 0;
    std::vector<agentspeed::quota::AntigravityServer> servers() override {
        ++calls;
        return list;
    }
};

inline const char* kFirefoxUa = "Mozilla/5.0 (X11; Linux x86_64; rv:144.0) Gecko/20100101 Firefox/144.0";

// A JSON answer for every vendor URL; `status` per URL fragment overrides.
struct Vendors {
    std::map<std::string, int> statuses;
    std::map<std::string, std::string> bodies = {
        {"cli-chat-proxy.grok.com", R"({"config": {"creditUsagePercent": 10, "billingPeriodEnd": "2026-11-12T00:00:00Z"}})"},
        {"api.kimi.com", R"({"usages": {"limit_5h": {"used_ratio": 0.5, "reset_time": "2026-11-10T22:00:00Z"}}})"},
        {"loadCodeAssist", R"({"cloudaicompanionProject": "proj-1"})"},
        {"retrieveUserQuota", R"({"buckets": [{"modelId": "gemini-3-pro", "remainingFraction": 0.5}]})"},
        {"api.factory.ai", R"({"limits": {"standard": {"weekly": {"usedPercent": 20}}}})"},
        {"quota/limit", R"({"success": true, "code": 200, "data": {"limits": [{"type": "TIME_LIMIT", "unit": 5, "number": 1, "percentage": 10}]}})"},
        {"big_model_credits", R"({"totalQuota": {"quotaSummary": {"usagePercentage": 42}}})"},
        {"workbuddy.cn", R"({"code": 0, "data": {"Packages": [{"CycleTotalCapacity": "10", "CycleRemainCapacity": "5", "CapacityUnit": "credits"}]}})"},
        {"RetrieveUserQuotaSummary", R"({"groups": [{"displayName": "Gemini", "buckets": [{"bucketId": "5h", "remainingFraction": 0.9}]}]})"},
        {"GetUserStatus", R"({"userStatus": {"cascadeModelConfigData": {"clientModelConfigs": [{"label": "Pro", "quotaInfo": {"remainingFraction": 0.25}}]}}})"},
    };
    std::int64_t now = kNow;

    agentspeed::quota::http::Response operator()(const agentspeed::quota::http::Request& request) const {
        for (const auto& [fragment, status] : statuses) {
            if (request.url.find(fragment) != std::string::npos) return answer(status, "{}", now, now - 1000);
        }
        for (const auto& [fragment, body] : bodies) {
            if (request.url.find(fragment) != std::string::npos) return answer(200, body, now, now - 1000);
        }
        return answer(404, "");
    }
};

struct ServiceFixture {
    TempHome home;
    FakeTransport transport;
    FakeSecrets secrets;
    std::shared_ptr<CountingFiles> files = std::make_shared<CountingFiles>();
    std::shared_ptr<FakeSessions> sessions = std::make_shared<FakeSessions>();
    std::shared_ptr<FakeLocator> locator = std::make_shared<FakeLocator>();
    std::map<std::string, std::string> variables;
    Vendors vendors;
    int wakes = 0;
    std::vector<std::pair<std::string, int>> logged;
    std::unique_ptr<VendorService> service;

    explicit ServiceFixture(std::set<VendorRoute> available = {agentspeed::quota::kVendorRoutes.begin(),
                                                               agentspeed::quota::kVendorRoutes.end()}) {
        home.write("grok/auth.json", R"({"https://api.x.ai/": {"key": "grok-key", "access_token": "grok-access", "expires_at": "2026-12-01T00:00:00Z"}, "b": {}})");
        home.write(".kimi-code/credentials/kimi-code.json", R"({"access_token": "kimi-token", "refresh_token": "never-read", "expires_at": 1796000000})");
        home.write(".gemini/oauth_creds.json", R"({"access_token": "gem-token", "refresh_token": "never-read", "expiry_date": 1796000000000})");
        home.write(".factory/.env", "FACTORY_API_KEY=fk-1\n");
        home.write(".factory/settings.json", R"({"providerKey": "never-read"})");
        variables["GROK_HOME"] = home.file("grok");
        secrets.items[agentspeed::quota::zcodeKeyAttributes()] = {agentspeed::quota::SecretResult::Found, "zk-1"};
        sessions->sessions["qoder.com"] = {"qoder.com.cn", "sid=q1", kFirefoxUa};
        sessions->sessions["workbuddy.cn"] = {"workbuddy.cn", "wb=w1", kFirefoxUa};
        locator->list = {{4242, {{"http", 5555, "tok"}, {"https", 5555, "tok"}}}};
        transport.handler = [this](const agentspeed::quota::http::Request& r) { return vendors(r); };
        build(std::move(available));
    }

    void build(std::set<VendorRoute> available) {
        auto env = agentspeed::quota::VendorEnvironment::fixed(home.path.string(), variables, files);
        env.timeZone = agentspeed::TimeZone::utc();
        agentspeed::quota::VendorServiceOptions options;
        options.available = std::move(available);
        options.browser = sessions;
        options.antigravity = locator;
        options.wake = [this] { ++wakes; };
        options.log = [this](std::string_view host, int status) { logged.emplace_back(std::string(host), status); };
        service = std::make_unique<VendorService>(std::move(env), transport, secrets, std::move(options));
    }

    // Everything on: every source enabled, running, every switch on, both consents, the network on.
    static VendorInputs allOn(std::int64_t nowMs = kNow) {
        VendorInputs in;
        in.nowMs = nowMs;
        in.networkEnabled = true;
        for (const auto& route : agentspeed::quota::sourceRoutes()) {
            in.enabledSources.insert(route.source);
            in.running.insert(route.source);
            in.switchesOn.insert(std::string(route.switchKey));
        }
        in.switchesOn.insert(std::string(agentspeed::quota::kGrokLiveSwitch));
        in.qoderConsent = in.workBuddyConsent = true;
        return in;
    }

    // Only one route's source and switch on.
    static VendorInputs only(VendorRoute route, std::int64_t nowMs = kNow) {
        auto in = allOn(nowMs);
        const auto source = agentspeed::quota::vendorRouteSource(route);
        in.enabledSources = {source};
        in.running = {source};
        in.switchesOn = {std::string(agentspeed::quota::routeOf(source)->switchKey)};
        if (route == VendorRoute::GrokBuildLive) in.switchesOn.insert(std::string(agentspeed::quota::kGrokLiveSwitch));
        return in;
    }

    std::vector<agentspeed::quota::http::Request> sentTo(std::string_view fragment) const {
        std::vector<agentspeed::quota::http::Request> out;
        for (const auto& r : transport.sent) {
            if (r.url.find(fragment) != std::string::npos) out.push_back(r);
        }
        return out;
    }

    bool touched(std::string_view fragment) const {
        for (const auto& path : files->touched) {
            if (path.find(fragment) != std::string::npos) return true;
        }
        return false;
    }
};

}  // namespace quota2_test
