#include "agentspeed/dedupe.hpp"

namespace agentspeed {

bool Deduper::tryClaim(const std::string& key, const std::string& agentId, std::string* owner) {
    if (auto it = owners_.find(key); it != owners_.end()) {
        if (owner) *owner = it->second;
        return false;
    }
    if (order_.size() >= kCapacity) {
        owners_.erase(order_.front());
        order_.pop_front();
    }
    owners_.emplace(key, agentId);
    order_.push_back(key);
    if (owner) *owner = agentId;
    return true;
}

std::optional<std::string> Deduper::owner(const std::string& key) const {
    if (auto it = owners_.find(key); it != owners_.end()) return it->second;
    return std::nullopt;
}

bool Deduper::ownedByOther(const std::string& key, const std::string& agentId) const {
    auto it = owners_.find(key);
    return it != owners_.end() && it->second != agentId;
}

}  // namespace agentspeed
