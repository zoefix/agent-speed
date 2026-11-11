#pragma once

#include <array>

namespace agentspeed::character {

// The cards of the character's floating readout (DESIGN 2a.6, *Readouts*): the speed on her right with the running
// models under it, the model above her head, and the column of facts on her left, top to bottom, 額度 last. New cards go
// last, so the others keep their float phases.
enum class HudCard { Speed = 0, Model = 1, Performance = 2, Network = 3, Cost = 4, Agents = 5, Models = 6, Quota = 7 };

constexpr int kHudCardCount = 8;
constexpr std::array<HudCard, kHudCardCount> kHudCards = {HudCard::Speed, HudCard::Model,  HudCard::Performance, HudCard::Network,
                                                          HudCard::Cost,  HudCard::Agents, HudCard::Models,      HudCard::Quota};
// The left column, top to bottom.
constexpr std::array<HudCard, 5> kHudColumn = {HudCard::Performance, HudCard::Network, HudCard::Cost, HudCard::Agents, HudCard::Quota};

inline int index(HudCard c) { return static_cast<int>(c); }

struct CardSize {
    double w = 0, h = 0;
};

}  // namespace agentspeed::character
