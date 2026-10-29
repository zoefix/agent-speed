#pragma once

#include <QString>
#include <array>
#include <QVariantList>
#include <QVariantMap>

#include "agentspeed/snapshot.hpp"

class Strings;

// DESIGN 12: what an agent row's 智力 cell and score card show, as texts and numbers ready for QML.
namespace task_card {

// `fmt("task.status.failing", {check, 3, 5})`: the string's %@ and %d filled in order, %% kept as %.
QString fmt(const Strings& strings, const QString& key, const QVariantList& args = {});

// The integer dimensions that add up to the shown score (largest remainders, ties in the order V C R E K).
std::array<int, 5> shownDims(const agentspeed::tasks::Dims& dims, std::int64_t shown);

// Percent as the card shows it: rounded half away from zero, `<1%` and `>99%` at the ends.
QString percent(double p);

// The row fields of the 智力 cell: iq (−1 for –), iqForm, iqProof.
void cell(const agentspeed::AgentRow& row, QVariantMap& out);

QVariantMap card(const agentspeed::AgentRow& row, const Strings& strings, const QString& sourceName);

}  // namespace task_card
