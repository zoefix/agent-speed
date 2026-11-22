#pragma once

#include <map>
#include <memory>
#include <mutex>
#include <vector>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/model.hpp"

namespace agentspeed::openbitfun {

using RoundId = std::pair<std::string, std::string>;  // turn id, round id

struct Attempt {
    std::optional<std::string> model, provider;
    std::optional<TokenCounts> tokens;
    bool operator==(const Attempt&) const = default;
};

struct UsageSnapshot {
    std::map<RoundId, std::vector<Attempt>> rounds;
    std::int64_t signature = 0, writeNs = 0;
};

// Daily files serve many sessions. Retain only the identities, counters and attribution for each attempt.
class UsageLedger {
public:
    explicit UsageLedger(std::string root) : root_(std::move(root)) {}
    static UsageLedger& shared();
    std::shared_ptr<const UsageSnapshot> forSession(const std::string& session);

private:
    struct Row {
        RoundId round;
        std::string id;
        Attempt attempt;
        std::int64_t recordedMs;
        bool operator==(const Row&) const = default;
    };
    struct Day {
        static std::optional<Day> read(const fsx::Entry& file);
        fsx::Stat stat;
        std::map<std::string, std::vector<Row>> sessions;
    };
    std::string root_;
    std::mutex mutex_;
    std::int64_t nextScan_ = 0;
    std::map<std::string, Day> days_;
    std::map<std::string, std::shared_ptr<UsageSnapshot>> sessions_;
};

}  // namespace agentspeed::openbitfun
