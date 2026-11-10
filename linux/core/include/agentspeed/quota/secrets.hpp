#pragma once

#include <string>
#include <utility>
#include <vector>

// The platform's secret store as the quota routes use it (QUOTAS 9.8 ZCode key, 9.10 Chrome's `v11` password): on
// Linux the freedesktop Secret Service. Secrets are returned for one use and never kept.
namespace agentspeed::quota {

using SecretAttributes = std::vector<std::pair<std::string, std::string>>;

enum class SecretResult : unsigned char {
    Found,
    Missing,      // no such item
    Refused,      // the user dismissed the unlock prompt: not asked again until the app restarts
    Unavailable,  // no Secret Service on the session bus
};

struct SecretLookup {
    SecretResult result = SecretResult::Missing;
    std::string secret;
};

class SecretStore {
public:
    virtual ~SecretStore() = default;
    virtual SecretLookup lookup(const SecretAttributes& attributes) = 0;
    virtual bool store(const SecretAttributes& attributes, const std::string& label, const std::string& secret) = 0;
    virtual bool remove(const SecretAttributes& attributes) = 0;
};

// QUOTAS 9.8: the ZCode key's item.
inline SecretAttributes zcodeKeyAttributes() { return {{"service", "AgentSpeed"}, {"account", "zcode-api-key"}}; }

}  // namespace agentspeed::quota
