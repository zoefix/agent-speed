#pragma once

#include <deque>
#include <optional>
#include <string>
#include <unordered_map>

namespace agentspeed {

// METRICS 3.4: one bounded set of response keys shared by every file of every source, with FIFO eviction.
class Deduper {
public:
    static constexpr std::size_t kCapacity = 65536;

    virtual ~Deduper() = default;
    // Claims the key for the agent. Returns true when it was new; otherwise `owner` names the file that has it.
    virtual bool tryClaim(const std::string& key, const std::string& agentId, std::string* owner = nullptr);
    virtual std::optional<std::string> owner(const std::string& key) const;
    virtual bool ownedByOther(const std::string& key, const std::string& agentId) const;
    std::size_t size() const { return owners_.size(); }

private:
    std::unordered_map<std::string, std::string> owners_;
    std::deque<std::string> order_;
};

}  // namespace agentspeed
