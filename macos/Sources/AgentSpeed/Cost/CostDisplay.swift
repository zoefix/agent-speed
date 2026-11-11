import AgentSpeedCore
import Foundation
import Observation

/// The money unit as one surface shows it (DESIGN 9): the number, its unit, and the states around it.
struct LiveMoney: Equatable {
    /// The digits (`0.0042`), `0` for a subscription-only set, `–` when nothing is priced.
    var number: String
    /// `US$/s`.
    var unit: String
    /// The symbol in front of the number, for places without a unit line (`$0.0042`).
    var inline: String
    /// Part of the live value has no known price: a small `≥` precedes the number.
    var atLeast: Bool
    /// Nothing is priced: the number is `–` in the idle value color.
    var unknown: Bool
    /// micro-USD per second, for the odometer's direction.
    var value: Double
    /// The hover line in money mode (`cost.today.summary`, `cost.partial`, `cost.unknownModels`, `cost.subscriptionOnly`).
    var hoverLine: String
    /// The value as read by accessibility (`0.0042 美元`, wrapped in `a11y.orb.costAtLeast` when partial).
    var spokenAmount: String

    static let placeholder = LiveMoney(number: "0", unit: "$/s", inline: "$0", atLeast: false, unknown: false, value: 0,
                                       hoverLine: "", spokenAmount: "0")
}

/// The character orb's 費用 card 今日 row (DESIGN 2a.6): today's cost of the billable sources as an amount with its
/// symbol (`$12.34`), `–` when nothing today is priced, and the row as accessibility reads it.
struct CostToday: Equatable {
    /// `$12.34`, or `–`.
    var amount: String
    /// Part of today is unpriced: a small `≥` precedes the amount.
    var atLeast: Bool
    var unknown: Bool
    /// `a11y.orb.costToday` with the amount.
    var spoken: String
}

/// Formats costs in the display currency for the UI language with the rates in use (METRICS 9.8, DESIGN 9) and knows
/// which sources count (METRICS 9.4). Observable: views re-render when the currency, language, rates or marks change.
@MainActor
@Observable
final class CostDisplay {
    @ObservationIgnored let settings: AppSettings
    @ObservationIgnored let l10n: Localizer
    @ObservationIgnored let rates: ExchangeRateController
    @ObservationIgnored let prices: PriceSyncController
    @ObservationIgnored let currencies: CurrencyTable

    init(settings: AppSettings, l10n: Localizer, rates: ExchangeRateController, prices: PriceSyncController,
         currencies: CurrencyTable = .bundled) {
        self.settings = settings
        self.l10n = l10n
        self.rates = rates
        self.prices = prices
        self.currencies = currencies
    }

    // MARK: - Currency

    var currency: Currency { currencies.currencyOrUSD(settings.currency) }
    var isUSD: Bool { currency.code == Currency.usdCode }
    var language: String { l10n.language.rawValue }
    var symbol: String { currency.symbol(language: language) }
    /// Units of the display currency per US dollar.
    var rate: Decimal { rates.rate(for: currency.code) }
    /// `US$/s`.
    var unitPerSecond: String { l10n("cost.unitPerSecond", symbol) }
    var currencyName: String { name(of: currency) }
    var billable: SourceSet { settings.billableSources }

    func name(of currency: Currency) -> String { l10n("currency.\(currency.code)") }

    /// The English name, for the picker's search (DESIGN 9).
    func englishName(of currency: Currency) -> String {
        Localizer.string("currency.\(currency.code)", language: .en)
    }

    // MARK: - Values

    func rate(microUsdPerSecond: Double) -> FormattedMoney {
        MoneyFormat.format(MoneyFormat.rate(microUsdPerSecond: microUsdPerSecond, rate: rate), currency: currency,
                           language: language, form: .rate)
    }

    func amount(microUsd: Int64) -> FormattedMoney {
        MoneyFormat.format(MoneyFormat.amount(microUsd: microUsd, rate: rate), currency: currency, language: language,
                           form: .amount)
    }

    /// Board costs per second in this currency (DESIGN 2c Live metrics).
    var boardMoney: BoardMoneyFormat { BoardMoneyFormat(currency: currency, rate: rate, l10n: l10n) }

    /// `$12.34`, `NT$1,235`.
    func amountText(microUsd: Int64) -> String { amount(microUsd: microUsd).text }

    /// The amount in US dollars (tooltips show it next to another currency).
    func usdText(microUsd: Int64) -> String {
        MoneyFormat.format(MoneyFormat.amount(microUsd: microUsd, rate: 1), currency: currencies.currencyOrUSD(Currency.usdCode),
                           language: language, form: .amount).text
    }

    /// Tooltips: `NT$1,235（US$37.94）` for a non-USD currency, else the amount.
    func amountWithUSD(microUsd: Int64) -> String {
        let text = amountText(microUsd: microUsd)
        guard !isUSD else { return text }
        let usd = usdText(microUsd: microUsd)
        return l10n.language == .en ? "\(text) (\(usd))" : "\(text)（\(usd)）"
    }

