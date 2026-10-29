#pragma once

#include <QString>
#include <QVariantMap>

#include "agentspeed/snapshot.hpp"

class Strings;

// DESIGN 12: what an agent row's 性能 cell and score card show, as texts and numbers ready for QML.
namespace perf_card {

// The row fields of the 性能 cell: perf (the shown integer, −1 for –), perfText (compact form), perfForm,
// perfProvisional, perfA11y (the score words).
void cell(const agentspeed::AgentRow& row, const Strings& strings, QVariantMap& out);

// The score card of an agent row (DESIGN 12 Score card): header, score, usual chip, scale, summary, components,
// behaviour, facts and footnote, plus `a11y`, the card's accessibility content.
QVariantMap card(const agentspeed::AgentRow& row, const Strings& strings, const QString& sourceName);

// A component's measured value in the card's formats (DESIGN 12 Components).
QString measureText(const Strings& strings, agentspeed::perf::Id id, double measure);
// The card's tier word of a shown integer (偏慢 · 標準 · 快 · 極快).
QString tierWord(const Strings& strings, std::int64_t shown);
// The components in the card's order (speed: T, L, R; efficiency: N, U, S, H) with their string key suffixes.
struct ComponentKey {
    agentspeed::perf::Id id;
    const char* key;
};
inline constexpr ComponentKey kCardOrder[] = {{agentspeed::perf::Id::T, "throughput"}, {agentspeed::perf::Id::L, "latency"},
                                              {agentspeed::perf::Id::R, "reasoning"},  {agentspeed::perf::Id::N, "pace"},
                                              {agentspeed::perf::Id::U, "toolTime"},   {agentspeed::perf::Id::S, "toolSuccess"},
                                              {agentspeed::perf::Id::H, "cache"}};

// The score words of a value (DESIGN 12 Accessibility): full form, provisional, resting, the highest of N.
QString scoreWords(const Strings& strings, std::int64_t shown, bool provisional, bool resting, std::int64_t top = 0);

}  // namespace perf_card
