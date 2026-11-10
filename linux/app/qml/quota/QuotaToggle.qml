import QtQuick
import QtQuick.Layouts

// DESIGN 5 額度: one quota switch after its group's source toggles — the title, its hint (secondary), the unverified
// caption (tertiary) and, while on, the status line (warning style for problems). A nested switch is indented and
// follows its parent. The row dims while its source is off and hides its status.
Item {
    id: row
    property string switchKey
    property string title
    property string hint
    property bool unverified: false
    property bool nested: false
    // The source itself is enabled (Data sources).
    property bool sourceActive: true
    // The parent switch is on (Grok's live request under the log switch).
    property bool parentOn: true
    property string statusText
    property bool statusWarning: false
    // A healthy status (ok, connected) gets the check mark; others the quiet ring.
    property bool statusOk: false
    // Extra controls under the captions while on (ZCode's key and region).
    default property alias extra: extraColumn.data
    readonly property bool on: prefs.quotaRevision >= 0 && prefs.quotaSwitch(switchKey)
    readonly property bool active: sourceActive && parentOn
    // The status shows while on; a failed install shows it while off too.
    property bool showStatus: on
    signal toggledTo(bool on)

    Layout.fillWidth: true
    implicitHeight: content.implicitHeight + 22
    opacity: sourceActive ? 1 : 0.5
    Behavior on opacity { NumberAnimation { duration: 160 } }

    RowLayout {
        id: content
        anchors.left: parent.left
        anchors.leftMargin: row.nested ? 33 + 19 : 33
        anchors.right: parent.right
        anchors.rightMargin: 14
        anchors.verticalCenter: parent.verticalCenter
        spacing: 10
        ColumnLayout {
            Layout.fillWidth: true
            spacing: 3
            Text {
                textFormat: Text.PlainText
                Layout.fillWidth: true
                text: row.title
                color: row.active ? Theme.ink : Theme.ink2
                font.pixelSize: 13
                wrapMode: Text.Wrap
            }
            Text {
                textFormat: Text.PlainText
                visible: row.hint.length > 0
                Layout.fillWidth: true
                text: row.hint
                color: Theme.ink2
                font.pixelSize: 11
                lineHeight: 1.25
                wrapMode: Text.Wrap
            }
            Text {
                textFormat: Text.PlainText
                visible: row.unverified
                Layout.fillWidth: true
                text: S.tr2(S.rev, "settings.quota.unverified")
                color: Theme.ink3
                font.pixelSize: 11
                lineHeight: 1.25
                wrapMode: Text.Wrap
            }
            RowLayout {
                visible: row.showStatus && row.sourceActive && row.statusText.length > 0
                Layout.topMargin: 2
                Layout.fillWidth: true
                spacing: 5
                Glyph {
                    Layout.preferredWidth: 11
                    Layout.preferredHeight: 11
                    Layout.alignment: Qt.AlignTop
                    Layout.topMargin: 1
                    name: row.statusWarning ? "warning" : row.statusOk ? "checkCircle" : "dotRing"
                    color: row.statusWarning ? (Theme.dark ? "#FB923C" : "#E86A0C") : row.statusOk ? Theme.good : Theme.ink3
                    Accessible.ignored: true
                }
                Text {
                    textFormat: Text.PlainText
                    Layout.fillWidth: true
                    text: row.statusText
                    color: row.statusWarning ? (Theme.dark ? "#FDBA74" : "#C2410C") : Theme.ink2
                    font.pixelSize: 11
                    font.features: Theme.tabular
                    wrapMode: Text.Wrap
                }
            }
            ColumnLayout {
                id: extraColumn
                visible: row.on && row.sourceActive && children.length > 0
                Layout.fillWidth: true
                Layout.topMargin: 6
                spacing: 8
            }
        }
        ToggleSwitch {
            Layout.alignment: Qt.AlignTop
            label: row.title
            checked: row.on
            enabled: row.active
            Accessible.description: [row.hint, row.on ? row.statusText : ""].filter(t => t.length > 0).join(" ")
            onToggled: (on) => row.toggledTo(on)
        }
    }

    Rectangle {
        anchors.left: parent.left
        anchors.leftMargin: 14
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        height: 1
        color: Theme.hairline
    }
}
