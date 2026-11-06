import QtQuick

// An animated on/off switch in the accent color.
Item {
    id: toggle
    property bool checked: false
    property string label
    signal toggled(bool checked)
    implicitWidth: 38
    implicitHeight: 22
    activeFocusOnTab: true
    opacity: enabled ? 1 : 0.45
    Accessible.role: Accessible.CheckBox
    Accessible.name: label
    Accessible.checkable: true
    Accessible.checked: checked
    Accessible.onToggleAction: flip()
    Keys.onSpacePressed: flip()

    function flip() { toggle.toggled(!toggle.checked) }

    Rectangle {
        anchors.fill: parent
        radius: height / 2
        color: toggle.checked ? Theme.accent : (Theme.dark ? Qt.rgba(1, 1, 1, 0.16) : Qt.rgba(0, 0, 0, 0.14))
        Behavior on color { ColorAnimation { duration: 160 } }
        // The keyboard focus ring sits outside the track, so it shows on the accent fill too.
        Rectangle {
            visible: toggle.activeFocus
            anchors.fill: parent
            anchors.margins: -3
            radius: height / 2
            color: "transparent"
            border.width: 2
            border.color: Theme.accent
            opacity: 0.85
        }
        Rectangle {
            width: 18
            height: 18
            radius: 9
            y: 2
            x: toggle.checked ? parent.width - width - 2 : 2
            color: "white"
            Behavior on x { NumberAnimation { duration: 180; easing.type: Easing.OutCubic } }
            Rectangle {
                anchors.fill: parent
                anchors.margins: -0.5
                radius: width / 2
                color: "transparent"
                border.width: 0.5
                border.color: Qt.rgba(0, 0, 0, 0.12)
            }
        }
    }
    MouseArea {
        anchors.fill: parent
        cursorShape: Qt.PointingHandCursor
        onClicked: toggle.flip()
    }
}
