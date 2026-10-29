import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// DESIGN 2c Interaction: Settings → 懸浮窗 gains the boards list (style, sources, visible switch, add / remove) and an
// editor for the selected board: style, size, sources, the live metrics switches, opacity and the window toggles.
ColumnLayout {
    id: group
    Layout.fillWidth: true
    spacing: 10
    property string selected: ""
    readonly property var current: {
        var list = boardStore.boards
        for (var i = 0; i < list.length; ++i) if (list[i].id === selected) return list[i]
        return list.length > 0 ? list[0] : null
    }
    readonly property var sourceIds: ["codex", "claudeCode", "claudeDesktop", "deepseek", "opencode", "gemini", "omp", "antigravity",
                                      "workbuddy", "workbuddyAi", "zcode", "kimi", "grokBuild", "pi", "openclaw", "hermes", "droid"]

    SettingsGroup {
        title: S.tr2(S.rev, "boards.title")
        Repeater {
            model: boardStore.boards
            SettingsRow {
                required property var modelData
                required property int index
                label: S.tr2(S.rev, "boards.style." + modelData.style) + " · " + modelData.size
                caption: modelData.sources === "all" ? S.tr2(S.rev, "orbs.sources.all")
                         : (modelData.sources || []).map(s => S.tr2(S.rev, "source." + s)).join(", ")
                Rectangle {
                    width: 8
                    height: 8
                    radius: 4
                    anchors.verticalCenter: parent.verticalCenter
                    color: Theme.accent
                    visible: group.current && group.current.id === modelData.id
                }
                LinkButton {
                    text: S.tr2(S.rev, "settings.orb")
                    onClicked: group.selected = modelData.id
                }
                ToggleSwitch {
                    label: S.tr2(S.rev, "boards.show")
                    checked: modelData.visible
                    onToggled: (on) => boardStore.set(modelData.id, "visible", on)
                }
            }
        }
        Item {
            Layout.fillWidth: true
            implicitHeight: 44
            RowLayout {
                anchors.left: parent.left
                anchors.leftMargin: 14
                anchors.verticalCenter: parent.verticalCenter
                spacing: 8
                PillButton {
                    text: S.tr2(S.rev, "boards.add")
                    enabled: boardStore.canAdd
                    onClicked: group.selected = boardStore.add()
                }
                PillButton {
                    visible: group.current !== null
                    text: S.tr2(S.rev, "boards.remove")
                    onClicked: confirm.open()
                }
                Text {
                    visible: !boardStore.canAdd
                    text: S.tr2(S.rev, "boards.limit")
                    color: Theme.ink3
                    font.pixelSize: 11
                }
            }
        }
    }

    Dialog {
        id: confirm
        anchors.centerIn: Overlay.overlay
        modal: true
        title: S.tr2(S.rev, "boards.remove.confirm")
        standardButtons: Dialog.Ok | Dialog.Cancel
        onAccepted: if (group.current) boardStore.remove(group.current.id)
    }

    // The editor of the selected board.
    SettingsGroup {
        visible: group.current !== null
        title: S.tr2(S.rev, "boards.settings")
        SettingsRow {
            label: S.tr2(S.rev, "boards.style")
            Segmented {
                value: group.current ? group.current.style : "neon"
                options: [
                    { value: "glass", label: S.tr2(S.rev, "boards.style.glass") },
                    { value: "timing", label: S.tr2(S.rev, "boards.style.timing") },
                    { value: "neon", label: S.tr2(S.rev, "boards.style.neon") }
                ]
                onActivated: (v) => boardStore.set(group.current.id, "style", v)
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "menu.size")
            Segmented {
                value: group.current ? group.current.size : "M"
                options: [{ value: "S", label: "S" }, { value: "M", label: "M" }, { value: "L", label: "L" }]
                onActivated: (v) => boardStore.set(group.current.id, "size", v)
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "orbs.sources")
            caption: group.current && group.current.sources !== "all" ? "" : S.tr2(S.rev, "orbs.sources.all")
            ToggleSwitch {
                label: S.tr2(S.rev, "orbs.sources.all")
                checked: group.current ? group.current.sources === "all" : true
                onToggled: (on) => boardStore.setSources(group.current.id, on ? "all" : ["codex", "claudeCode"])
            }
        }
        Flow {
            visible: group.current && group.current.sources !== "all"
            Layout.fillWidth: true
            Layout.leftMargin: 14
            Layout.rightMargin: 14
            Layout.bottomMargin: 10
            spacing: 6
            Repeater {
                model: group.sourceIds
                Rectangle {
                    required property string modelData
                    readonly property bool on: group.current && Array.isArray(group.current.sources) && group.current.sources.indexOf(modelData) >= 0
                    radius: 11
                    height: 22
                    width: chipLabel.implicitWidth + 18
                    color: on ? Qt.rgba(Theme.accent.r, Theme.accent.g, Theme.accent.b, 0.16) : Theme.field
                    border.width: on ? 1 : 0
                    border.color: Theme.accent
                    Text {
                        id: chipLabel
                        anchors.centerIn: parent
                        text: S.tr2(S.rev, "source." + parent.modelData)
                        color: parent.on ? Theme.accent : Theme.ink2
                        font.pixelSize: 11
                    }
                    MouseArea {
                        anchors.fill: parent
                        cursorShape: Qt.PointingHandCursor
                        onClicked: {
                            var list = Array.isArray(group.current.sources) ? group.current.sources.slice() : []
                            var at = list.indexOf(parent.modelData)
                            if (at >= 0) list.splice(at, 1); else list.push(parent.modelData)
                            if (list.length > 0) boardStore.setSources(group.current.id, list)
                        }
                    }
                }
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "boards.readouts")
            caption: S.tr2(S.rev, "boards.readouts.hint")
            Row {
                spacing: 10
                Repeater {
                    model: [{ key: "intelligence", label: "boards.readouts.intel" }, { key: "thinking", label: "boards.readouts.thinking" },
                            { key: "cost", label: "boards.readouts.cost" }]
                    CheckBox {
                        required property var modelData
                        text: S.tr2(S.rev, modelData.label)
                        checked: group.current && group.current.readouts ? group.current.readouts[modelData.key] !== false : true
                        onToggled: boardStore.setReadout(group.current.id, modelData.key, checked)
                    }
                }
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "settings.orb.opacity")
            Slider {
                width: 160
                from: 0.3
                to: 1
                value: group.current ? group.current.opacity : 1
                onMoved: boardStore.set(group.current.id, "opacity", value)
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "menu.alwaysOnTop")
            ToggleSwitch {
                label: S.tr2(S.rev, "menu.alwaysOnTop")
                checked: group.current ? group.current.alwaysOnTop : true
                onToggled: (on) => boardStore.set(group.current.id, "alwaysOnTop", on)
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "menu.clickThrough")
            divider: false
            ToggleSwitch {
                label: S.tr2(S.rev, "menu.clickThrough")
                checked: group.current ? group.current.clickThrough : false
                onToggled: (on) => boardStore.set(group.current.id, "clickThrough", on)
            }
        }
    }
}
