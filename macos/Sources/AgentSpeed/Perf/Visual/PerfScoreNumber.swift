import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation
import SwiftUI

extension EnvironmentValues {
    /// Reduce Motion as the app sees it (`AccessibilityState`, which a harness may force), set by each surface's host.
    @Entry var perfReduceMotion = false
}

/// The two number forms of a surface (DESIGN 12, *Number formats*).
enum PerfNumberForm: Sendable {
    case full
    case compact

    func text(_ value: Int) -> String {
        switch self {
        case .full: PerfFormat.full(value)
        case .compact: PerfFormat.compact(value)
        }
    }
}

/// SwiftUI timings of `PerfMotion` (DESIGN 12, *Motion*).
enum PerfScoreMotion {
    /// easeOutCubic as a curve SwiftUI interpolates with.
    static func countAnimation(seconds: Double) -> Animation {
        .timingCurve(0.33, 1, 0.68, 1, duration: seconds)
    }

    static let formAnimation = Animation.easeInOut(duration: PerfMotion.form)
}

/// A score number that follows DESIGN 12's motion: a change of at least 1 % and 3 counts through the intermediate
/// integers (each in the surface's form, so a count from 986 to 1,240 gains its comma as it passes 1,000), smaller
/// changes replace the digits, `–` ↔ a number crossfades, and Reduce Motion crossfades every change in 0.15 s. A new
/// value during a count continues from the value on screen.
struct PerfScoreNumber: View {
    var value: Int?
    var form: PerfNumberForm = .full
    var counts = true
    var font: Font
    var absent = "\u{2013}"

    @Environment(\.perfReduceMotion) private var reduceMotion
    @State private var shown: Double?
    @State private var identity = 0

    var body: some View {
        CountingText(value: shown ?? value.map(Double.init), form: form, absent: absent)
            .font(font)
            .monospacedDigit()
            .id(identity)
            .transition(.opacity)
            .onChange(of: value) { old, new in
                let from = shown.map { Int($0.rounded()) } ?? old
                switch PerfMotion.change(from: from, to: new, reduceMotion: reduceMotion, counts: counts) {
                case .none:
                    break
                case .count(let seconds):
                    if shown == nil { shown = old.map(Double.init) }
                    withAnimation(PerfScoreMotion.countAnimation(seconds: seconds)) { shown = new.map(Double.init) }
                case .replace:
                    var transaction = Transaction()
                    transaction.disablesAnimations = true
                    withTransaction(transaction) { shown = new.map(Double.init) }
                case .crossfade(let seconds):
                    withAnimation(.easeOut(duration: seconds)) {
                        identity += 1
                        shown = new.map(Double.init)
                    }
                }
            }
    }
}

/// The interpolated value as the nearest integer in the surface's form, so a count passes through every intermediate
/// number.
private struct CountingText: View, Animatable {
    var value: Double?
    var form: PerfNumberForm
    var absent: String

    nonisolated var animatableData: Double {
        get { value ?? 0 }
        set { if value != nil { value = newValue } }
    }

    var body: some View {
        Text(value.map { form.text(Int($0.rounded())) } ?? absent)
    }
}
