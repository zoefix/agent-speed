import QtQuick

// A pill-shaped segmented control: `options` is a list of {value, label}.
Item {
    id: control
    property var options: []
    property var value
    signal activated(var value)
    implicitHeight: 28
    implicitWidth: strip.implicitWidth + 4
    activeFocusOnTab: true
    Accessible.role: Accessible.PageTabList

    readonly property int index: {
        for (var i = 0; i < options.length; ++i) if (options[i].value === value) return i
        return -1
    }
    Keys.onLeftPressed: if (index > 0) control.activated(options[index - 1].value)
    Keys.onRightPressed: if (index < options.length - 1) control.activated(options[index + 1].value)

    Rectangle {
        anchors.fill: parent
        radius: 8
        color: Theme.dark ? Qt.rgba(1, 1, 1, 0.06) : Qt.rgba(0, 0, 0, 0.05)
        border.width: control.activeFocus ? 1.5 : 0
        border.color: Theme.accent
    }
    Rectangle {
        id: thumb
        readonly property Item target: control.index >= 0 ? strip.children[control.index] : null
        visible: target !== null
        x: target ? strip.x + target.x : 0
        width: target ? target.width : 0
        y: 2
        height: parent.height - 4
        radius: 6
        color: Theme.dark ? Qt.rgba(1, 1, 1, 0.14) : "white"
        border.width: Theme.dark ? 0 : 0.5
        border.color: Qt.rgba(0, 0, 0, 0.1)
        Behavior on x { NumberAnimation { duration: 200; easing.type: Easing.OutCubic } }
        Behavior on width { NumberAnimation { duration: 200; easing.type: Easing.OutCubic } }
    }
    Row {
        id: strip
        x: 2
        y: 2
        height: parent.height - 4
        Repeater {
            model: control.options
            Item {
                required property var modelData
                required property int index
                width: Math.max(44, caption.implicitWidth + 22)
                height: strip.height
                Accessible.role: Accessible.PageTab
                Accessible.name: modelData.label
                Text {
                    id: caption
                    anchors.centerIn: parent
                    text: modelData.label
                    color: control.index === index ? Theme.ink : Theme.ink2
                    font.pixelSize: 12
                    font.weight: control.index === index ? Font.DemiBold : Font.Normal
                    Behavior on color { ColorAnimation { duration: 150 } }
                }
                MouseArea {
                    anchors.fill: parent
                    cursorShape: Qt.PointingHandCursor
                    onClicked: control.activated(modelData.value)
                }
            }
        }
    }
}
