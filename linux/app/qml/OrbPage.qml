import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import AgentSpeed.Orb

// DESIGN 5 Floating orb pane: the master group, the orb list with live thumbnails, and the editor of the selected orb
// (sources, style, unit, size, opacity, name plate, window toggles, readout, advanced), then the boards.
SettingsPage {
    id: page
    title: S.tr2(S.rev, "settings.orb")
    readonly property var current: {
        const list = orbs.list
        for (let i = 0; i < list.length; ++i) if (list[i].id === orbs.selected) return list[i]
        return list.length > 0 ? list[0] : null
    }
    function set(key, value) { if (current) orbs.set(current.id, key, value) }

    // 1. Master group.
    SettingsGroup {
        SettingsRow {
            label: S.tr2(S.rev, "settings.orb.show")
            caption: S.tr2(S.rev, "orbs.allNote").replace("%@", S.tr2(S.rev, "settings.taskbar"))
            ToggleSwitch {
                label: S.tr2(S.rev, "settings.orb.show")
                checked: orbs.shown
                onToggled: (on) => orbs.shown = on
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "orbs.names")
            caption: S.tr2(S.rev, "orbs.names.hint")
            divider: false
            Dropdown {
                value: orbs.names
                popupWidth: 160
                options: [
                    { value: "auto", label: S.tr2(S.rev, "orbs.names.auto") },
                    { value: "always", label: S.tr2(S.rev, "orbs.names.always") },
                    { value: "never", label: S.tr2(S.rev, "orbs.names.never") }
                ]
                onActivated: (v) => orbs.names = v
            }
        }
    }

    // Undo banner after split, merge or remove (8 s).
    Rectangle {
        Layout.fillWidth: true
        visible: orbs.banner.length > 0
        implicitHeight: 38
        radius: 10
        color: Qt.rgba(Theme.accent.r, Theme.accent.g, Theme.accent.b, Theme.dark ? 0.16 : 0.09)
        Text {
            anchors.left: parent.left
            anchors.leftMargin: 14
            anchors.verticalCenter: parent.verticalCenter
            text: orbs.banner
            color: Theme.ink
            font.pixelSize: 13
        }
        LinkButton {
            anchors.right: parent.right
            anchors.rightMargin: 14
            anchors.verticalCenter: parent.verticalCenter
            text: S.tr2(S.rev, "orbs.undo")
            onClicked: orbs.undo()
        }
    }

    // 2. Orb list.
    ColumnLayout {
        Layout.fillWidth: true
        spacing: 6
        RowLayout {
            Layout.fillWidth: true
            Layout.leftMargin: 4
            Layout.rightMargin: 4
            Text {
                text: S.tr2(S.rev, "settings.orb")
                color: Theme.ink2
                font.pixelSize: 12
                font.weight: Font.DemiBold
                Layout.fillWidth: true
            }
            Text {
                text: S.tr2(S.rev, "orbs.count").replace("%d", orbs.count).replace("%d", orbs.maximum)
                color: Theme.ink3
                font.pixelSize: 12
                font.features: Theme.tabular
            }
        }
        Rectangle {
            Layout.fillWidth: true
            implicitHeight: listColumn.implicitHeight
            radius: 10
            color: Theme.card
            border.width: 1
            border.color: Theme.border
            ColumnLayout {
                id: listColumn
                width: parent.width
                spacing: 0
                Repeater {
                    model: orbs.list
                    delegate: Item {
                        id: row
                        required property var modelData
                        required property int index
                        readonly property bool current: page.current && page.current.id === modelData.id
                        readonly property var live: orbs.values[modelData.id] || ({ value: 0, valueText: "0", unavailable: false })
                        Layout.fillWidth: true
                        implicitHeight: 52
                        opacity: modelData.visible ? 1 : 0.5
                        activeFocusOnTab: true
                        Accessible.role: Accessible.ListItem
                        Accessible.name: modelData.name
                        Accessible.selected: current
                        onActiveFocusChanged: if (activeFocus) { orbs.selected = modelData.id; orbs.highlight(modelData.id, true) } else orbs.highlight(modelData.id, false)
                        Keys.onUpPressed: if (index > 0) orbs.selected = orbs.list[index - 1].id
                        Keys.onDownPressed: if (index < orbs.list.length - 1) orbs.selected = orbs.list[index + 1].id
                        Keys.onSpacePressed: orbs.set(modelData.id, "visible", !modelData.visible)
                        Keys.onDeletePressed: orbs.remove(modelData.id)
                        Keys.onPressed: (event) => {
                            if (event.key === Qt.Key_D && (event.modifiers & Qt.ControlModifier)) { orbs.duplicate(modelData.id); event.accepted = true }
                        }

                        Rectangle {
                            anchors.fill: parent
                            anchors.margins: 4
                            radius: 7
                            color: row.current ? Qt.rgba(Theme.accent.r, Theme.accent.g, Theme.accent.b, Theme.dark ? 0.18 : 0.10)
                                               : rowHover.hovered ? Theme.hover : "transparent"
                            Behavior on color { ColorAnimation { duration: 120 } }
                        }
                        OrbPreview {
                            id: thumb
                            x: 10
                            anchors.verticalCenter: parent.verticalCenter
                            width: 44
                            height: 44
                            diameter: 38
                            style: row.modelData.style
                            dark: Theme.dark
                            fps: 15
                            liveValue: row.live.unavailable ? 0 : row.live.value
                            valueText: row.live.unavailable ? "–" : row.live.valueText
                            redline: row.modelData.redline
                        }
                        ColumnLayout {
                            anchors.left: thumb.right
                            anchors.leftMargin: 10
                            anchors.right: trailing.left
                            anchors.rightMargin: 10
                            anchors.verticalCenter: parent.verticalCenter
                            spacing: 1
                            Text {
                                Layout.fillWidth: true
                                text: row.modelData.name
                                color: Theme.ink
                                font.pixelSize: 13
                                elide: Text.ElideRight
                            }
                            Text {
                                Layout.fillWidth: true
                                text: row.modelData.detail
                                color: Theme.ink3
                                font.pixelSize: 11
                                elide: Text.ElideRight
                            }
                        }
                        Row {
                            id: trailing
                            anchors.right: parent.right
                            anchors.rightMargin: 10
                            anchors.verticalCenter: parent.verticalCenter
                            spacing: 6
                            Text {
                                anchors.verticalCenter: parent.verticalCenter
                                text: !row.modelData.visible ? S.tr2(S.rev, "orbs.hidden")
                                      : row.live.unavailable ? "–" : row.live.valueText + " " + S.tr2(S.rev, "unit.tokS")
                                color: Theme.ink3
                                font.pixelSize: 12
                                font.features: Theme.tabular
                            }
                            IconButton {
                                anchors.verticalCenter: parent.verticalCenter
                                glyph: row.modelData.visible ? "eye" : "eyeOff"
                                label: S.tr2(S.rev, "orbs.visible")
                                onClicked: orbs.set(row.modelData.id, "visible", !row.modelData.visible)
                            }
                        }
                        Rectangle {
                            visible: row.index < orbs.list.length - 1
                            anchors.left: parent.left
                            anchors.leftMargin: 62
                            anchors.right: parent.right
                            anchors.bottom: parent.bottom
                            height: 1
                            color: Theme.hairline
                        }
                        HoverHandler {
                            id: rowHover
                            onHoveredChanged: orbs.highlight(row.modelData.id, hovered)
                        }
                        TapHandler {
                            onTapped: {
                                orbs.selected = row.modelData.id
                                row.forceActiveFocus()
                            }
                        }
                    }
                }
                // Footer: add, remove, duplicate; split and merge.
                Rectangle {
                    Layout.fillWidth: true
                    implicitHeight: 1
                    color: Theme.hairline
                }
                RowLayout {
                    Layout.fillWidth: true
                    Layout.margins: 6
                    spacing: 2
                    IconButton {
                        glyph: "plus"
                        label: orbs.count >= orbs.maximum ? S.tr2(S.rev, "orbs.limit").replace("%d", orbs.maximum) : S.tr2(S.rev, "orbs.add")
                        enabled: orbs.count < orbs.maximum
                        opacity: enabled ? 1 : 0.4
                        onClicked: orbs.add(page.current ? page.current.id : "")
                        ToolTip.visible: hovered
                        ToolTip.text: label
                    }
                    IconButton {
                        glyph: "minus"
                        label: S.tr2(S.rev, "orbs.remove")
                        enabled: orbs.count > 1
                        opacity: enabled ? 1 : 0.4
                        onClicked: if (page.current) orbs.remove(page.current.id)
                        ToolTip.visible: hovered
                        ToolTip.text: label
                    }
                    IconButton {
                        glyph: "copy"
                        label: S.tr2(S.rev, "orbs.duplicate")
                        enabled: orbs.count < orbs.maximum
                        opacity: enabled ? 1 : 0.4
                        onClicked: if (page.current) orbs.duplicate(page.current.id)
                        ToolTip.visible: hovered
                        ToolTip.text: label
                    }
                    Item { Layout.fillWidth: true }
                    LinkButton {
                        text: S.tr2(S.rev, "orbs.split")
                        enabled: orbs.canSplitAll
                        opacity: enabled ? 1 : 0.4
                        onClicked: orbs.splitAll()
                        HoverHandler { id: splitHover }
                        ToolTip.visible: splitHover.hovered
                        ToolTip.text: orbs.canSplitAll ? S.tr2(S.rev, "orbs.split.hint") : S.tr2(S.rev, "orbs.split.unavailable")
                    }
                    LinkButton {
                        text: S.tr2(S.rev, "orbs.merge")
                        enabled: orbs.count > 1
                        opacity: enabled ? 1 : 0.4
                        onClicked: orbs.merge(page.current ? page.current.id : "")
                        HoverHandler { id: mergeHover }
                        ToolTip.visible: mergeHover.hovered
                        ToolTip.text: S.tr2(S.rev, "orbs.merge.hint")
                    }
                }
            }
        }
    }

    // 3. Editor of the selected orb, headed by its colour dots and name.
    RowLayout {
        Layout.fillWidth: true
        Layout.topMargin: 6
        Layout.leftMargin: 4
        spacing: 8
        visible: page.current !== null
        Row {
            spacing: -3
            Repeater {
                model: page.current ? page.current.colors.slice(0, 3) : []
                delegate: Rectangle {
                    required property var modelData
                    width: 10
                    height: 10
                    radius: 5
                    color: modelData
                    border.width: 1.5
                    border.color: Theme.panel
                }
            }
        }
        Text {
            Layout.fillWidth: true
            text: page.current ? page.current.name : ""
            color: Theme.ink
            font.pixelSize: 17
            font.weight: Font.DemiBold
            elide: Text.ElideRight
        }
    }

    // Sources.
    SettingsGroup {
        visible: page.current !== null
        title: S.tr2(S.rev, "orbs.sources")
        SettingsRow {
            label: S.tr2(S.rev, "orbs.sources.all")
            caption: S.tr2(S.rev, "orbs.sources.allHint")
            CheckBox {
                checked: page.current ? page.current.all : true
                onToggled: page.set("allSources", checked)
                Accessible.name: S.tr2(S.rev, "orbs.sources.all")
            }
        }
        Repeater {
            model: orbs.sources
            delegate: SettingsRow {
                id: sourceRow
                required property var modelData
                required property int index
                readonly property bool tracked: page.current && (page.current.all || page.current.sources.indexOf(modelData.key) >= 0)
                readonly property bool last: page.current && !page.current.all && page.current.sources.length === 1 && tracked
                label: modelData.name
                divider: index < orbs.sources.length - 1
                Text {
                    anchors.verticalCenter: parent.verticalCenter
                    visible: sourceRow.modelData.status.length > 0
                    text: sourceRow.modelData.status
                    color: Theme.ink3
                    font.pixelSize: 11
                }
                Rectangle {
                    anchors.verticalCenter: parent.verticalCenter
                    width: 8
                    height: 8
                    radius: 4
                    color: sourceRow.modelData.color
                }
                CheckBox {
                    anchors.verticalCenter: parent.verticalCenter
                    checked: sourceRow.tracked
                    enabled: page.current && !page.current.all && !sourceRow.last
                    onToggled: orbs.setSource(page.current.id, sourceRow.modelData.key, checked)
                    Accessible.name: sourceRow.modelData.name
                    ToolTip.visible: sourceRow.last && hovered
                    ToolTip.text: S.tr2(S.rev, "orbs.sources.lastOne")
                }
            }
        }
    }

    // Style and unit.
    SettingsGroup {
        visible: page.current !== null
        title: S.tr2(S.rev, "orbs.style")
        Item {
            Layout.fillWidth: true
            implicitHeight: stylePicker.implicitHeight + 28
            OrbStylePicker {
                id: stylePicker
                x: 14
                y: 14
                width: parent.width - 28
                selected: page.current ? page.current.style : "led"
                onPicked: (key) => page.set("style", key)
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "orbs.unit")
            divider: false
            Segmented {
                value: page.current ? page.current.unit : "tokens"
                options: [
                    { value: "tokens", label: "tok/s" },
                    { value: "cost", label: S.tr2(S.rev, "cost.unitPerSecond").replace("%@", (page.current && page.current.costSymbol) || "US$") }
                ]
                onActivated: (v) => page.set("unit", v)
            }
        }
    }

    // Size, opacity, name plate and window toggles.
    SettingsGroup {
        visible: page.current !== null
        SettingsRow {
            label: S.tr2(S.rev, "settings.orb.size")
            Segmented {
                anchors.verticalCenter: parent.verticalCenter
                value: !page.current ? -1 : Math.abs(page.current.size - 112) < 0.5 ? 112 : Math.abs(page.current.size - 148) < 0.5 ? 148
                     : Math.abs(page.current.size - 196) < 0.5 ? 196 : -1
                options: [
                    { value: 112, label: S.tr2(S.rev, "menu.size.small") },
                    { value: 148, label: S.tr2(S.rev, "menu.size.medium") },
                    { value: 196, label: S.tr2(S.rev, "menu.size.large") }
                ]
                onActivated: (v) => page.set("size", v)
            }
            Slider {
                anchors.verticalCenter: parent.verticalCenter
                width: 140
                from: page.current ? page.current.minimumSize : 96
                to: 240
                stepSize: 4
                snapMode: Slider.SnapAlways
                value: page.current ? page.current.size : 148
                onMoved: page.set("size", value)
                Accessible.name: S.tr2(S.rev, "settings.orb.size")
            }
            Text {
                anchors.verticalCenter: parent.verticalCenter
                width: 46
                horizontalAlignment: Text.AlignRight
                text: (page.current ? Math.round(page.current.size) : 148) + " pt"
                color: Theme.ink2
                font.pixelSize: 12
                font.features: Theme.tabular
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "settings.orb.opacity")
            Slider {
                anchors.verticalCenter: parent.verticalCenter
                width: 160
                from: 0.4
                to: 1
                value: page.current ? page.current.opacity : 1
                onMoved: page.set("opacity", value)
                Accessible.name: S.tr2(S.rev, "settings.orb.opacity")
            }
            Text {
                anchors.verticalCenter: parent.verticalCenter
                width: 40
                horizontalAlignment: Text.AlignRight
                text: Math.round((page.current ? page.current.opacity : 1) * 100) + "%"
                color: Theme.ink2
                font.pixelSize: 12
                font.features: Theme.tabular
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "orbs.showName")
            Dropdown {
                popupWidth: 220
                value: !page.current || page.current.showName === undefined || page.current.showName === null ? "auto"
                       : page.current.showName ? "on" : "off"
                options: [
                    { value: "auto", label: S.tr2(S.rev, "orbs.showName.auto").replace("%@", page.current && page.current.plate
                                                                                       ? S.tr2(S.rev, "orbs.showName.on") : S.tr2(S.rev, "orbs.showName.off")) },
                    { value: "on", label: S.tr2(S.rev, "orbs.showName.on") },
                    { value: "off", label: S.tr2(S.rev, "orbs.showName.off") }
                ]
                onActivated: (v) => page.set("showName", v === "auto" ? null : v === "on")
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "orbs.traffic")
            caption: page.current && page.current.traffic === "intelligence" ? S.tr2(S.rev, "orbs.traffic.hint.intel") : S.tr2(S.rev, "orbs.traffic.hint")
            Dropdown {
                value: page.current ? page.current.traffic : "sources"
                popupWidth: 200
                options: [
                    { value: "sources", label: S.tr2(S.rev, "orbs.traffic.sources") },
                    { value: "machine", label: S.tr2(S.rev, "orbs.traffic.machine") },
                    { value: "intelligence", label: S.tr2(S.rev, "orbs.traffic.intelligence") },
                    { value: "off", label: S.tr2(S.rev, "orbs.traffic.off") }
                ]
                onActivated: (v) => page.set("traffic", v)
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "menu.alwaysOnTop")
            ToggleSwitch {
                label: S.tr2(S.rev, "menu.alwaysOnTop")
                checked: page.current ? page.current.alwaysOnTop : true
                onToggled: (on) => page.set("alwaysOnTop", on)
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "menu.clickThrough")
            ToggleSwitch {
                label: S.tr2(S.rev, "menu.clickThrough")
                checked: page.current ? page.current.clickThrough : false
                onToggled: (on) => page.set("clickThrough", on)
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "settings.orb.fadeIdle")
            ToggleSwitch {
                label: S.tr2(S.rev, "settings.orb.fadeIdle")
                checked: page.current ? page.current.fadeWhenIdle : false
                onToggled: (on) => page.set("fadeWhenIdle", on)
            }
        }
        SettingsRow {
            label: ""
            divider: false
            PillButton {
                text: S.tr2(S.rev, "settings.taskbar.position.reset")
                onClicked: if (page.current) orbs.resetPosition(page.current.id)
            }
        }
    }

    // Advanced: name, title theme, redline.
    ColumnLayout {
        id: advanced
        visible: page.current !== null
        Layout.fillWidth: true
        spacing: 6
        property bool open: false
        RowLayout {
            Layout.leftMargin: 4
            spacing: 6
            Glyph {
                name: "chevron"
                width: 12
                height: 12
                rotation: advanced.open ? 90 : 0
                Behavior on rotation { NumberAnimation { duration: 150 } }
            }
            Text {
                text: S.tr2(S.rev, "orbs.advanced")
                color: Theme.ink2
                font.pixelSize: 12
                font.weight: Font.DemiBold
            }
            Text {
                visible: !advanced.open
                text: [S.tr2(S.rev, "orbs.name"), S.tr2(S.rev, "titles.theme"), S.tr2(S.rev, "settings.redline")].join(S.tr2(S.rev, "list.separator"))
                color: Theme.ink3
                font.pixelSize: 12
            }
            TapHandler { onTapped: advanced.open = !advanced.open }
            HoverHandler { cursorShape: Qt.PointingHandCursor }
        }
        SettingsGroup {
            visible: advanced.open
            SettingsRow {
                label: S.tr2(S.rev, "orbs.name")
                FieldInput {
                    width: 220
                    text: page.current ? page.current.customName : ""
                    placeholder: S.tr2(S.rev, "orbs.name.auto").replace("%@", page.current ? page.current.autoName : "")
                    accessibleName: S.tr2(S.rev, "orbs.name")
                    onCommitted: (value) => page.set("name", value)
                }
            }
            SettingsRow {
                label: S.tr2(S.rev, "titles.theme")
                caption: S.tr2(S.rev, "orbs.titleTheme.hint")
                Dropdown {
                    popupWidth: 240
                    value: page.current ? page.current.titleTheme : ""
                    options: {
                        S.rev
                        themes.rev
                        let general = ""
                        const presets = themes.presets, customs = themes.customSets
                        for (let i = 0; i < presets.length; ++i) if (presets[i].id === themes.selected) general = presets[i].name
                        for (let j = 0; j < customs.length; ++j) if (customs[j].id === themes.selected) general = customs[j].name
                        const out = [{ value: "", label: S.tr2(S.rev, "orbs.titleTheme.global").replace("%@", general) }]
                        for (let k = 0; k < presets.length; ++k) out.push({ value: presets[k].id, label: presets[k].name })
                        for (let m = 0; m < customs.length; ++m) out.push({ value: customs[m].id, label: customs[m].name })
                        return out
                    }
                    onActivated: (v) => page.set("titleTheme", v)
                }
            }
            SettingsRow {
                label: S.tr2(S.rev, "settings.redline")
                caption: S.tr2(S.rev, "orbs.redline.hint")
                divider: page.current && page.current.customRedline
                Segmented {
                    value: page.current && page.current.customRedline ? "custom" : "global"
                    options: [
                        { value: "global", label: S.tr2(S.rev, "orbs.redline.global") },
                        { value: "custom", label: S.tr2(S.rev, "orbs.redline.custom") }
                    ]
                    onActivated: (v) => page.set("redline", v === "custom" ? prefs.redline : null)
                }
            }
            Item {
                visible: page.current && page.current.customRedline
                Layout.fillWidth: true
                implicitHeight: 56
                RedlineSlider {
                    x: 14
                    width: parent.width - 28
                    anchors.verticalCenter: parent.verticalCenter
                    value: page.current ? page.current.redline : 2000
                    onMoved: (v) => page.set("redline", v)
                }
            }
        }
    }

    BoardsGroup {}
}
