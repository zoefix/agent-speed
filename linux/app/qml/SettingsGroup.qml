import QtQuick
import QtQuick.Layouts

// A rounded card of settings rows; rows are separated by hairlines.
ColumnLayout {
    id: group
    property string title
    property string footnote
    // A quiet caption at the title's right end (a count such as 2/8).
    property string trailing
    default property alias rows: body.data
    Layout.fillWidth: true
    spacing: 6

    RowLayout {
        visible: group.title.length > 0
        Layout.fillWidth: true
        Layout.leftMargin: 4
        Layout.rightMargin: 4
        Text {
            Layout.fillWidth: true
            text: group.title
            color: Theme.ink2
            font.pixelSize: 12
            font.weight: Font.DemiBold
            elide: Text.ElideRight
            Accessible.role: Accessible.Heading
            Accessible.name: text
        }
        Text {
            visible: group.trailing.length > 0
            text: group.trailing
            color: Theme.ink3
            font.pixelSize: 11
            font.features: Theme.tabular
            Accessible.ignored: true
        }
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
