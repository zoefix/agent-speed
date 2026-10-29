import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "Format.js" as Format

// DESIGN 5: the settings window — a sidebar of sections and the selected pane. Every change applies at once.
Window {
    id: win
    width: 780
    height: 620
    minimumWidth: 700
    minimumHeight: 480
    title: S.tr2(S.rev, "settings.title")
    color: Theme.panel
    property int page: 0
    readonly property var sections: [
        { glyph: "sliders", key: "settings.general" },
        { glyph: "orb", key: "settings.orb" },
        { glyph: "sparkle", key: "settings.effects" },
        { glyph: "coin", key: "settings.cost" },
        { glyph: "folder", key: "settings.sources" },
        { glyph: "info", key: "settings.about" }
    ]

    function present(index) {
        if (index !== undefined && index >= 0) page = index
        show()
        raise()
        requestActivate()
    }

    Shortcut {
        sequences: [StandardKey.Close]
        onActivated: win.close()
    }

    RowLayout {
        anchors.fill: parent
        spacing: 0

        Rectangle {
            Layout.fillHeight: true
            Layout.preferredWidth: 208
            color: Theme.sidebar
            ColumnLayout {
                anchors.fill: parent
                anchors.margins: 14
                anchors.topMargin: 20
                spacing: 2
                RowLayout {
                    Layout.leftMargin: 8
                    Layout.bottomMargin: 16
                    spacing: 10
                    Gauge {
                        heat: appState.snapshot.heat || 0
                        Layout.preferredWidth: 26
                        Layout.preferredHeight: 26
                    }
                    Text {
                        text: S.tr2(S.rev, "app.name")
                        color: Theme.ink
                        font.pixelSize: 15
                        font.weight: Font.DemiBold
                    }
                }
                Repeater {
                    model: win.sections
                    Item {
                        id: item
                        required property var modelData
                        required property int index
                        readonly property bool current: win.page === index
                        Layout.fillWidth: true
                        implicitHeight: 34
                        activeFocusOnTab: true
                        Accessible.role: Accessible.PageTab
                        Accessible.name: S.tr2(S.rev, modelData.key)
                        Keys.onSpacePressed: win.page = index
                        Keys.onUpPressed: win.page = Math.max(0, win.page - 1)
                        Keys.onDownPressed: win.page = Math.min(win.sections.length - 1, win.page + 1)
                        Rectangle {
                            anchors.fill: parent
                            radius: 8
                            color: item.current ? (Theme.dark ? Qt.rgba(1, 1, 1, 0.09) : "white")
                                                : navArea.containsMouse ? Theme.hover : "transparent"
                            border.width: item.activeFocus ? 1.5 : (item.current && !Theme.dark ? 0.5 : 0)
                            border.color: item.activeFocus ? Theme.accent : Qt.rgba(0, 0, 0, 0.08)
                            Behavior on color { ColorAnimation { duration: 140 } }
                        }
                        Glyph {
                            id: navGlyph
                            x: 12
                            anchors.verticalCenter: parent.verticalCenter
                            width: 15
                            height: 15
                            name: item.modelData.glyph
                            color: item.current ? Theme.accent : Theme.ink2
                        }
                        Text {
                            anchors.left: navGlyph.right
                            anchors.leftMargin: 10
                            anchors.right: parent.right
                            anchors.verticalCenter: parent.verticalCenter
                            text: S.tr2(S.rev, item.modelData.key)
                            color: item.current ? Theme.ink : Theme.ink2
                            font.pixelSize: 13
                            font.weight: item.current ? Font.DemiBold : Font.Normal
                            elide: Text.ElideRight
                        }
                        MouseArea {
                            id: navArea
                            anchors.fill: parent
                            hoverEnabled: true
                            cursorShape: Qt.PointingHandCursor
                            onClicked: win.page = item.index
                        }
                    }
                }
                Item { Layout.fillHeight: true }
                Text {
                    Layout.leftMargin: 8
                    text: Format.fill(S.tr2(S.rev, "settings.about.version"), [catalog.version])
                    color: Theme.ink3
                    font.pixelSize: 11
                }
            }
        }
        Rectangle {
            Layout.fillHeight: true
            Layout.preferredWidth: 1
            color: Theme.border
        }
        StackLayout {
            Layout.fillWidth: true
            Layout.fillHeight: true
            currentIndex: win.page
            GeneralPage {}
            OrbPage {}
            EffectsPage {}
            CostPage {}
            SourcesPage {}
            AboutPage {}
        }
    }
}
