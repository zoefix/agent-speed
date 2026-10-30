import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "Format.js" as Format
import "History.js" as H

// DESIGN 9 Unknown prices: every unpriced request of the window's view, grouped by source with its reason (METRICS
// 9.10), the requests the current table prices now, and the diagnostics to copy.
Popup {
    id: popup
    property string first: ""
    property string last: ""
    property string source: "all"
    property var excluded: []
    property string view: "30d"

    readonly property var report: opened && history.detailRevision >= 0 && catalog.revision >= 0 && S.rev >= 0
                                  ? history.unpricedView(first, last, source, excluded, view) : ({ groups: [], count: 0, pricedNow: 0, text: "" })
    readonly property string localeName: S.language === "en" ? "en_US" : S.language.replace("-", "_")

    width: 380
    padding: 16
    modal: false
    focus: true
    closePolicy: Popup.CloseOnEscape | Popup.CloseOnPressOutside
    background: Rectangle { radius: 12; color: Theme.cardFill; border.width: 1; border.color: Theme.cardLine }

    function shortDate(iso) { return iso.length > 0 ? H.parse(iso).toLocaleDateString(Qt.locale(localeName), Locale.ShortFormat) : "" }
    function seen(row) {
        var dates = row.first === row.last ? shortDate(row.first) : shortDate(row.first) + " – " + shortDate(row.last)
        return Format.fill(S.tr2(S.rev, "history.unpricedModels.seen"), [dates])
    }
    function details(row) {
        var parts = []
        if (row.provider.length > 0) parts.push(Format.fill(S.tr2(S.rev, "history.unpricedModels.via"), [row.provider]))
        if (row.fast) parts.push(S.tr2(S.rev, "speed.value.fast") + " ⚡")
        if (row.canonical.length > 0) parts.push(Format.fill(S.tr2(S.rev, "history.unpricedModels.lookup"), [row.canonical]))
        return parts.join(" · ")
    }

    property string copyState: ""
    Timer { id: copyReset; interval: 1600; onTriggered: popup.copyState = "" }
    function copy() {
        var ok = history.copyText(report.text)
        if (!ok) ok = history.copyText(report.text)
        copyState = ok ? "copied" : "failed"
        copyReset.restart()
    }

    contentItem: ColumnLayout {
        spacing: 0

        RowLayout {
            Layout.fillWidth: true
            Text { text: S.tr2(S.rev, "cost.unknown"); color: Theme.ink; font.pixelSize: 13; font.weight: Font.DemiBold }
            Item { Layout.fillWidth: true }
            Text { text: H.grouped(popup.report.count); color: Theme.ink2; font.pixelSize: 13; font.features: Theme.tabular }
        }

        Flickable {
            id: scroller
            Layout.fillWidth: true
            Layout.topMargin: 10
            Layout.preferredHeight: Math.min(groups.implicitHeight, 440 - 180)
            contentWidth: width
            contentHeight: groups.implicitHeight
            boundsBehavior: Flickable.StopAtBounds
            clip: true
            visible: popup.report.groups.length > 0
            ScrollBar.vertical: ScrollBar { policy: scroller.contentHeight > scroller.height ? ScrollBar.AsNeeded : ScrollBar.AlwaysOff }

            Column {
                id: groups
                width: scroller.width
                spacing: 12
                Repeater {
                    model: popup.report.groups
                    Column {
                        id: group
                        required property var modelData
                        width: groups.width
                        spacing: 7
                        RowLayout {
                            width: parent.width
                            spacing: 7
                            Rectangle { width: 7; height: 7; radius: 3.5; color: Theme.sourceColor(group.modelData.source) }
                            Text {
                                Layout.fillWidth: true
                                text: S.tr2(S.rev, "source." + group.modelData.source)
                                color: Theme.ink
                                font.pixelSize: 12
                                font.weight: Font.DemiBold
                                elide: Text.ElideRight
                            }
                            Text { text: H.grouped(group.modelData.requests); color: Theme.ink2; font.pixelSize: 12; font.features: Theme.tabular }
                        }
                        Repeater {
                            model: group.modelData.rows
                            Column {
                                id: entry
                                required property var modelData
                                x: 14
                                width: group.width - 14
                                spacing: 2
                                RowLayout {
                                    width: parent.width
                                    spacing: 8
                                    TextEdit {
                                        Layout.fillWidth: true
                                        readOnly: true
                                        selectByMouse: true
                                        clip: true
                                        text: entry.modelData.model.length > 0 ? entry.modelData.model : S.tr2(S.rev, "cost.noModelName")
                                        color: entry.modelData.model.length > 0 ? Theme.ink : Theme.ink2
                                        selectionColor: Qt.rgba(Theme.accent.r, Theme.accent.g, Theme.accent.b, 0.3)
                                        font.pixelSize: 12
                                        font.weight: Font.Medium
                                        font.italic: entry.modelData.model.length === 0
                                    }
                                    Text { text: H.grouped(entry.modelData.requests); color: Theme.ink2; font.pixelSize: 12; font.features: Theme.tabular }
                                }
                                RowLayout {
                                    width: parent.width
                                    spacing: 6
                                    // The reason chip: the same neutral look for every reason.
                                    Rectangle {
                                        implicitWidth: chip.implicitWidth + 10
                                        implicitHeight: chip.implicitHeight + 2
                                        radius: height / 2
                                        color: Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, 0.09)
                                        border.width: 0.5
                                        border.color: Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, 0.16)
                                        Text {
                                            id: chip
                                            anchors.centerIn: parent
                                            text: S.tr2(S.rev, "cost.reason." + entry.modelData.reason)
                                            color: Theme.ink2
                                            font.pixelSize: 10
                                            font.weight: Font.Medium
                                        }
                                        HoverHandler { id: chipHover }
                                        ToolTip.visible: chipHover.hovered
                                        ToolTip.delay: 400
                                        ToolTip.text: S.tr2(S.rev, "cost.reason." + entry.modelData.reason + ".hint")
                                                      + (entry.modelData.reason === "unpriceable" && entry.modelData.rule.length > 0 ? "\n" + entry.modelData.rule : "")
                                    }
                                    Text {
                                        id: facts
                                        Layout.fillWidth: true
                                        text: popup.details(entry.modelData)
                                        color: Theme.ink2
                                        font.pixelSize: 11
                                        elide: Text.ElideRight
                                        HoverHandler { id: factsHover }
                                        ToolTip.visible: factsHover.hovered
                                        ToolTip.delay: 400
                                        ToolTip.text: [S.tr2(S.rev, "cost.reason." + entry.modelData.reason), facts.text, popup.seen(entry.modelData)]
                                                      .filter(function(t) { return t.length > 0 }).join(" · ")
                                    }
                                }
                            }
                        }
                        Text {
                            visible: group.modelData.more > 0
                            x: 14
                            text: Format.fill(S.tr2(S.rev, "history.unpricedModels.more"), [group.modelData.more, H.grouped(group.modelData.moreRequests)])
                            color: Theme.ink3
                            font.pixelSize: 11
                        }
                    }
                }
            }
        }

        Text {
            visible: popup.report.pricedNow > 0
            Layout.fillWidth: true
            Layout.topMargin: 10
            text: Format.fill(S.tr2(S.rev, "history.unpricedModels.pricedNow"), [H.grouped(popup.report.pricedNow)])
            color: Theme.ink2
            font.pixelSize: 11
            wrapMode: Text.WordWrap
        }

        Rectangle { Layout.fillWidth: true; Layout.topMargin: 12; Layout.preferredHeight: 1; color: Theme.hairline }
        Text {
            Layout.fillWidth: true
            Layout.topMargin: 10
            text: S.tr2(S.rev, "history.unpricedModels.hint")
            color: Theme.ink2
            font.pixelSize: 11
            wrapMode: Text.WordWrap
        }
        RowLayout {
            Layout.fillWidth: true
            Layout.topMargin: 10
            spacing: 10
            // Keeps its width through the copied / failed states so nothing shifts.
            Item {
                id: copyButton
                Layout.alignment: Qt.AlignTop
                implicitWidth: Math.max(copyIdle.implicitWidth, copyDone.implicitWidth, copyFail.implicitWidth) + 22
                implicitHeight: 26
                activeFocusOnTab: true
                Accessible.role: Accessible.Button
                Accessible.name: S.tr2(S.rev, "history.unpricedModels.copy")
                Accessible.onPressAction: popup.copy()
                Keys.onSpacePressed: popup.copy()
                Keys.onReturnPressed: popup.copy()
                Rectangle {
                    anchors.fill: parent
                    radius: 6
                    color: copyArea.pressed ? Theme.pressed : copyArea.containsMouse ? Theme.hover : "transparent"
                    border.width: copyButton.activeFocus ? 1.5 : 1
                    border.color: copyButton.activeFocus ? Theme.accent : Theme.cardLine
                }
                Row {
                    id: copyIdle
                    anchors.centerIn: parent
                    spacing: 5
                    opacity: popup.copyState === "" ? 1 : 0
                    Behavior on opacity { NumberAnimation { duration: 150 } }
                    Glyph { name: "copy"; width: 12; height: 12; color: Theme.ink2; anchors.verticalCenter: parent.verticalCenter }
                    Text { text: S.tr2(S.rev, "history.unpricedModels.copy"); color: Theme.ink; font.pixelSize: 12 }
                }
                Row {
                    id: copyDone
                    anchors.centerIn: parent
                    spacing: 5
                    opacity: popup.copyState === "copied" ? 1 : 0
                    Behavior on opacity { NumberAnimation { duration: 150 } }
                    Glyph { name: "check"; width: 12; height: 12; color: Theme.good; anchors.verticalCenter: parent.verticalCenter }
                    Text { text: S.tr2(S.rev, "history.unpricedModels.copied"); color: Theme.ink; font.pixelSize: 12 }
                }
                Row {
                    id: copyFail
                    anchors.centerIn: parent
                    spacing: 5
                    opacity: popup.copyState === "failed" ? 1 : 0
                    Behavior on opacity { NumberAnimation { duration: 150 } }
                    Glyph { name: "alertCircle"; width: 12; height: 12; color: Theme.amber; anchors.verticalCenter: parent.verticalCenter }
                    Text { text: S.tr2(S.rev, "history.unpricedModels.copyFailed"); color: Theme.ink; font.pixelSize: 12 }
                }
                MouseArea {
                    id: copyArea
                    anchors.fill: parent
                    hoverEnabled: true
                    cursorShape: Qt.PointingHandCursor
                    onClicked: popup.copy()
                }
            }
            Text {
                Layout.fillWidth: true
                text: S.tr2(S.rev, "history.unpricedModels.copyHint")
                color: Theme.ink3
                font.pixelSize: 11
                wrapMode: Text.WordWrap
            }
        }
    }
}
