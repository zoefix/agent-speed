import AgentSpeedOrbModel
import AppKit

/// The last row of an orb's size submenu (DESIGN 2b, Context menu): a continuous slider over the shared range in 4 pt
/// steps with the value in points at the trailing edge. The orb resizes while the thumb moves. The row never takes
/// keyboard focus: NSMenu stops navigating while a view in it is first responder, and the orb's menu belongs to an
/// app that is usually not active, so the arrow keys keep moving between rows; VoiceOver reads and adjusts the slider
/// like the Settings one.
@MainActor
final class OrbSizeSliderItem: NSMenuItem {
    private static let width: CGFloat = 232
    private static let height: CGFloat = 30
    /// The track starts under the item titles and the value ends with the separator above it.
    private static let leading: CGFloat = 28
    private static let trailing: CGFloat = 14
    private static let valueWidth: CGFloat = 46
    private static let gap: CGFloat = 8

    private let slider: OrbSizeSlider
    private let valueLabel: NSTextField
    private let valueText: (Int) -> String
    private let onChange: @MainActor (Double) -> Void
    private var size: Double

    /// `label` names the slider for accessibility; `valueText` formats a size (`%d pt`).
    init(size: Double, label: String, valueText: @escaping (Int) -> String, onChange: @escaping @MainActor (Double) -> Void) {
        let size = OrbSizes.clamp(size)
        self.size = size
        self.valueText = valueText
        self.onChange = onChange
        slider = OrbSizeSlider(value: size, minValue: OrbSizes.range.lowerBound, maxValue: OrbSizes.range.upperBound,
                               target: nil, action: nil)
        valueLabel = NSTextField(labelWithString: valueText(Int(size.rounded())))
        super.init(title: label, action: nil, keyEquivalent: "")

        let row = NSView(frame: NSRect(x: 0, y: 0, width: Self.width, height: Self.height))
        row.autoresizingMask = [.width]
        let valueX = Self.width - Self.trailing - Self.valueWidth
        slider.frame = NSRect(x: Self.leading, y: 0, width: valueX - Self.gap - Self.leading, height: Self.height)
        slider.autoresizingMask = [.width]
        slider.controlSize = .small
        slider.isContinuous = true
        slider.refusesFirstResponder = true
        slider.target = self
        slider.action = #selector(sliderMoved(_:))
        slider.step = { [weak self] direction in self?.step(direction) }
        slider.setAccessibilityLabel(label)

        let font = NSFont.menuFont(ofSize: 0)
        valueLabel.font = .monospacedDigitSystemFont(ofSize: font.pointSize, weight: .regular)
        valueLabel.textColor = .secondaryLabelColor
        valueLabel.alignment = .right
        valueLabel.lineBreakMode = .byClipping
        valueLabel.sizeToFit()
        let labelHeight = valueLabel.frame.height
        valueLabel.frame = NSRect(x: valueX, y: ((Self.height - labelHeight) / 2).rounded(), width: Self.valueWidth, height: labelHeight)
        valueLabel.autoresizingMask = [.minXMargin]
        valueLabel.setAccessibilityElement(false)

        row.addSubview(slider)
        row.addSubview(valueLabel)
        view = row
        updateValue()
    }

    @available(*, unavailable)
    required init(coder: NSCoder) {
        fatalError("init(coder:) is not supported")
    }

    @objc private func sliderMoved(_ sender: NSSlider) {
        apply((sender.doubleValue / OrbSizes.step).rounded() * OrbSizes.step)
    }

    private func step(_ direction: Double) {
        apply(size + direction * OrbSizes.step)
    }

    private func apply(_ requested: Double) {
        let next = OrbSizes.clamp(requested)
        if slider.doubleValue != next { slider.doubleValue = next }
        guard next != size else { return }
        size = next
        updateValue()
        onChange(next)
    }

    private func updateValue() {
        let text = valueText(Int(size.rounded()))
        valueLabel.stringValue = text
        slider.setAccessibilityValueDescription(text)
    }
}

/// A slider that VoiceOver steps by whole size steps instead of its cell's default amount.
@MainActor
private final class OrbSizeSlider: NSSlider {
    var step: ((Double) -> Void)?

    override func accessibilityPerformIncrement() -> Bool {
        step?(1)
        return true
    }

    override func accessibilityPerformDecrement() -> Bool {
        step?(-1)
        return true
    }
}
