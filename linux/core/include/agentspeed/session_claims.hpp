#pragma once

#include <map>
#include <string>

namespace agentspeed {

// METRICS 2.1 Databases: which database of a source reads a session — the first that has it. A session id another
// database of the same source already reads is skipped, so a copied database adds no agents, sessions or tokens.
class SessionClaims {
public:
    // True when `owner` may read the session: no other database has it.
    bool claim(const std::string& session, int owner) {
        const auto [it, added] = owners_.emplace(session, owner);
        return added || it->second == owner;
    }
    void release(const std::string& session, int owner) {
        if (const auto it = owners_.find(session); it != owners_.end() && it->second == owner) owners_.erase(it);
    }
    void releaseAll(int owner) { std::erase_if(owners_, [owner](const auto& entry) { return entry.second == owner; }); }

private:
    std::map<std::string, int> owners_;
};

}  // namespace agentspeed
