import QtQuick
import QtQuick.Layouts

// A rounded card of settings rows; rows are separated by hairlines.
ColumnLayout {
    id: group
    property string title
    property string footnote
    default property alias rows: body.data
    Layout.fillWidth: true
    spacing: 6

    Text {
        visible: group.title.length > 0
        text: group.title
        color: Theme.ink2
        font.pixelSize: 12
        font.weight: Font.DemiBold
        Layout.leftMargin: 4
    }
    Rectangle {
        Layout.fillWidth: true
        implicitHeight: body.implicitHeight
        radius: 10
        color: Theme.card
        border.width: 1
        border.color: Theme.border
        ColumnLayout {
            id: body
            width: parent.width
            spacing: 0
        }
    }
    Text {
        visible: group.footnote.length > 0
        text: group.footnote
        color: Theme.ink3
        font.pixelSize: 11
        lineHeight: 1.25
        wrapMode: Text.Wrap
        Layout.fillWidth: true
        Layout.leftMargin: 4
        Layout.rightMargin: 4
    }
}
