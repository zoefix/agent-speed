import QtQuick
import QtQuick.Controls

// The next report body, pretty-printed in the monospaced font at 11 px with keys in the secondary color (DESIGN 11).
// The text changes in place, so the scroll position stays while it refreshes every second.
Rectangle {
    id: box
    property string requestText
    radius: 8
    color: Theme.field
    border.width: 1
    border.color: Theme.border
    implicitHeight: 220
    Accessible.role: Accessible.EditableText
    Accessible.name: S.tr2(S.rev, "report.preview.title")
    Accessible.readOnly: true

    Flickable {
        id: flick
        anchors.fill: parent
        anchors.margins: 10
        contentWidth: Math.max(width, column.implicitWidth)
        contentHeight: column.implicitHeight
        clip: true
        boundsBehavior: Flickable.StopAtBounds
        ScrollBar.vertical: ScrollBar { policy: flick.contentHeight > flick.height ? ScrollBar.AsNeeded : ScrollBar.AlwaysOff }
        Column {
            id: column
            spacing: 8
            TextEdit {
                visible: box.requestText.length > 0
                text: box.requestText
                readOnly: true
                selectByMouse: true
                color: Theme.ink2
                font.family: "monospace"
                font.pixelSize: 11
            }
            TextEdit {
                id: bodyText
                textFormat: TextEdit.RichText
                text: report.previewRich(Theme.ink3)
                readOnly: true
                selectByMouse: true
                color: Theme.ink
                font.family: "monospace"
                font.pixelSize: 11
                Connections {
                    target: report
                    function onPreviewChanged() { bodyText.text = report.previewRich(Theme.ink3) }
                }
            }
        }
    }
}
