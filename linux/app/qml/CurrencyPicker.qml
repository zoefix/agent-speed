import QtQuick
import QtQuick.Controls

// DESIGN 9: the currency picker — a button with `code · name` and a searchable list (common group, then the rest).
Item {
    id: picker
    property string value: "USD"
    signal activated(string code)
    implicitWidth: Math.max(180, label.implicitWidth + 44)
    implicitHeight: 28
    activeFocusOnTab: true
    Accessible.role: Accessible.ComboBox
    Accessible.name: label.text
    Keys.onSpacePressed: popup.open()

    property var all: []
    function reload() { all = catalog.currencies() }
    Component.onCompleted: reload()
    Connections {
        target: S
        function onChanged() { picker.reload() }
    }

    function fold(text) { return String(text).toLowerCase().normalize("NFKD").replace(/[̀-ͯ]/g, "") }
    function wordStarts(text, needle) {
        var t = fold(text)
        if (t.startsWith(needle)) return true
        for (var i = 1; i < t.length; ++i) {
            var prev = t.charAt(i - 1), ch = t.charCodeAt(i)
            var boundary = /[\s\-\(\)\.,$¥€£]/.test(prev) || (ch >= 0x3040 && ch <= 0x9FFF)
            if (boundary && t.substr(i, needle.length) === needle) return true
        }
        return false
    }
    function filtered(query) {
        var needle = fold(query.trim())
        if (needle.length === 0) return all
        var codes = [], names = []
        for (var i = 0; i < all.length; ++i) {
            var c = all[i]
            if (fold(c.code).startsWith(needle)) codes.push(c)
            else if (wordStarts(c.name, needle) || wordStarts(c.symbol, needle)) names.push(c)
        }
        return codes.concat(names)
    }

    Rectangle {
        anchors.fill: parent
        radius: 7
        color: area.pressed ? Theme.pressed : area.containsMouse ? Theme.hover : Theme.field
        border.width: picker.activeFocus ? 1.5 : 0
        border.color: Theme.accent
    }
    Text {
        id: label
        anchors.left: parent.left
        anchors.leftMargin: 11
        anchors.right: chevron.left
        anchors.rightMargin: 6
        anchors.verticalCenter: parent.verticalCenter
        text: S.rev >= 0 ? catalog.currencyLabel(picker.value) : ""
        color: Theme.ink
        font.pixelSize: 12
        elide: Text.ElideRight
    }
    Glyph {
        id: chevron
        anchors.right: parent.right
        anchors.rightMargin: 9
        anchors.verticalCenter: parent.verticalCenter
        width: 11
        height: 11
        name: "chevron"
        color: Theme.ink2
    }
    MouseArea {
        id: area
        anchors.fill: parent
        hoverEnabled: true
        cursorShape: Qt.PointingHandCursor
        onClicked: popup.opened ? popup.close() : popup.open()
    }

    Popup {
        id: popup
        y: picker.height + 4
        x: picker.width - width
        width: 320
        height: 400
        padding: 8
        focus: true
        onOpened: {
            search.text = ""
            search.forceActiveFocus()
            results.currentIndex = -1
        }
        background: Rectangle {
            radius: 12
            color: Theme.dark ? "#22252C" : "#FFFFFF"
            border.width: 1
            border.color: Theme.border
        }
        contentItem: Column {
            spacing: 8
            Rectangle {
                width: parent.width
                height: 32
                radius: 8
                color: Theme.field
                Glyph {
                    id: lens
                    x: 10
                    anchors.verticalCenter: parent.verticalCenter
                    width: 13
                    height: 13
                    name: "search"
                    color: Theme.ink3
                }
                TextInput {
                    id: search
                    anchors.left: lens.right
                    anchors.leftMargin: 8
                    anchors.right: parent.right
                    anchors.rightMargin: 10
                    anchors.verticalCenter: parent.verticalCenter
                    color: Theme.ink
                    font.pixelSize: 13
                    selectByMouse: true
                    clip: true
                    Text {
                        anchors.fill: parent
                        visible: search.text.length === 0
                        text: S.tr2(S.rev, "settings.currency.search")
                        color: Theme.ink3
                        font.pixelSize: 13
                    }
                    Keys.onDownPressed: results.currentIndex = Math.min(results.count - 1, results.currentIndex + 1)
                    Keys.onUpPressed: results.currentIndex = Math.max(0, results.currentIndex - 1)
                    Keys.onReturnPressed: {
                        var index = results.currentIndex >= 0 ? results.currentIndex : 0
                        if (results.count > 0) {
                            picker.activated(results.model[index].code)
                            popup.close()
                        }
                    }
                    Keys.onEscapePressed: popup.close()
                }
            }
            Text {
                visible: results.count === 0
                width: parent.width
                horizontalAlignment: Text.AlignHCenter
                topPadding: 40
                text: S.tr2(S.rev, "settings.currency.noResults")
                color: Theme.ink3
                font.pixelSize: 12
            }
            ListView {
                id: results
                width: parent.width
                height: parent.height - 40
                clip: true
                boundsBehavior: Flickable.StopAtBounds
                model: picker.filtered(search.text)
                section.property: search.text.length === 0 ? "common" : ""
                section.delegate: Text {
                    required property bool section
                    width: results.width
                    leftPadding: 8
                    topPadding: 8
                    bottomPadding: 4
                    text: section ? S.tr2(S.rev, "settings.currency.common") : S.tr2(S.rev, "settings.currency.all")
                    color: Theme.ink3
                    font.pixelSize: 11
                    font.weight: Font.DemiBold
                }
                ScrollBar.vertical: ScrollBar { policy: ScrollBar.AsNeeded }
                delegate: Item {
                    required property var modelData
                    required property int index
                    width: results.width
                    height: 30
                    Rectangle {
                        anchors.fill: parent
                        radius: 6
                        color: results.currentIndex === index || rowArea.containsMouse ? Theme.hover : "transparent"
                    }
                    Text {
                        id: code
                        x: 10
                        width: 40
                        anchors.verticalCenter: parent.verticalCenter
                        text: modelData.code
                        color: Theme.ink
                        font.pixelSize: 12
                        font.weight: Font.DemiBold
                        font.features: Theme.tabular
                    }
                    Text {
                        anchors.left: code.right
                        anchors.leftMargin: 6
                        anchors.right: symbol.left
                        anchors.rightMargin: 8
                        anchors.verticalCenter: parent.verticalCenter
                        text: modelData.name
                        color: Theme.ink
                        font.pixelSize: 12
                        elide: Text.ElideRight
                    }
                    Text {
                        id: symbol
                        anchors.right: tick.left
                        anchors.rightMargin: 6
                        anchors.verticalCenter: parent.verticalCenter
                        text: modelData.symbol
                        color: Theme.ink3
                        font.pixelSize: 12
                    }
                    Glyph {
                        id: tick
                        anchors.right: parent.right
                        anchors.rightMargin: 8
                        anchors.verticalCenter: parent.verticalCenter
                        width: 13
                        height: 13
                        name: "check"
                        color: Theme.accent
                        opacity: modelData.code === picker.value ? 1 : 0
                    }
                    MouseArea {
                        id: rowArea
                        anchors.fill: parent
                        hoverEnabled: true
                        cursorShape: Qt.PointingHandCursor
                        onClicked: {
                            picker.activated(modelData.code)
                            popup.close()
                        }
                    }
                }
            }
        }
    }
}
