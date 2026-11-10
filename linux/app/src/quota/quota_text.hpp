#pragma once

#include <QString>
#include <QVariantList>
#include <QVariantMap>
#include <cstdint>

#include "agentspeed/quota/format.hpp"
#include "agentspeed/quota/window.hpp"

class Strings;

// QUOTAS 7 in the app's language and the local time zone, and one snapshot's lines as the dashboard and the orb card
// draw them (DESIGN 4 Quota lines, 2a.6 額度).
namespace quota_text {

agentspeed::quota::QuotaFormat format(const Strings& strings);

// "normal", "warning" from 80 %, "critical" from 95 % (the fill and, when critical, the percent's red).
QString level(int percent);

// The snapshot's time for `quota.updated`: the 24-hour time alone today, else `M/d HH:mm`.
QString updatedTime(std::int64_t observedMs, std::int64_t nowMs);

// One map per window in QUOTAS 2 order: label, percent, percentText, fraction (0…1), pace (0…1, −1 while unknown),
// level, reset (long, "" while unknown or passed), short (the orb's short reset), age ("" within an hour), tooltip
// (`quota.updated` and `quota.pace`), spoken (`a11y.quota.window` or `.windowNoReset`), passed.
QVariantList lines(const agentspeed::quota::Snapshot& snapshot, std::int64_t nowMs, const Strings& strings,
                   const agentspeed::quota::QuotaFormat& format);

// The lines' spoken texts joined by `list.separator`.
QString spoken(const QVariantList& lines, const Strings& strings);

}  // namespace quota_text
