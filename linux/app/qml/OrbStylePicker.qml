import QtQuick
import QtQuick.Layouts
import AgentSpeed.Orb

// DESIGN 5 Floating orb pane, `orbs.style`: five cards with a live preview each (the real renderer at 64 pt, Soft
// effects, cruise → redline → cruise every 8 s, each card 1.6 s after the previous), the name below; the selected one
// has a 2 pt accent ring and a faint accent fill. Arrow keys move between cards. Below: the name and description.
ColumnLayout {
    id: picker
    property string selected: "led"
    signal picked(string key)
    readonly property var styles: ["led", "needle", "liquid", "reactor", "minimal"]
    readonly property int columns: width >= 560 ? 5 : 3
    Layout.fillWidth: true
    spacing: 12

    GridLayout {
        id: grid
        Layout.fillWidth: true
        columns: picker.columns
        columnSpacing: 10
        rowSpacing: 10

        Repeater {
            model: picker.styles
            delegate: Rectangle {
                id: card
                required property string modelData
                required property int index
                readonly property bool current: picker.selected === modelData
                Layout.fillWidth: true
                Layout.preferredWidth: 120
                Layout.maximumWidth: 150
                implicitHeight: 112
                radius: 10
                color: current ? Qt.rgba(Theme.accent.r, Theme.accent.g, Theme.accent.b, Theme.dark ? 0.12 : 0.07)
                               : hover.hovered ? Theme.hover : Theme.card
                border.width: current ? 2 : 1
                border.color: current ? Theme.accent : (card.activeFocus ? Theme.connector : Theme.border)
                activeFocusOnTab: true
                Accessible.role: Accessible.RadioButton
                Accessible.name: S.tr2(S.rev, "orbs.preview.a11y").replace("%@", S.tr2(S.rev, "style." + modelData))
                Accessible.checked: current
                Behavior on color { ColorAnimation { duration: 140 } }

                OrbPreview {
                    anchors.horizontalCenter: parent.horizontalCenter
                    y: 4
                    width: 84
                    height: 80
                    diameter: 64
                    style: card.modelData
                    offset: card.index * 1.6
                    dark: Theme.dark
                }
                Text {
                    anchors.horizontalCenter: parent.horizontalCenter
                    anchors.bottom: parent.bottom
                    anchors.bottomMargin: 10
                    width: parent.width - 12
                    horizontalAlignment: Text.AlignHCenter
                    elide: Text.ElideRight
                    text: S.tr2(S.rev, "style." + card.modelData)
                    color: card.current ? Theme.ink : Theme.ink2
                    font.pixelSize: 12
                    font.weight: card.current ? Font.DemiBold : Font.Normal
                }
                HoverHandler { id: hover; cursorShape: Qt.PointingHandCursor }
                TapHandler { onTapped: { card.forceActiveFocus(); picker.picked(card.modelData) } }
                Keys.onPressed: (event) => {
                    const step = event.key === Qt.Key_Right || event.key === Qt.Key_Down ? 1
                               : event.key === Qt.Key_Left || event.key === Qt.Key_Up ? -1 : 0
                    if (step !== 0) {
                        const next = (card.index + step + picker.styles.length) % picker.styles.length
                        picker.picked(picker.styles[next])
                        const item = grid.children[next]
                        if (item) item.forceActiveFocus()
                        event.accepted = true
                    } else if (event.key === Qt.Key_Space || event.key === Qt.Key_Return) {
                        picker.picked(card.modelData)
                        event.accepted = true
                    }
                }
            }
        }
    }

    ColumnLayout {
        Layout.fillWidth: true
        Layout.leftMargin: 4
        spacing: 3
        Text {
            text: S.tr2(S.rev, "style." + picker.selected)
            color: Theme.ink
            font.pixelSize: 13
            font.weight: Font.DemiBold
        }
        Text {
            Layout.fillWidth: true
            text: S.tr2(S.rev, "style." + picker.selected + ".desc")
            color: Theme.ink2
            font.pixelSize: 12
            lineHeight: 1.25
            wrapMode: Text.Wrap
        }
    }
}
