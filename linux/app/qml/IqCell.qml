import QtQuick

// The 智力 cell of an agent row (DESIGN 4, 12): the score right-aligned, live in the number color, resting and `–` pale,
// with the proof mark under an unproven score. A rise of 3 or more rolls up, a fall crossfades.
Item {
    id: cell
    property int value: -1
    property string form: "none"
    property bool proof: false
    property int pixelSize: 12
    implicitHeight: digits.implicitHeight

    readonly property string label: value >= 0 ? String(value) : "–"
    readonly property color ink: value >= 0 && form === "live" ? Theme.ink : Theme.ink3
    property int shownValue: value

    onValueChanged: {
        if (value >= 0 && shownValue >= 0 && value - shownValue >= 3) roll.restart()
        else if (value !== shownValue) fade.restart()
        shownValue = value
    }

    Text {
        id: digits
        anchors.right: parent.right
        anchors.verticalCenter: parent.verticalCenter
        text: cell.label
        color: cell.ink
        font.pixelSize: cell.pixelSize
        font.weight: Font.Medium
        font.features: Theme.tabular
        Behavior on color { ColorAnimation { duration: 200 } }
        transform: Translate { id: shift }
    }
    ProofDots {
        anchors.right: digits.right
        y: digits.y + digits.baselineOffset + 0.22 * cell.pixelSize - height / 2
        textWidth: digits.advance.width > 0 ? digits.advance.width : digits.implicitWidth
        pixelSize: cell.pixelSize
        color: digits.color
        shown: cell.proof && cell.value >= 0
    }
    ParallelAnimation {
        id: roll
        NumberAnimation { target: shift; property: "y"; from: cell.pixelSize * 0.55; to: 0; duration: 240; easing.type: Easing.OutCubic }
        NumberAnimation { target: digits; property: "opacity"; from: 0.2; to: 1; duration: 240; easing.type: Easing.OutCubic }
    }
    NumberAnimation { id: fade; target: digits; property: "opacity"; from: 0.25; to: 1; duration: 200 }
}
