import QtQuick
import QtQuick.Layouts

// A field's problem right under it: a small red mark and the sentence, wrapping in the field's column.
RowLayout {
    id: error
    property string text
    spacing: 4
    visible: text.length > 0
    Glyph {
        Layout.alignment: Qt.AlignTop
        Layout.topMargin: 1
        Layout.preferredWidth: 11
        Layout.preferredHeight: 11
        name: "alertCircle"
        color: Theme.danger
    }
    Text {
        Layout.fillWidth: true
        text: error.text
        color: Theme.ink2
        font.pixelSize: 11
        lineHeight: 1.2
        wrapMode: Text.Wrap
    }
}
