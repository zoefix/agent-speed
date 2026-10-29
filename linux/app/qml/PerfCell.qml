import QtQuick
import "Format.js" as Format

// The 性能 cell of an agent row (DESIGN 4, 12): the compact score right-aligned and tabular, live in the number color,
// resting and `–` pale, the provisional mark under a young score. A change of at least 1 % and 3 counts through the
// intermediate values, up or down; none ↔ a value crossfades.
Item {
    id: cell
    property real value: -1
    property string form: "none"
    property bool provisional: false
    property real pixelSize: 12
    property string family: ""
    implicitHeight: digits.implicitHeight

    property real shownValue: value
    readonly property color ink: value >= 0 && form === "live" ? Theme.ink : Theme.ink3

    onValueChanged: {
        const from = Math.round(shownValue)
        if (Format.perfCounts(from, value)) {
            count.stop()
            count.from = from
            count.to = value
            count.duration = Format.perfCountMs(from, value)
            count.start()
        } else {
            count.stop()
            if ((value >= 0) !== (shownValue >= 0)) fade.restart()
            shownValue = value
        }
    }

    NumberAnimation {
        id: count
        target: cell
        property: "shownValue"
        easing.type: Easing.OutCubic
    }

    Text {
        id: digits
        anchors.right: parent.right
        anchors.verticalCenter: parent.verticalCenter
        text: cell.shownValue >= 0 ? Format.perfCompact(Math.round(cell.shownValue)) : "–"
        color: cell.ink
        font.pixelSize: cell.pixelSize
        font.weight: Font.Medium
        font.family: cell.family.length > 0 ? cell.family : Qt.application.font.family
        font.features: Theme.tabular
        Behavior on color { ColorAnimation { duration: 200 } }
    }
    ProvisionalDots {
        anchors.right: digits.right
        y: digits.y + digits.baselineOffset + 0.22 * cell.pixelSize - height / 2
        textWidth: digits.advance.width > 0 ? digits.advance.width : digits.implicitWidth
        pixelSize: cell.pixelSize
        color: digits.color
        shown: cell.provisional && cell.value >= 0
    }
    NumberAnimation { id: fade; target: digits; property: "opacity"; from: 0; to: 1; duration: 150 }
}
