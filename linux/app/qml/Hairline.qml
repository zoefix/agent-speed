import QtQuick
import QtQuick.Layouts

// The 16 pt gap between dashboard sections with its hairline in the middle.
Item {
    Layout.fillWidth: true
    implicitHeight: 33
    Rectangle {
        y: 16
        width: parent.width
        height: 1
        color: Theme.hairline
    }
}
