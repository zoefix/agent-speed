#pragma once

#include <memory>
#include <string>
#include <vector>

#include "agentspeed/session_claims.hpp"
#include "agentspeed/zcode.hpp"

// METRICS 2.1 Databases: ZCode's databases, one per root of its location list, read in list order. Each owns its
// responses; a session another database already reads is skipped.
namespace agentspeed::zcode {

void merge(Update& into, Update other);

class SourceSet {
public:
    SourceSet(const std::vector<std::string>& roots, Deduper& deduper);

    const std::vector<std::unique_ptr<Source>>& sources() const { return sources_; }
    bool available() const;
    bool unsupported() const;
    // The first root whose database exists, else the first root (the path the source reports).
    std::string displayRoot() const;

    Update seed(std::int64_t todayStartMs);
    Update refresh();

private:
    std::vector<std::string> roots_;
    SessionClaims claims_;
    std::vector<std::unique_ptr<Source>> sources_;
};

}  // namespace agentspeed::zcode
