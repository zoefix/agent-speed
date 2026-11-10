#pragma once

#include <cstdint>
#include <functional>
#include <map>
#include <string>
#include <vector>

#include "agentspeed/devin_history.hpp"
#include "agentspeed/qoder.hpp"

// METRICS 4.18 History, 8: Qoder IDE's part of the daily history, one entry per chat session.
namespace agentspeed::history {

// The same entry as a Devin session: its key `<database path>#<session id>`, its database and its calls.
using QoderSession = DevinSession;

// Reads the sessions whose identity (row count, highest `rowid`) changed since the store's, and keeps each database's
// row statistics in memory so the next analysis of this run reads less. Not thread-safe: owned by the analyzer.
class QoderPlanner {
public:
    // `unchanged(key, size, modified)` says whether the stored entry still holds; the databases in list order, a session
    // an earlier database has left out.
    std::vector<QoderSession> plan(const std::vector<qoder::Root>& roots,
                                   const std::function<bool(const std::string&, std::int64_t, std::int64_t)>& unchanged);
    int discovered() const { return discovered_; }
    int unreadable() const { return unreadable_; }

private:
    struct Cache {
        std::string identity;
        std::map<std::string, qoder::RowStats> stats;
    };
    const std::map<std::string, qoder::RowStats>& statsOf(const qoder::Database& database);

    std::map<std::string, Cache> cache_;
    int discovered_ = 0, unreadable_ = 0;
};

}  // namespace agentspeed::history
