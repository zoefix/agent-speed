#include "agentspeed/board.hpp"

#include <algorithm>

namespace agentspeed::board {

namespace {

bool leads(const Group& a, const Group& b) {
    if (a.live < Ranking::kIdle && b.live < Ranking::kIdle) return a.lastActiveMs > b.lastActiveMs + 1000;
    return a.live > b.live * 1.1 + 1;
}

bool overwhelms(const Group& a, const Group& b) { return a.live > b.live * 2 + 20; }

}  // namespace

std::pair<std::vector<std::string>, std::vector<RankEvent>> Ranking::tick(const std::vector<Group>& groups) {
    std::map<std::string, const Group*> byId;
    for (const auto& g : groups) byId[g.id] = &g;
    std::map<std::pair<std::string, std::string>, int> pressure;
    for (const auto& a : groups)
        for (const auto& b : groups)
            if (&a != &b && leads(a, b)) {
                const auto key = std::make_pair(a.id, b.id);
                const auto it = pressure_.find(key);
                pressure[key] = (it == pressure_.end() ? 0 : it->second) + 1;
            }
    pressure_ = std::move(pressure);
    std::vector<std::string> order;
    for (const auto& id : order_)
        if (byId.contains(id)) order.push_back(id);
    std::vector<const Group*> fresh;
    for (const auto& g : groups)
        if (std::find(order_.begin(), order_.end(), g.id) == order_.end()) fresh.push_back(&g);
    std::sort(fresh.begin(), fresh.end(), [](const Group* a, const Group* b) {
        if (a->live != b->live) return a->live > b->live;
        if (a->lastActiveMs != b->lastActiveMs) return a->lastActiveMs > b->lastActiveMs;
        return a->id < b->id;
    });
    for (const auto* g : fresh) {
        const auto at = std::find_if(order.begin(), order.end(), [&](const std::string& id) { return byId[id]->live < g->live; });
        order.insert(at, g->id);
    }
    const auto baseline = order;
    for (std::size_t i = 1; i < order.size(); ++i) {
        for (std::size_t j = i; j > 0; --j) {
            const auto& a = *byId[order[j]];
            const auto& b = *byId[order[j - 1]];
            const auto it = pressure_.find({a.id, b.id});
            if ((it != pressure_.end() && it->second >= 2) || overwhelms(a, b)) std::swap(order[j - 1], order[j]);
            else break;
        }
    }
    std::vector<RankEvent> events;
    for (std::size_t rank = 1; rank <= order.size(); ++rank) {
        const auto before = static_cast<int>(std::find(baseline.begin(), baseline.end(), order[rank - 1]) - baseline.begin()) + 1;
        if (static_cast<int>(rank) < before)
            events.push_back({rank == 1 ? RankEvent::Type::NewLeader : RankEvent::Type::Overtake, order[rank - 1], before, static_cast<int>(rank)});
    }
    order_ = order;
    return {order, events};
}

}  // namespace agentspeed::board
