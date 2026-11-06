import QtQuick

// 預設: the automatic entry's mark, caption weight, secondary on a faint capsule (DESIGN 5).
Rectangle {
    id: badge
    property alias text: caption.text
    implicitWidth: caption.implicitWidth + 12
    implicitHeight: caption.implicitHeight + 2
    radius: height / 2
    color: Theme.capsuleFill
    Accessible.ignored: true

    Text {
        id: caption
        anchors.centerIn: parent
        color: Theme.ink2
        font.pixelSize: 10
        font.weight: Font.Medium
    }
}
