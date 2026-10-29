import AgentSpeedCore
import SwiftUI

/// The card's scale (DESIGN 12, *Score card*, *Log scale*): a 6 pt track whose fill runs to `f` of the value, painted
/// with the pearl gradient laid across the whole track so the color at the fill's end tells the level; ticks at 500,
/// 1000 (the reference, taller and stronger) and 2000, labels under them and 4000 at the end. A provisional fill is
/// round dots of the track's height on a 2.2 × pitch, a resting fill is at 45 %, and none draws the track alone.
struct PerfScale: View, Animatable {
    /// The value drawn; nil while none. Animatable, so the fill follows a counting number.
    var value: Double?
    var provisional: Bool
    var resting: Bool

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    nonisolated var animatableData: Double {
        get { value ?? 0 }
        set { if value != nil { value = newValue } }
    }

    static let ticks: [(value: Double, label: String)] = [(500, "500"), (1000, "1000"), (2000, "2000")]
    static let barHeight: CGFloat = PerfCardLayout.scaleTrack + 8

    var body: some View {
        let dark = colorScheme == .dark
        let increased = contrast == .increased
        VStack(spacing: 4) {
            Canvas { context, size in
                draw(in: &context, size: size, dark: dark, increased: increased)
            }
            .frame(height: Self.barHeight)
            labels
        }
        .accessibilityHidden(true)
    }

    private func draw(in context: inout GraphicsContext, size: CGSize, dark: Bool, increased: Bool) {
        let track = PerfCardLayout.scaleTrack
        let top = (size.height - track) / 2
        let bar = CGRect(x: 0, y: top, width: size.width, height: track)
        context.fill(Path(roundedRect: bar, cornerRadius: track / 2),
                     with: .color(PerfPalette.track(dark: dark, increasedContrast: increased)))
        if let value {
            let fill = CGFloat(PerfFormat.scale(value)) * size.width
            if fill > 0 {
                let colors = PerfPalette.pearl(dark: dark, increasedContrast: increased)
                let shading: GraphicsContext.Shading = colors.count > 1
                    ? .linearGradient(Gradient(colors: colors), startPoint: CGPoint(x: 0, y: top),
                                      endPoint: CGPoint(x: size.width, y: top))
                    : .color(colors[0])
                var path = Path()
                if provisional {
                    var x: CGFloat = 0
                    while x + track <= max(fill, track) + 0.01 {
                        path.addEllipse(in: CGRect(x: x, y: top, width: track, height: track))
                        x += 2.2 * track
                    }
                } else {
                    path.addRoundedRect(in: CGRect(x: 0, y: top, width: max(track, fill), height: track),
                                        cornerSize: CGSize(width: track / 2, height: track / 2))
                }
                var layer = context
                layer.opacity = resting ? 0.45 : 1
                layer.fill(path, with: shading)
            }
        }
        for tick in Self.ticks {
            let reference = tick.value == 1000
            let height = track + 4 + (reference ? 4 : 0)
            let x = (CGFloat(PerfFormat.scale(tick.value)) * size.width).rounded() - 0.5
            context.fill(Path(CGRect(x: x, y: (size.height - height) / 2, width: 1, height: height)),
                         with: .color(PerfPalette.tick(reference: reference, increasedContrast: increased)))
        }
    }

    private var labels: some View {
        GeometryReader { proxy in
            let width = proxy.size.width
            ZStack(alignment: .topLeading) {
                ForEach(Self.ticks, id: \.value) { tick in
                    let reference = tick.value == 1000
                    Text(verbatim: tick.label)
                        .font(reference ? PerfCardFont.scaleReference : PerfCardFont.scaleLabel)
                        .foregroundStyle(reference ? AnyShapeStyle(.secondary) : AnyShapeStyle(.tertiary))
                        .fixedSize()
                        .position(x: CGFloat(PerfFormat.scale(tick.value)) * width, y: 6)
                }
                Text(verbatim: "4000")
                    .font(PerfCardFont.scaleLabel)
                    .foregroundStyle(.tertiary)
                    .fixedSize()
                    .frame(width: width, height: 12, alignment: .trailing)
            }
        }
        .frame(height: 12)
    }
}

/// A component's mini meter (DESIGN 12): 44 × 4, radius 2, the sub-score on the log scale in the score ink, a 1 × 8 tick
/// at the middle (1000) in the label color at 35 %; an absent component is a dotted track without fill.
struct PerfMiniMeter: View {
    /// `f` of the sub-score; nil while the component is absent.
    var fraction: Double?
    var width: CGFloat = PerfCardLayout.meterWidth

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        let dark = colorScheme == .dark
        let increased = contrast == .increased
        let height = PerfCardLayout.meterHeight
        ZStack(alignment: .leading) {
            if let fraction {
                RoundedRectangle(cornerRadius: height / 2, style: .continuous)
                    .fill(PerfPalette.track(dark: dark, increasedContrast: increased))
                RoundedRectangle(cornerRadius: height / 2, style: .continuous)
                    .fill(PerfPalette.ink(dark: dark, increasedContrast: increased))
                    .frame(width: max(height, CGFloat(fraction) * width))
                    .opacity(fraction > 0 ? 1 : 0)
            } else {
                ProvisionalDots(diameter: height, color: PerfPalette.absentDots(increasedContrast: increased), pitch: 2.2)
            }
            Rectangle()
                .fill(Color(nsColor: .labelColor).opacity(increased ? 1 : 0.35))
                .frame(width: 1, height: 8)
                .offset(x: width / 2 - 0.5)
                .opacity(fraction == nil ? 0 : 1)
        }
        .frame(width: width, height: height)
        .accessibilityHidden(true)
    }
}