    /// A cost with `≥` when some responses are unpriced, `–` when nothing is (or no cost data exists).
    func tallyText(_ tally: CostTally?, requests: Int) -> String {
        guard let tally, !tally.isUnknown(requests: requests) else { return "–" }
        let text = amountText(microUsd: tally.costMicroUsd)
        return tally.unpricedRequests > 0 ? "≥" + text : text
    }

    /// `12.34 美元`: how accessibility reads an amount (DESIGN 9).
    func spoken(_ number: String) -> String {
        number == "–" ? l10n("cost.unknown") : "\(number) \(currencyName)"
    }

    /// A cost tally as accessibility reads it: the amount with the currency's name, `a11y.orb.costAtLeast` when part
    /// is unpriced, `cost.unknown` when nothing is (or no cost data exists).
    func spokenTally(_ tally: CostTally?, requests: Int) -> String {
        guard let tally, !tally.isUnknown(requests: requests) else { return l10n("cost.unknown") }
        let amount = spoken(self.amount(microUsd: tally.costMicroUsd).number)
        return tally.unpricedRequests > 0 ? l10n("a11y.orb.costAtLeast", amount) : amount
    }

    // MARK: - Live value

    /// The money unit of a cost reading (METRICS 9.5, DESIGN 9 Orb).
    func live(_ reading: CostReading, agentCount: Int) -> LiveMoney {
        let formatted = rate(microUsdPerSecond: reading.microUsdPerSecond)
        let unknown = reading.state == .nothingPriced
        let number: String = switch reading.state {
        case .subscriptionOnly: "0"
        case .nothingPriced: "–"
        case .priced, .partial: formatted.number
        }
        let atLeast = reading.state == .partial && number != "0"
        let hover: String = switch reading.state {
        case .subscriptionOnly: l10n("cost.subscriptionOnly")
        case .nothingPriced: l10n("cost.unknownModels", modelList(reading.unpricedModels))
        case .partial: l10n("cost.partial")
        case .priced: l10n("cost.today.summary", amountText(microUsd: reading.today.costMicroUsd), agentCount)
        }
        let spokenNumber = spoken(number)
        return LiveMoney(
            number: number,
            unit: unitPerSecond,
            inline: unknown ? "–" : MoneyFormat.withSymbol(symbol, number),
            atLeast: atLeast,
            unknown: unknown,
            value: reading.state == .subscriptionOnly ? 0 : reading.microUsdPerSecond,
            hoverLine: hover,
            spokenAmount: atLeast ? l10n("a11y.orb.costAtLeast", spokenNumber) : spokenNumber
        )
    }

    /// The 今日 row of a cost reading; nil while the billable sources have no request today.
    func today(_ reading: CostReading) -> CostToday? {
        guard reading.todayRequests > 0 else { return nil }
        let unknown = reading.today.isUnknown(requests: reading.todayRequests)
        let atLeast = !unknown && reading.today.unpricedRequests > 0
        return CostToday(amount: unknown ? "–" : amountText(microUsd: reading.today.costMicroUsd), atLeast: atLeast,
                         unknown: unknown,
                         spoken: l10n("a11y.orb.costToday", spokenTally(reading.today, requests: reading.todayRequests)))
    }

    /// Up to two model names, then `+n`.
    func modelList(_ models: [String]) -> String {
        guard !models.isEmpty else { return l10n("cost.unknown") }
        let shown = models.prefix(2).joined(separator: l10n("list.separator"))
        return models.count > 2 ? "\(shown) +\(models.count - 2)" : shown
    }

    // MARK: - Rates

    /// `US$1 = NT$32.55 · 2026/10/22 · open.er-api.com`, or `rates.fallback` before the first fetch.
    var rateStatus: String {
        let date = rateDateText
        guard let provider = rates.rates.provider else { return l10n("rates.fallback", date) }
        let usd = currencies.currencyOrUSD(Currency.usdCode).symbol(language: language)
        let value = MoneyFormat.withSymbol(symbol, MoneyFormat.significant(rate))
        return l10n("rates.status", MoneyFormat.withSymbol(usd, "1"), value, date, provider)
    }

    /// The rate date in the language's numeric date format (the rates are dated by UTC day).
    var rateDateText: String {
        var style = Date.FormatStyle(date: .numeric, time: .omitted, locale: l10n.locale, calendar: Calendar(identifier: .gregorian),
                                     timeZone: TimeZone(identifier: "UTC")!)
        style = style.year(.defaultDigits).month(.twoDigits).day(.twoDigits)
        return rates.ratesDate.formatted(style)
    }

    /// `以 2026/10/22 的匯率換算` for a non-USD currency.
    var historyNote: String? { isUSD ? nil : l10n("rates.historyNote", rateDateText) }
}
