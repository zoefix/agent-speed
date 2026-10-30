import SwiftUI

/// The effort meter (DESIGN 4, Agents): five rising bars, `level` of them lit from the left. The top rung glows and,
/// when allowed, a light band sweeps across it.
struct EffortMeter: View {
    let level: Int
    let tone: EffortTone
    var reduceMotion = false
    var shimmers = false

    @Environment(\.self) private var environment

    var body: some View {
        let palette = EffortCapsulePalette(environment)
        let glows = tone == .topRung && !palette.increasedContrast

        ZStack(alignment: .bottomLeading) {
            unlitBars(tone == .configured ? palette.configuredUnlit : palette.unlit)
            if glows {
                litBars(palette)
                    .blur(radius: 1.5)
                    .opacity(0.45)
                    .accessibilityHidden(true)
            }
            litBars(palette)
            if glows && shimmers && !reduceMotion {
                ShimmerBand()
                    .mask(alignment: .bottomLeading) { litBars(palette) }
                    .allowsHitTesting(false)
            }
        }
        .frame(width: EffortCapsule.meterWidth, height: EffortCapsule.barHeights.last, alignment: .bottomLeading)
        .accessibilityHidden(true)
    }

    private func unlitBars(_ color: Color) -> some View {
        HStack(alignment: .bottom, spacing: EffortCapsule.barSpacing) {
            ForEach(EffortCapsule.barHeights.indices, id: \.self) { index in
                RoundedRectangle(cornerRadius: 0.75)
                    .fill(color)
                    .frame(width: EffortCapsule.barWidth, height: EffortCapsule.barHeights[index])
            }
        }
    }

    /// Lit bars fill from the bottom one after another (30 ms apart); Reduce Motion crossfades them.
    private func litBars(_ palette: EffortCapsulePalette) -> some View {
        let tallest = EffortCapsule.barHeights.last ?? 1
        return HStack(alignment: .bottom, spacing: EffortCapsule.barSpacing) {
            ForEach(EffortCapsule.barHeights.indices, id: \.self) { index in
                let height = EffortCapsule.barHeights[index]
                let lit = index < level
                RoundedRectangle(cornerRadius: 0.75)
                    .fill(palette.litBar(tone, top: Double(height / tallest)))
                    .frame(width: EffortCapsule.barWidth, height: height)
                    .scaleEffect(x: 1, y: lit || reduceMotion ? 1 : 0.01, anchor: .bottom)
                    .opacity(lit ? 1 : 0)
                    .animation(barAnimation(index: index, lit: lit), value: lit)
            }
        }
    }

    private func barAnimation(index: Int, lit: Bool) -> Animation {
        if reduceMotion { return .easeInOut(duration: 0.2) }
        let order = lit ? index : EffortCapsule.barHeights.count - 1 - index
        return .spring(duration: 0.22).delay(Double(order) * 0.03)
    }
}

/// A soft 30° light band crossing the meter in 0.9 s every 2.6 s.
private struct ShimmerBand: View {
    var body: some View {
        let span = EffortCapsule.meterWidth + 12
        KeyframeAnimator(initialValue: CGFloat(0), repeating: true) { progress in
            LinearGradient(
                stops: [
                    .init(color: .white.opacity(0), location: 0),
                    .init(color: .white.opacity(0.75), location: 0.5),
                    .init(color: .white.opacity(0), location: 1),
                ],
                startPoint: .leading,
                endPoint: .trailing
            )
            .frame(width: 7, height: 18)
            .rotationEffect(.degrees(30))
            .offset(x: -span / 2 + progress * span)
        } keyframes: { _ in
            CubicKeyframe(1, duration: 0.9)
            LinearKeyframe(1, duration: 1.7)
        }
        .frame(width: EffortCapsule.meterWidth, height: EffortCapsule.barHeights.last)
    }
}
