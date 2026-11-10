#pragma once

#include <QString>
#include <QVariantMap>

// Development aid (macOS CharacterDemoReadings): `AGENT_SPEED_ORB_READOUTS` gives `character` orbs demo network, 性能 /
// 思考 / 首字, cost and running-model readings that follow the orb's speed, so every card shows in shots. Its value
// switches cards: a comma list of readout keys (`intelligence`, `thinking`, `latency`, `network`, `cost`, `agents`,
// `models`, `quota`, `model`), each optionally prefixed with `-` to switch it off; empty keeps the orb's own switches.
namespace character_demo {

bool enabled();
// The readouts the variable switches, as key → on.
QVariantMap switches();
// Adds the demo readings to one orb's tick map; `time` in seconds.
void apply(QVariantMap& orb, double time, const QString& language);

}  // namespace character_demo
