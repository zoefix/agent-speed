import SwiftUI

/// The effort capsule (DESIGN 4, Agents): meter, word and bolts in a fully rounded capsule. Given a column width it
/// stretches to fill it, so meters start on one vertical line and bolts end on another.
struct EffortCapsuleView: View {
    let capsule: EffortCapsule
    /// Drops the word (the column would leave the name too little room).
    var compact = false
    /// Width of the shared column; nil draws the capsule at its own width.
    var width: CGFloat?
    var reduceMotion = false
    /// Top-rung shimmer; never with Reduce Motion or effects Off.
    var shimmers = true

    @Environment(\.self) private var environment

    var body: some View {
        ZStack {
            face(EffortCapsulePalette(environment))
                .id(capsule.provenance)
                .transition(.opacity)
        }
        .animation(.easeInOut(duration: 0.2), value: capsule.provenance)
        .frame(width: width ?? capsule.width(compact: compact), height: EffortCapsule.height)
        .opacity(capsule.isVisible ? 1 : 0)
    }

    private func face(_ palette: EffortCapsulePalette) -> some View {
        let word = capsule.showsWord(compact: compact) ? capsule.effort : nil
        let mark = capsule.showsMark(compact: compact) ? capsule.mark : nil
        let leads = capsule.level != nil || word != nil

        return HStack(spacing: 0) {
            HStack(spacing: EffortCapsule.partSpacing) {
                if let level = capsule.level {
                    EffortMeter(level: level, tone: capsule.tone, reduceMotion: reduceMotion, shimmers: shimmers)
                        .transition(.opacity)
                }
                if let word {
                    Text(word)
                        .font(.system(size: EffortCapsule.wordFontSize, weight: .medium))
                        .foregroundStyle(palette.word(capsule.tone))
                        .lineLimit(1)
                        .fixedSize()
                        .id(word)
                        .transition(.opacity)
                }
                if let mark {
                    Text(mark)
                        .font(.system(size: EffortCapsule.markFontSize, weight: .medium))
                        .foregroundStyle(palette.configuredInk)
                        .lineLimit(1)
                        .fixedSize()
                        .transition(.opacity)
                }
            }
            .animation(.easeInOut(duration: 0.2), value: word)
            Spacer(minLength: leads && capsule.bolts > 0 ? EffortCapsule.partSpacing : 0)
            if capsule.bolts > 0 {
                SpeedBolts(count: capsule.bolts, configured: capsule.speedIsConfigured)
                    .id(capsule.bolts)
                    .transition(boltTransition)
            }
        }
        .animation(reduceMotion ? .easeInOut(duration: 0.2) : .spring(duration: 0.3), value: capsule.bolts)
        .padding(.horizontal, EffortCapsule.padding)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background { background(palette) }
    }

    @ViewBuilder
    private func background(_ palette: EffortCapsulePalette) -> some View {
        if capsule.isConfiguredForm {
            Capsule().strokeBorder(palette.configuredStroke, style: StrokeStyle(
                lineWidth: palette.configuredStrokeWidth, lineCap: .round, dash: [3, 2.5]))
        } else {
            Capsule()
                .fill(palette.fill)
                .overlay(Capsule().strokeBorder(palette.stroke, lineWidth: palette.strokeWidth))
        }
    }

    /// Bolts pop in (scale 0.6 to 1) and fade out in 0.15 s; Reduce Motion crossfades.
    private var boltTransition: AnyTransition {
        guard !reduceMotion else { return .opacity }
        return .asymmetric(
            insertion: .scale(scale: 0.6).combined(with: .opacity),
            removal: .opacity.animation(.easeOut(duration: 0.15))
        )
    }
}
