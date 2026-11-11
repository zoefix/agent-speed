#pragma once

#include <QString>
#include <QVariantList>
#include <QVariantMap>
#include <cstdint>

#include "agentspeed/quota/estimate/entry.hpp"
#include "agentspeed/quota/format.hpp"
#include "agentspeed/quota/window.hpp"
#include "quota/quota_estimate_text.hpp"

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
// level, reset (long, "" while unknown or passed), short (the short reset of the row and the orb), age ("" within an
// hour), tooltip (the long reset, `quota.updated` and `quota.pace`, one per line), spoken (`a11y.quota.window` or
// `.windowNoReset`, then the amount and `a11y.quota.estimated`), passed. With the account-window's estimate
// (QUOTAS 10.7, 10.8; `estimates` of the source, `money` the display currency) the percent, bar and reset are the live
// ones, and: estimated (the `≈`), amount and cap (the amount cell: ` / ≈5,742` in tertiary after `US$4,938`),
// details (the tooltip's estimate lines, secondary) and note (its last line, tertiary), spokenCard (the orb card's
// spoken window: no amount, DESIGN 2a.6).
QVariantList lines(const agentspeed::quota::Snapshot& snapshot, std::int64_t nowMs, const Strings& strings,
                   const agentspeed::quota::QuotaFormat& format, const std::vector<agentspeed::quota::estimate::Entry>* estimates = nullptr,
                   const quota_estimate_text::Money& money = {});

// The lines' spoken texts joined by `list.separator`.
QString spoken(const QVariantList& lines, const Strings& strings);

}  // namespace quota_text
