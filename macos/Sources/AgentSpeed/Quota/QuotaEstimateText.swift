import AgentSpeedCore
import Foundation

extension QuotaShownWindow {
    /// The window as the estimate sees it now (QUOTAS 10.7): the live percent and bar, and after a reset the new
    /// instance's reset; a window that started over without a new instance stays passed.
    func live(_ estimate: QuotaEstimateDisplay) -> QuotaShownWindow {
        var shown = self
        shown.percent = QuotaPercent.rounded(estimate.percent)
        shown.bar = QuotaPercent.clamped(estimate.percent)
        shown.resetsAtMs = estimate.resetsMs
        shown.passed = passed && estimate.resetsMs == nil
        shown.window.usedPercent = estimate.percent
        shown.window.resetsAtMs = estimate.resetsMs
        return shown
    }
}

/// The estimate's texts (DESIGN 4 *Amount*, *Tooltip*, *Accessibility*; 2a.6): amounts in the display currency
/// (DESIGN 9 *Amount*), the USD amount beside another currency in tooltips.
@MainActor
struct QuotaEstimateText {
    let l10n: Localizer
    let cost: CostDisplay

    /// `$4,938` and ` / ≈5,742` (or ` / ≥598`) in the compact digits, the cap without the symbol the used amount
    /// already shows (user's choice 2026-11-11), split so the cap can take the tertiary token.
    func amountCell(_ e: QuotaEstimateDisplay) -> (used: String, cap: String?) {
        let used = cost.compactAmountText(microUsd: e.usedMicroUsd)
        let full: String
        if let cap = e.capMicroUsd {
            full = l10n("quota.amount.cap", used, cost.compactAmountNumber(microUsd: cap))
        } else if e.hasEstimate, let low = e.lowMicroUsd {
            full = l10n("quota.amount.atLeast", used, cost.compactAmountNumber(microUsd: low))
        } else {
            return (used, nil)
        }
        guard full.hasPrefix(used) else { return (full, nil) }
        return (used, String(full.dropFirst(used.count)))
    }

    /// The used amount alone, for the orb card (which shows no cap).
    func usedAmount(_ e: QuotaEstimateDisplay) -> String? {
        e.showsAmount ? cost.compactAmountText(microUsd: e.usedMicroUsd) : nil
    }

    /// `a11y.quota.amount` with the cap, else `a11y.quota.amountNoCap`.
    func spokenAmount(_ e: QuotaEstimateDisplay, withCap: Bool) -> String {
        let used = cost.spoken(cost.amount(microUsd: e.usedMicroUsd).number)
        if withCap, let cap = e.capMicroUsd {
            return l10n("a11y.quota.amount", used, cost.spoken(cost.amount(microUsd: cap).number))
        }
        return l10n("a11y.quota.amountNoCap", used)
    }

    /// The tooltip's lines under the hairline, in order; empty without an estimate.
    func details(_ e: QuotaEstimateDisplay) -> [String] {
        guard e.hasEstimate else { return [] }
        var lines = [l10n("quota.estimate.used", cost.amountWithUSD(microUsd: e.usedMicroUsd))]
        if let cap = e.capMicroUsd, let low = e.lowMicroUsd, let high = e.highMicroUsd {
            lines.append(l10n("quota.estimate.cap", cost.amountWithUSD(microUsd: cap), cost.amountText(microUsd: low),
                              cost.amountText(microUsd: high)))
        } else if let low = e.lowMicroUsd {
            lines.append(l10n("quota.estimate.atLeast", cost.amountWithUSD(microUsd: low)))
        }
        if e.capMicroUsd != nil, let account = e.accountUsedMicroUsd {
            lines.append(l10n("quota.estimate.account", cost.amountWithUSD(microUsd: account)))
        }
        if let coverage = e.coverage {
            lines.append(l10n("quota.estimate.coverage", Int((coverage * 100).rounded())))
        }
        let confidence = l10n("quota.estimate.confidence.\(e.confidence.rawValue)")
        lines.append(confidence + " · " + l10n("quota.estimate.windows", e.windows))
        if let plan = e.plan {
            lines.append(l10n("quota.estimate.plan", [plan, e.tier].compactMap { $0 }.joined(separator: " · ")))
        }
        if e.estimated { lines.append(l10n("quota.estimate.live")) }
        return lines
    }
}
