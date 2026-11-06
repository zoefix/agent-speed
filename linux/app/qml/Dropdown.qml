import QtQuick
import QtQuick.Controls

// A compact picker: the current label and a chevron; the list opens in a popup. `options` is a list of {value, label}.
Item {
    id: control
    property var options: []
    property var value
    property int popupWidth: Math.max(width, 200)
    // Options may carry a `glyph`, drawn before the label. `labelMaxWidth` caps the current label (truncated in the
    // middle); `iconOnly` shows only the current option's glyph. `accessibleName` names the control itself.
    property real labelMaxWidth: -1
    property bool iconOnly: false
    property string accessibleName
    property string tooltip
    signal activated(var value)
    readonly property string currentGlyph: {
        for (var i = 0; i < options.length; ++i) if (options[i].value === value) return options[i].glyph || ""
        return ""
    }
    readonly property real glyphRoom: currentGlyph.length > 0 ? 20 : 0
    implicitWidth: iconOnly && currentGlyph.length > 0 ? 54
                   : Math.max(labelMaxWidth > 0 ? 0 : 140, (labelMaxWidth > 0 ? Math.min(label.implicitWidth, labelMaxWidth) : label.implicitWidth) + 44 + glyphRoom)
    implicitHeight: 28
    activeFocusOnTab: true
    Accessible.role: Accessible.ComboBox
    Accessible.name: accessibleName.length > 0 ? accessibleName : label.text
    Accessible.description: accessibleName.length > 0 ? control.currentLabel : ""
    HoverHandler { id: controlHover }
    ToolTip.visible: controlHover.hovered && !popup.opened && (control.tooltip.length > 0 || (control.iconOnly || label.truncated) && control.currentLabel.length > 0)
    ToolTip.delay: 600
    ToolTip.text: control.tooltip.length > 0 ? control.tooltip : control.currentLabel
    Keys.onSpacePressed: popup.open()
    Keys.onReturnPressed: popup.open()

    readonly property string currentLabel: {
        for (var i = 0; i < options.length; ++i) if (options[i].value === value) return options[i].label
        return ""
    }

    Rectangle {
        anchors.fill: parent
        radius: 7
        color: area.pressed ? Theme.pressed : area.containsMouse ? Theme.hover : Theme.field
        border.width: control.activeFocus ? 1.5 : 0
        border.color: Theme.accent
        Behavior on color { ColorAnimation { duration: 120 } }
    }
    Glyph {
        id: leading
        visible: control.currentGlyph.length > 0
        anchors.left: parent.left
        anchors.leftMargin: 10
        anchors.verticalCenter: parent.verticalCenter
        width: 14
        height: 14
        name: control.currentGlyph
        color: Theme.ink2
    }
    Text {
        id: label
        visible: !control.iconOnly || control.currentGlyph.length === 0
        anchors.left: leading.visible ? leading.right : parent.left
        anchors.leftMargin: leading.visible ? 6 : 11
        anchors.right: chevron.left
        anchors.rightMargin: 6
        anchors.verticalCenter: parent.verticalCenter
        text: control.currentLabel
        color: Theme.ink
        font.pixelSize: 12
        elide: control.labelMaxWidth > 0 ? Text.ElideMiddle : Text.ElideRight
    }
    Glyph {
        id: chevron
        anchors.right: parent.right
        anchors.rightMargin: 9
        anchors.verticalCenter: parent.verticalCenter
        width: 11
        height: 11
        name: "chevron"
        color: Theme.ink2
    }
    MouseArea {
        id: area
        anchors.fill: parent
        hoverEnabled: true
        cursorShape: Qt.PointingHandCursor
        onClicked: popup.opened ? popup.close() : popup.open()
    }

    Popup {
        id: popup
        y: control.height + 4
        x: control.width - width
        // Pushed inside the window when it would run past an edge (a wide list under a narrow control).
        margins: 8
        width: control.popupWidth
        padding: 5
        implicitHeight: Math.min(list.contentHeight + 10, 320)
        background: Rectangle {
            radius: 10
            color: Theme.dark ? "#22252C" : "#FFFFFF"
            border.width: 1
            border.color: Theme.border
        }
        enter: Transition { NumberAnimation { property: "opacity"; from: 0; to: 1; duration: 120 } }
        exit: Transition { NumberAnimation { property: "opacity"; from: 1; to: 0; duration: 90 } }
        contentItem: ListView {
            id: list
            clip: true
            model: control.options
            boundsBehavior: Flickable.StopAtBounds
            delegate: Item {
                required property var modelData
                width: list.width
                height: 30
                Rectangle {
                    anchors.fill: parent
                    radius: 6
                    color: rowArea.containsMouse ? Theme.hover : "transparent"
                }
                Glyph {
                    id: optionGlyph
                    visible: (modelData.glyph || "").length > 0
                    anchors.left: parent.left
                    anchors.leftMargin: 10
                    anchors.verticalCenter: parent.verticalCenter
                    width: 14
                    height: 14
                    name: modelData.glyph || ""
                    color: Theme.ink2
                }
                Text {
                    anchors.left: optionGlyph.visible ? optionGlyph.right : parent.left
                    anchors.leftMargin: optionGlyph.visible ? 8 : 10
                    anchors.right: check.left
                    anchors.rightMargin: 6
                    anchors.verticalCenter: parent.verticalCenter
                    text: modelData.label
                    color: Theme.ink
                    font.pixelSize: 12
                    elide: optionGlyph.visible ? Text.ElideMiddle : Text.ElideRight
                }
                Glyph {
                    id: check
                    anchors.right: parent.right
                    anchors.rightMargin: 8
                    anchors.verticalCenter: parent.verticalCenter
                    width: 13
                    height: 13
                    name: "check"
                    color: Theme.accent
                    visible: modelData.value === control.value
                }
                MouseArea {
                    id: rowArea
                    anchors.fill: parent
                    hoverEnabled: true
                    cursorShape: Qt.PointingHandCursor
                    onClicked: {
                        control.activated(modelData.value)
                        popup.close()
                    }
                }
            }
        }
    }
}
