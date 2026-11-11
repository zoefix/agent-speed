import Foundation

/// A cap class (QUOTAS 10.2): every account of one `(vendor, plan, tier, window)`, else `(account, window)`.
public enum QuotaCapClass: Hashable, Sendable {
    /// `vendorPlan` is `<vendor>/<plan>`.
    case plan(vendorPlan: String, tier: String?, window: String)
    case account(String, window: String)

    static func of(account: String, vendorPlan: String?, tier: String?, window: String) -> QuotaCapClass {
        if let vendorPlan { return .plan(vendorPlan: vendorPlan, tier: tier, window: window) }
        return .account(account, window: window)
    }

    public var window: String {
        switch self {
        case .plan(_, _, let window), .account(_, let window): window
        }
    }

    /// The window's length, for the half-life.
    var minutes: Int? {
        window.split(separator: ":", omittingEmptySubsequences: false).last.flatMap { Int($0) }
    }

    /// The stored form: `["plan", vendorPlan, tier, window]` or `["account", account, window]`.
    var list: [String?] {
        switch self {
        case .plan(let plan, let tier, let window): ["plan", plan, tier, window]
        case .account(let account, let window): ["account", account, window]
        }
    }

    init?(list: [String?]) {
        if list.count == 4, list[0] == "plan", let plan = list[1], let window = list[3] {
            self = .plan(vendorPlan: plan, tier: list[2], window: window)
        } else if list.count == 3, list[0] == "account", let account = list[1], let window = list[2] {
            self = .account(account, window: window)
        } else {
            return nil
        }
    }

    /// The order of the reference, which sorts classes by their Python text.
    var sortKey: String { QuotaPythonText.tuple(list.map { .text($0) }) }

    /// `plan|openai/pro|None|fiveHour::300`, as the fixtures name a class.
    public var name: String { list.map { $0 ?? "None" }.joined(separator: "|") }
}

/// Python's `str()` of tuples of strings, integers and None: the reference sorts by it.
enum QuotaPythonText {
    enum Item {
        case text(String?)
        case int(Int64)
    }

    static func tuple(_ items: [Item]) -> String {
        "(" + items.map(item).joined(separator: ", ") + (items.count == 1 ? ",)" : ")")
    }

    private static func item(_ item: Item) -> String {
        switch item {
        case .text(nil): "None"
        case .text(let text?): quoted(text)
        case .int(let value): String(value)
        }
    }

    private static func quoted(_ text: String) -> String {
        let quote = text.contains("'") && !text.contains("\"") ? "\"" : "'"
        var out = quote
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            default:
                if String(scalar) == quote { out += "\\" + quote } else { out.unicodeScalars.append(scalar) }
            }
        }
        return out + quote
    }
}
