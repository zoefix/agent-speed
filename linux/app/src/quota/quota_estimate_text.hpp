#pragma once

#include <QString>
#include <QStringList>
#include <QVariantMap>
#include <optional>
#include <vector>

#include "agentspeed/quota/estimate/entry.hpp"
#include "agentspeed/quota/window.hpp"

class Strings;

// QUOTAS 10.7 and 10.8 in words (DESIGN 4 Quota lines, 2a.6 額度): a window's live percent, its used amount and cap,
// the estimate's tooltip lines and its spoken parts, in the display currency.
namespace quota_estimate_text {

// The display currency (DESIGN 9): its code and the units per US dollar as decimal text.
struct Money {
    QString currency = QStringLiteral("USD");
    QString rate = QStringLiteral("1");
    bool operator==(const Money&) const = default;
};

// The account-window entry of a shown window: same kind, name and length.
const agentspeed::quota::estimate::Entry* find(const std::vector<agentspeed::quota::estimate::Entry>& entries,
                                                const agentspeed::quota::Window& window);

// DESIGN 9 Amount with its symbol (`US$161`); `symbol` false: the number alone (`5,742`), the cap after the used amount.
QString amount(std::int64_t microUsd, const Money& money, const QString& language, bool symbol = true);

// The keys a line gains from its estimate, over the vendor's values:
// - percent, percentText, fraction, level: the live percent (QUOTAS 10.7); `estimated` while it is extrapolated;
// - resetsAtMs: the reset of the live instance (a new one after R), nullopt while unknown;
// - amount: the used amount (`US$161`, `≥` while partly unpriced), "" without spend and estimate; cap: the rest of
//   `quota.amount.cap` / `quota.amount.atLeast` after the amount (` / ≈5,742`, the cap without its symbol), "" without;
// - details: the tooltip's estimate lines in secondary text, note: its last line in tertiary (empty without an estimate);
// - amountSpoken: `a11y.quota.amount`, or `.amountNoCap` without a cap ("" without amount).
struct Decoration {
    double percent = 0;
    bool estimated = false;
    std::optional<std::int64_t> resetsAtMs;
    bool passed = false;
    QString amount, cap, amountSpoken;
    QStringList details;
    QString note;
};
Decoration decorate(const agentspeed::quota::estimate::Entry& entry, std::int64_t nowMs, const Strings& strings, const Money& money);

}  // namespace quota_estimate_text
