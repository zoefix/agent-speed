import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import AgentSpeed.Orb
import "Format.js" as Format
import "Heat.js" as Heat

// DESIGN 5 Title themes (一般): the theme cards, the custom sets and the custom set editor. Every change applies live.
ColumnLayout {
    id: group
    Layout.fillWidth: true
    spacing: 10
    readonly property int columns: width < 520 ? 2 : 3
    readonly property real cardWidth: (width - 8 * (columns - 1)) / columns
    function heatFor(level) {
        if (level <= 0) return Theme.ink3
        return Theme.heatText(Heat.color(Heat.heatOf(themes.threshold(level, prefs.redline), prefs.redline)))
    }

    Text {
        text: S.tr2(S.rev, "titles.theme")
        color: Theme.ink2
        font.pixelSize: 12
        font.weight: Font.DemiBold
        Layout.leftMargin: 4
    }
    Text {
        text: S.tr2(S.rev, "titles.theme.hint")
        color: Theme.ink3
        font.pixelSize: 11
        wrapMode: Text.Wrap
        Layout.fillWidth: true
        Layout.leftMargin: 4
        Layout.topMargin: -6
    }

    component ThemeCard: Rectangle {
        id: card
        required property var theme
        Layout.preferredWidth: group.cardWidth
        Layout.maximumWidth: group.cardWidth
        implicitHeight: 64
        radius: 10
        color: theme.selected ? Qt.rgba(Theme.accent.r, Theme.accent.g, Theme.accent.b, Theme.dark ? 0.16 : 0.08)
                             : hover.hovered ? Theme.hover : Theme.card
        border.width: theme.selected ? 2 : 1
        border.color: theme.selected ? Theme.accent : Theme.border
        Behavior on color { ColorAnimation { duration: 200; easing.type: Easing.OutCubic } }
        activeFocusOnTab: true
        Accessible.role: Accessible.RadioButton
        Accessible.checked: theme.selected
        Accessible.name: Format.fill(S.tr2(S.rev, "titles.card.a11y"), [theme.name, theme.strip.map(s => s.title).join(", ")])
        Keys.onSpacePressed: themes.select(theme.id)
        Keys.onReturnPressed: themes.select(theme.id)
        Keys.onDeletePressed: if (theme.custom) themes.remove(theme.id)
        HoverHandler { id: hover }
        TapHandler {
            acceptedButtons: Qt.LeftButton
            onTapped: themes.select(card.theme.id)
        }
        TapHandler {
            acceptedButtons: Qt.RightButton
            onTapped: menu.popup()
        }
        ColumnLayout {
            anchors.fill: parent
            anchors.margins: 10
            spacing: 4
            Text {
                text: card.theme.name
                color: Theme.ink
                font.pixelSize: 13
                font.weight: Font.DemiBold
                elide: Text.ElideRight
                Layout.fillWidth: true
                Layout.rightMargin: 18
            }
            Text {
                Layout.fillWidth: true
                textFormat: Text.StyledText
                elide: Text.ElideRight
                font.pixelSize: 11
                color: Theme.ink3
                text: {
                    themes.rev
                    var parts = []
                    for (var i = 0; i < card.theme.strip.length; ++i) {
                        var s = card.theme.strip[i]
                        parts.push("<font color='" + group.heatFor(s.level) + "'>" + s.title.replace(/&/g, "&amp;").replace(/</g, "&lt;") + "</font>")
                    }
                    return parts.join(" · ")
                }
            }
        }
        Rectangle {
            visible: card.theme.selected
            anchors.right: parent.right
            anchors.top: parent.top
            anchors.margins: 7
            width: 16
            height: 16
            radius: 8
            color: Theme.accent
            Glyph {
                anchors.centerIn: parent
                name: "check"
                width: 10
                height: 10
                color: "white"
            }
        }
        IconButton {
            visible: !card.theme.selected && (hover.hovered || card.activeFocus)
            anchors.right: parent.right
            anchors.top: parent.top
            anchors.margins: 2
            glyph: "dotRing"
            label: S.tr2(S.rev, "titles.custom.more.a11y")
            onClicked: menu.popup()
        }
        Menu {
            id: menu
            MenuItem {
                visible: !card.theme.custom
                height: visible ? implicitHeight : 0
                text: S.tr2(S.rev, "titles.duplicateEdit")
                enabled: themes.canCreate
                onTriggered: themes.createFrom(card.theme.id)
            }
            MenuItem {
                visible: card.theme.custom
                height: visible ? implicitHeight : 0
                text: S.tr2(S.rev, "titles.custom.edit")
                onTriggered: themes.select(card.theme.id)
            }
            MenuItem {
                visible: card.theme.custom
                height: visible ? implicitHeight : 0
                text: S.tr2(S.rev, "titles.custom.duplicate")
                enabled: themes.canCreate
                onTriggered: themes.createFrom(card.theme.id)
            }
            MenuItem {
                visible: card.theme.custom
                height: visible ? implicitHeight : 0
                text: S.tr2(S.rev, "titles.custom.delete")
                onTriggered: themes.remove(card.theme.id)
            }
        }
    }

    GridLayout {
        Layout.fillWidth: true
        Layout.alignment: Qt.AlignLeft
        columns: group.columns
        columnSpacing: 8
        rowSpacing: 8
        Repeater {
            model: themes.presets
            ThemeCard {
                required property var modelData
                theme: modelData
            }
        }
    }
    Text {
        text: S.tr2(S.rev, "titles.custom")
        color: Theme.ink3
        font.pixelSize: 11
        font.weight: Font.DemiBold
        Layout.leftMargin: 4
        Layout.topMargin: 4
    }
    GridLayout {
        Layout.fillWidth: true
        Layout.alignment: Qt.AlignLeft
        columns: group.columns
        columnSpacing: 8
        rowSpacing: 8
        Repeater {
            model: themes.customSets
            ThemeCard {
                required property var modelData
                theme: modelData
            }
        }
        // The dashed card that copies the selected theme.
        Item {
            Layout.preferredWidth: group.cardWidth
            Layout.maximumWidth: group.cardWidth
            implicitHeight: 64
            opacity: themes.canCreate ? 1 : 0.45
            activeFocusOnTab: true
            Accessible.role: Accessible.Button
            Accessible.name: S.tr2(S.rev, "titles.custom.new")
            Keys.onSpacePressed: if (themes.canCreate) themes.createFrom(themes.selected)
            Canvas {
                anchors.fill: parent
                onPaint: {
                    var ctx = getContext("2d")
                    ctx.reset()
                    ctx.strokeStyle = Theme.ink3
                    ctx.setLineDash([4, 3])
                    ctx.lineWidth = 1
                    ctx.beginPath()
                    ctx.roundedRect(0.5, 0.5, width - 1, height - 1, 10, 10)
                    ctx.stroke()
                }
            }
            Text {
                anchors.centerIn: parent
                text: "+  " + S.tr2(S.rev, "titles.custom.new")
                color: Theme.accent
                font.pixelSize: 13
                font.weight: Font.Medium
            }
            MouseArea {
                anchors.fill: parent
                enabled: themes.canCreate
                cursorShape: Qt.PointingHandCursor
                hoverEnabled: true
                onClicked: themes.createFrom(themes.selected)
                ToolTip.visible: containsMouse && !themes.canCreate
                ToolTip.text: S.tr2(S.rev, "titles.custom.limit")
            }
        }
    }
    // The banner for a delete or a reset of all titles, with undo for 8 s.
    Rectangle {
        visible: themes.banner.length > 0
        Layout.fillWidth: true
        implicitHeight: 36
        radius: 8
        color: Theme.field
        RowLayout {
            anchors.fill: parent
            anchors.leftMargin: 12
            anchors.rightMargin: 8
            Text {
                text: themes.banner
                color: Theme.ink
                font.pixelSize: 12
                Layout.fillWidth: true
            }
            PillButton {
                text: S.tr2(S.rev, "orbs.undo")
                onClicked: themes.undo()
            }
        }
    }
    // The custom set editor.
    SettingsGroup {
        visible: themes.selectedCustom
        Item {
            Layout.fillWidth: true
            implicitHeight: editorColumn.implicitHeight + 24
            ColumnLayout {
                id: editorColumn
                anchors.left: parent.left
                anchors.right: parent.right
                anchors.margins: 14
                anchors.verticalCenter: parent.verticalCenter
                spacing: 8
                Text {
                    text: S.tr2(S.rev, "titles.custom.editing")
                    color: Theme.ink
                    font.pixelSize: 17
                    font.weight: Font.DemiBold
                }
                RowLayout {
                    Layout.fillWidth: true
                    spacing: 10
                    Text {
                        text: S.tr2(S.rev, "titles.custom.name")
                        color: Theme.ink
                        font.pixelSize: 13
                    }
                    FieldInput {
                        Layout.fillWidth: true
                        text: themes.editor.name || ""
                        accessibleName: S.tr2(S.rev, "titles.custom.name")
                        onCommitted: (t) => themes.setName(t)
                    }
                }
                RowLayout {
                    Layout.fillWidth: true
                    spacing: 10
                    Text {
                        text: S.tr2(S.rev, "titles.custom.base")
                        color: Theme.ink
                        font.pixelSize: 13
                    }
                    Dropdown {
                        value: themes.editor.base || "vehicles"
                        options: themes.presets.map(p => ({ value: p.id, label: p.name }))
                        onActivated: (v) => themes.setBase(v)
                    }
                }
                Text {
                    text: S.tr2(S.rev, "titles.custom.baseHint") + (themes.vehicleArt ? " " + S.tr2(S.rev, "titles.custom.vehicleArt") : "")
                    color: Theme.ink3
                    font.pixelSize: 11
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                }
                // Live preview: the dashboard hero at 112 pt and a 96 pt orb in the selected orb's style, at the focused
                // row's level × 1.05 of its threshold (idle row: idle; no field focused: level 13).
                RowLayout {
                    id: livePreview
                    Layout.fillWidth: true
                    Layout.topMargin: 4
                    Layout.bottomMargin: 4
                    spacing: 18
                    property int level: 13
                    readonly property var rows: themes.editor.rows || []
                    readonly property real value: {
                        for (let i = 0; i < rows.length; ++i) if (rows[i].level === level) return level === 0 ? 0 : rows[i].threshold * 1.05
                        return 0
                    }
                    readonly property string orbStyle: {
                        const list = orbs.list
                        for (let i = 0; i < list.length; ++i) if (list[i].id === orbs.selected) return list[i].style
                        return list.length > 0 ? list[0].style : "led"
                    }
                    Item { Layout.fillWidth: true }
                    Column {
                        spacing: 4
                        Item {
                            width: 132
                            height: 132
                            HeroOrb {
                                anchors.centerIn: parent
                                width: 150
                                height: 150
                                diameter: 112
                                dark: Theme.dark
                                redline: prefs.redline
                                effects: 2
                                fps: 30
                                snapshot: ({ liveOutput: livePreview.value })
                            }
                        }
                        Text {
                            anchors.horizontalCenter: parent.horizontalCenter
                            text: S.tr2(S.rev, "menu.dashboard") + " · 112 pt"
                            color: Theme.ink3
                            font.pixelSize: 11
                        }
                    }
                    Column {
                        spacing: 4
                        OrbPreview {
                            width: 132
                            height: 132
                            diameter: 96
                            style: livePreview.orbStyle
                            dark: Theme.dark
                            fps: 30
                            liveValue: livePreview.value
                            redline: prefs.redline
                            valueText: Format.speed(livePreview.value)
                            titleText: themes.rev >= 0 ? themes.title(livePreview.level) : ""
                        }
                        Text {
                            anchors.horizontalCenter: parent.horizontalCenter
                            text: S.tr2(S.rev, "settings.orb") + " · 96 pt"
                            color: Theme.ink3
                            font.pixelSize: 11
                        }
                    }
                    Item { Layout.fillWidth: true }
                }
                // The title table: the idle row, then levels 1–13 with their thresholds at the current redline.
                RowLayout {
                    Layout.fillWidth: true
                    Layout.topMargin: 4
                    Text {
                        Layout.preferredWidth: 96
                        horizontalAlignment: Text.AlignRight
                        text: Format.fill(S.tr2(S.rev, "titles.custom.speedColumn"), [S.tr2(S.rev, "unit.tokS")])
                        color: Theme.ink3
                        font.pixelSize: 11
                    }
                    Text {
                        Layout.leftMargin: 10
                        text: S.tr2(S.rev, "titles.custom.titleColumn")
                        color: Theme.ink3
                        font.pixelSize: 11
                    }
                }
                Repeater {
                    model: themes.editor.rows || []
                    RowLayout {
                        required property var modelData
                        Layout.fillWidth: true
                        implicitHeight: 30
                        spacing: 10
                        Text {
                            Layout.preferredWidth: 22
                            horizontalAlignment: Text.AlignRight
                            text: modelData.level > 0 ? modelData.level : ""
                            color: Theme.ink3
                            font.pixelSize: 11
                        }
                        Text {
                            Layout.preferredWidth: 64
                            horizontalAlignment: Text.AlignRight
                            text: modelData.level === 0 ? S.tr2(S.rev, "titles.custom.idleRow") : Format.speed(modelData.threshold)
                            color: modelData.level === 0 ? Theme.ink3 : group.heatFor(modelData.level)
                            font.pixelSize: 12
                            font.weight: modelData.level === 0 ? Font.Normal : Font.DemiBold
                            font.features: Theme.tabular
                        }
                        FieldInput {
                            Layout.fillWidth: true
                            text: modelData.text
                            placeholder: modelData.placeholder
                            accessibleName: modelData.level === 0 ? S.tr2(S.rev, "titles.custom.idle.a11y")
                                                                  : Format.fill(S.tr2(S.rev, "titles.level"), [modelData.level])
                            onCommitted: (t) => themes.setText(modelData.level, t)
                            onFocusedChanged: livePreview.level = focused ? modelData.level : 13
                        }
                        IconButton {
                            opacity: modelData.text.length > 0 ? 1 : 0
                            enabled: modelData.text.length > 0
                            glyph: "reset"
                            label: S.tr2(S.rev, "titles.custom.reset")
                            onClicked: themes.setText(modelData.level, "")
                            Behavior on opacity { NumberAnimation { duration: 150 } }
                        }
                    }
                }
                Text {
                    visible: themes.notice.length > 0
                    text: themes.notice
                    color: Theme.amber
                    font.pixelSize: 11
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                    Accessible.role: Accessible.AlertMessage
                    Accessible.name: text
                }
                Text {
                    text: S.tr2(S.rev, "titles.custom.lengthHint")
                    color: Theme.ink3
                    font.pixelSize: 11
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                }
                RowLayout {
                    spacing: 14
                    LinkButton {
                        text: S.tr2(S.rev, "titles.custom.resetAll")
                        onClicked: themes.resetAll()
                    }
                    LinkButton {
                        text: S.tr2(S.rev, "titles.custom.duplicate")
                        enabled: themes.canCreate
                        onClicked: themes.createFrom(themes.selected)
                    }
                    Item { Layout.fillWidth: true }
                    LinkButton {
                        destructive: true
                        text: S.tr2(S.rev, "titles.custom.delete")
                        onClicked: themes.remove(themes.selected)
                    }
                }
            }
        }
    }
}
