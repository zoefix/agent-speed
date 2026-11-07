import CoreGraphics

/// A connected display: its key (display UUID) and usable area in global screen coordinates (y up).
public struct OrbDisplayInfo: Equatable, Sendable {
    public var key: String
    public var visible: CGRect

    public init(key: String, visible: CGRect) {
        self.key = key
        self.visible = visible
    }
}

/// Where an orb's window goes when it is created at launch or restored by undo (DESIGN 2b): its entry for the
/// arrangement (or the arrangement resolver's choice) resolved on that display, else the default corner of the
/// primary display; an orb overlapping one placed before it slides to the nearest free touching position.
public enum OrbLaunchPlacement {
    public static func center(
        of orb: OrbConfig,
        document: OrbsDocument,
        arrangement: String,
        displays: [OrbDisplayInfo],
        main: String,
        primary: OrbDisplayInfo?,
        placed: [OrbDisc] = []
    ) -> CGPoint? {
        let radius = CGFloat(orb.footprintDiameter) / 2
        let entries = OrbLayoutResolver.entries(document: document, arrangement: arrangement, previousArrangement: nil,
                                                connected: displays.map(\.key), main: main)
        var center: CGPoint
        var visible: CGRect
        if let entry = entries[orb.id] ?? nil, let display = displays.first(where: { $0.key == entry.display }) ?? primary {
            visible = display.visible
            center = entry.record.center(radius: radius, visible: visible)
        } else if let primary {
            visible = primary.visible
            center = OrbSnap.defaultCenter(radius: radius, in: visible)
        } else {
            return nil
        }
        if !OrbPlacement.isFree(center, radius: radius, others: placed) {
            center = OrbPlacement.resolveOverlap(center, radius: radius, others: placed, visible: visible)
        }
        return center
    }
}
