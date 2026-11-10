import Foundation
import os

/// The templates of QUOTAS 7 in one language, from `spec/strings.json` (`%@` and `%d` placeholders, `%%`).
public struct QuotaStrings: Sendable {
    public var resetIn: String
    public var resetAt: String
    public var minutes: String
    public var hours: String
    public var hoursMinutes: String
    public var days: String
    public var age: String
    public var pace: String
    public var words: QuotaWindow.Words

    public init(resetIn: String, resetAt: String, minutes: String, hours: String, hoursMinutes: String, days: String,
                age: String, pace: String, words: QuotaWindow.Words = .english) {
        self.resetIn = resetIn
        self.resetAt = resetAt
        self.minutes = minutes
        self.hours = hours
        self.hoursMinutes = hoursMinutes
        self.days = days
        self.age = age
        self.pace = pace
        self.words = words
    }

    /// The keys each template comes from.
    public static let keys = (resetIn: "quota.reset.in", resetAt: "quota.reset.at", minutes: "quota.duration.m",
                              hours: "quota.duration.h", hoursMinutes: "quota.duration.hm", days: "quota.duration.d",
                              age: "quota.age", pace: "quota.pace", month: "quota.window.month",
                              credits: "quota.window.credits")

    /// Looks every template up by its key.
    public init(_ lookup: (String) -> String) {
        let k = Self.keys
        self.init(resetIn: lookup(k.resetIn), resetAt: lookup(k.resetAt), minutes: lookup(k.minutes), hours: lookup(k.hours),
                  hoursMinutes: lookup(k.hoursMinutes), days: lookup(k.days), age: lookup(k.age), pace: lookup(k.pace),
                  words: QuotaWindow.Words(month: lookup(k.month), credits: lookup(k.credits)))
    }
}

/// The texts of QUOTAS 7: reset times (long for the dashboard, short for the orb card), the age of a snapshot and the
/// pace of a window. Weekdays and times come from the platform's formatter in the app's locale; `timeZone` is local.
public struct QuotaFormat: Sendable {
    public static let hourMs: Int64 = 3_600_000
    public static let dayMs: Int64 = 24 * hourMs

    public let strings: QuotaStrings
    public let locale: Locale
    public let timeZone: TimeZone

    public init(strings: QuotaStrings, locale: Locale, timeZone: TimeZone = .current) {
        self.strings = strings
        self.locale = locale
        self.timeZone = timeZone
    }

    // MARK: Reset times

    /// `42 分後重置`, `3 小時 12 分後重置`, `週五 09:00 重置`, or `11/17 09:00 重置` when the weekday is today's.
    public func resetLong(_ resetsAtMs: Int64, nowMs: Int64) -> String {
        let d = resetsAtMs - nowMs
        if d < Self.dayMs {
            return Self.fill(strings.resetIn, duration(max(0, d)))
        }
        return Self.fill(strings.resetAt, "\(weekdayOrDate(resetsAtMs, nowMs: nowMs)) \(time(resetsAtMs))")
    }

    /// `42m`, `3h`, the weekday's short name, or `M/d` when the weekday is today's.
    public func resetShort(_ resetsAtMs: Int64, nowMs: Int64) -> String {
        let d = resetsAtMs - nowMs
        if d < Self.hourMs { return "\(max(0, d) / 60_000)m" }
        if d < Self.dayMs { return "\(d / Self.hourMs)h" }
        return weekdayOrDate(resetsAtMs, nowMs: nowMs)
    }

    /// Minutes below an hour, else hours with their minutes rounded down (an exact hour drops them).
    func duration(_ ms: Int64) -> String {
        let minutes = Int(ms / 60_000)
        if ms < Self.hourMs { return Self.fill(strings.minutes, minutes) }
        let (hours, rest) = minutes.quotientAndRemainder(dividingBy: 60)
        if rest == 0 { return Self.fill(strings.hours, hours) }
        return Self.fill(strings.hoursMinutes, hours, rest)
    }

    func weekdayOrDate(_ ms: Int64, nowMs: Int64) -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let at = Self.date(ms), now = Self.date(nowMs)
        if calendar.component(.weekday, from: at) == calendar.component(.weekday, from: now) {
            let parts = calendar.dateComponents([.month, .day], from: at)
            return "\(parts.month ?? 0)/\(parts.day ?? 0)"
        }
        return formatter("EEE").string(from: at)
    }

    func time(_ ms: Int64) -> String { formatter("HH:mm").string(from: Self.date(ms)) }

    /// Kept per locale, zone and format: the orb card asks every frame.
    private static let formatters = OSAllocatedUnfairLock<[String: DateFormatter]>(uncheckedState: [:])

    private func formatter(_ format: String) -> DateFormatter {
        let key = "\(locale.identifier)|\(timeZone.identifier)|\(format)"
        return Self.formatters.withLockUnchecked { cache in
            if let known = cache[key] { return known }
            let formatter = DateFormatter()
            formatter.locale = locale
            formatter.timeZone = timeZone
            formatter.calendar = Calendar(identifier: .gregorian)
            formatter.dateFormat = format
            if cache.count >= 32 { cache.removeAll() }
            cache[key] = formatter
            return formatter
        }
    }

    // MARK: Age and pace

    /// `quota.age` once the snapshot is older than an hour: whole hours, from 24 hours whole days; nil before.
    public func age(observedMs: Int64, nowMs: Int64) -> String? {
        let age = nowMs - observedMs
        guard age >= Self.hourMs else { return nil }
        if age < Self.dayMs { return Self.fill(strings.age, Self.fill(strings.hours, Int(age / Self.hourMs))) }
        return Self.fill(strings.age, Self.fill(strings.days, Int(age / Self.dayMs)))
    }

    /// The share of the window that has passed, 0…1; nil without a reset time or after it.
    public static func pace(_ window: QuotaWindow, nowMs: Int64) -> Double? {
        guard let resets = window.resetsAtMs, nowMs < resets, let minutes = window.minutes, minutes > 0 else { return nil }
        let share = 1 - Double(resets - nowMs) / (Double(minutes) * 60_000)
        return min(1, max(0, share))
    }

    /// `時間進度 60%`.
    public func paceText(_ share: Double) -> String {
        Self.fill(strings.pace, Int((share * 100).rounded()))
    }

    // MARK: Templates

    /// Replaces `%@` and `%d` in order and `%%` with `%`, like the reference's formatter, in every language.
    public static func fill(_ template: String, _ arguments: CustomStringConvertible...) -> String {
        var out = ""
        var index = 0
        var iterator = template.makeIterator()
        while let character = iterator.next() {
            guard character == "%" else {
                out.append(character)
                continue
            }
            switch iterator.next() {
            case "%": out.append("%")
            case "@", "d":
                if index < arguments.count { out += arguments[index].description }
                index += 1
            case let other?: out.append("%"); out.append(other)
            case nil: out.append("%")
            }
        }
        return out
    }

    static func date(_ ms: Int64) -> Date { Date(timeIntervalSince1970: Double(ms) / 1000) }
}
