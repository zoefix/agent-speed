import QtQuick
import QtQuick.Layouts

RowLayout {
    property alias text: title.text
    property alias trailing: caption.text
    Layout.fillWidth: true
    Layout.bottomMargin: 6
    spacing: 8

    Text {
        id: title
        color: Theme.ink2
        font.pixelSize: 12
        font.weight: Font.DemiBold
        font.letterSpacing: 0.3
    }
    Item { Layout.fillWidth: true }
    Text {
        id: caption
        visible: text.length > 0
        color: Theme.ink3
        font.pixelSize: 11
        font.weight: Font.Medium
    }
}
