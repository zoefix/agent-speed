import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// A scrolling settings pane with its title.
Flickable {
    id: page
    property string title
    default property alias content: body.data
    contentWidth: width
    contentHeight: body.implicitHeight + 64
    boundsBehavior: Flickable.StopAtBounds
    clip: true
    ScrollBar.vertical: ScrollBar {
        id: bar
        policy: page.contentHeight > page.height ? ScrollBar.AsNeeded : ScrollBar.AlwaysOff
        padding: 3
        background: null
        contentItem: Rectangle {
            implicitWidth: bar.hovered || bar.pressed ? 6 : 4
            radius: width / 2
            color: Theme.ink3
            opacity: bar.active ? 0.8 : 0
            Behavior on opacity { NumberAnimation { duration: 200 } }
        }
    }

    ColumnLayout {
        id: body
        x: 28
        y: 26
        width: page.width - 56
        spacing: 22
        Text {
            text: page.title
            color: Theme.ink
            font.pixelSize: 22
            font.weight: Font.DemiBold
            Layout.bottomMargin: -4
        }
    }
}
