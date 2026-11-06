#pragma once

#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/opencode.hpp"
#include "agentspeed/session_claims.hpp"

// METRICS 2.1 Databases: OpenCode's databases, one per root of its location list, read in list order. Each owns its
// responses; a session another database already reads is skipped.
namespace agentspeed::opencode {

void merge(Update& into, Update other);

class SourceSet {
public:
    SourceSet(const std::vector<std::string>& roots, Deduper& deduper);

    const std::vector<std::unique_ptr<Source>>& sources() const { return sources_; }
    bool databaseExists() const;
    // The first root whose database exists, else the first root (the path the source reports).
    std::string displayRoot() const;

    // Startup read of every database, in list order.
    Update seed(std::int64_t todayStartMs);
    std::optional<Update> refreshIfChanged();
    // `seeded`: databases that appeared, vanished or were replaced, read again like at startup; `live`: the others.
    struct Rediscovery {
        Update seeded, live;
    };
    Rediscovery rediscover(std::int64_t nowMs, std::int64_t todayStartMs);

private:
    std::vector<std::string> roots_;
    SessionClaims claims_;
    std::vector<std::unique_ptr<Source>> sources_;
};

}  // namespace agentspeed::opencode
