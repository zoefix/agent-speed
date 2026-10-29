import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// DESIGN 11 Preview window: the request line and headers, then the body of the next report, refreshed every second.
Window {
    id: win
    width: 560
    height: 640
    minimumWidth: 420
    minimumHeight: 360
    title: S.tr2(S.rev, "report.preview.title")
    color: Theme.panel
    onVisibleChanged: report.watchPreview(visible)
    Component.onCompleted: if (visible) report.watchPreview(true)

    Shortcut {
        sequences: [StandardKey.Close, "Esc"]
        onActivated: win.close()
    }

    ColumnLayout {
        anchors.fill: parent
        anchors.margins: 18
        spacing: 12
        Text {
            text: S.tr2(S.rev, "report.preview.title")
            color: Theme.ink
            font.pixelSize: 17
            font.weight: Font.DemiBold
        }
        ReportPreviewText {
            Layout.fillWidth: true
            Layout.fillHeight: true
            requestText: report.previewRequest
        }
        Text {
            text: report.previewHint
            color: Theme.ink3
            font.pixelSize: 11
            wrapMode: Text.Wrap
            Layout.fillWidth: true
        }
        RowLayout {
            Layout.fillWidth: true
            Text {
                text: report.previewFooter
                color: Theme.ink2
                font.pixelSize: 12
                Layout.fillWidth: true
            }
            PillButton {
                text: S.tr2(S.rev, "report.preview.copy")
                onClicked: report.copyPreview()
            }
        }
    }
}
