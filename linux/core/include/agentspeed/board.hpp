#pragma once

#include <cstdint>
#include <map>
#include <string>
#include <utility>
#include <vector>

// Leaderboard boards (DESIGN 2c): the stable ranking of agent groups (spec/tools/board_ranking.py).
namespace agentspeed::board {

struct Group {
    std::string id;
    double live = 0;
    std::int64_t lastActiveMs = 0;
};

struct RankEvent {
    enum class Type { Overtake, NewLeader } type = Type::Overtake;
    std::string id;
    int from = 0, to = 0;
};

// A group passes the one above only after leading it by more than 10 % + 1 tok/s on two consecutive ticks, or at once
// by more than 2× + 20; groups all below 0.05 order by latest activity. New groups enter at their value's place.
class Ranking {
public:
    static constexpr double kIdle = 0.05;
    std::pair<std::vector<std::string>, std::vector<RankEvent>> tick(const std::vector<Group>& groups);
    const std::vector<std::string>& order() const { return order_; }

private:
    std::vector<std::string> order_;
    std::map<std::pair<std::string, std::string>, int> pressure_;
};

}  // namespace agentspeed::board
