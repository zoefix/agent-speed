#pragma once

#include <cstdint>
#include <functional>
#include <map>
#include <string>
#include <vector>

#include "agentspeed/devin.hpp"

// METRICS 4.17 History, 8: Devin's part of the daily history, one entry per CLI session.
namespace agentspeed::history {

struct DevinSession {
    // The entry's store key: `<database path>#<session id>`.
    std::string key, database;
    // The last version of each call, in first-seen order (a call's stored copies update it, METRICS 4.4).
    std::vector<ParsedResponse> responses;
    std::int64_t size = 0, modified = 0;
};

// Reads the sessions whose identity changed since the store's, and keeps each database's row statistics in memory so
// the next analysis of this run reads less. Not thread-safe: owned by the analyzer.
class DevinPlanner {
public:
    // `unchanged(key, size, modified)` says whether the stored entry still holds; the databases in list order, a session
    // an earlier database has left out.
    std::vector<DevinSession> plan(const std::vector<devin::Root>& roots,
                                   const std::function<bool(const std::string&, std::int64_t, std::int64_t)>& unchanged);
    int discovered() const { return discovered_; }
    int unreadable() const { return unreadable_; }

    // The highest row id and the session model in one number: a call without a model of its own takes the latter.
    static std::int64_t modified(std::int64_t maxRowId, const std::optional<std::string>& model);

private:
    struct Cache {
        std::string identity;
        std::map<std::string, devin::RowStats> stats;
    };
    const std::map<std::string, devin::RowStats>& statsOf(const devin::Database& database);

    std::map<std::string, Cache> cache_;
    int discovered_ = 0, unreadable_ = 0;
};

}  // namespace agentspeed::history
