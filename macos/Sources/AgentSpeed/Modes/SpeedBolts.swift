import SwiftUI

/// The speed's bolts (DESIGN 4, Agents): one for `fast` and `highspeed`, two overlapping for `ultrafast`, the rear one
/// at 60 %. A configured value draws them as outlines in its quiet color, never amber.
struct SpeedBolts: View {
    let count: Int
    var configured = false

    @Environment(\.self) private var environment

    var body: some View {
        HStack(spacing: -EffortCapsule.boltOverlap) {
            ForEach(0..<count, id: \.self) { index in
                Image(systemName: configured ? "bolt" : "bolt.fill")
                    .font(.system(size: EffortCapsule.boltPointSize))
                    .frame(width: EffortCapsule.boltSize.width, height: EffortCapsule.boltSize.height)
                    .opacity(count > 1 && index < count - 1 ? 0.6 : 1)
            }
        }
        .foregroundStyle(configured ? EffortCapsulePalette(environment).configuredInk : EffortCapsulePalette(environment).bolt)
        .accessibilityHidden(true)
    }
}
