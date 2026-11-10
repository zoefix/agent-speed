import QtQuick

// A one-line text that crossfades to a new value (0.2 s) instead of jumping (DESIGN 4 Quota lines, Motion).
Item {
    id: fade
    property string text
    property color color: Theme.ink
    property int pixelSize: 11
    property int weight: Font.Normal
    property bool tabular: false
    property int horizontalAlignment: Text.AlignLeft
    property int elide: Text.ElideRight
    implicitWidth: Math.max(front.implicitWidth, back.opacity > 0 ? back.implicitWidth : 0)
    implicitHeight: front.implicitHeight

    property bool flip: false
    onTextChanged: {
        if (!fade.visible) {
            (flip ? back : front).text = text
            return
        }
        flip = !flip
        const incoming = flip ? back : front
        const outgoing = flip ? front : back
        incoming.text = text
        incoming.opacity = 1
        outgoing.opacity = 0
    }

    component Face: Text {
        textFormat: Text.PlainText
        width: fade.width
        anchors.verticalCenter: parent.verticalCenter
        color: fade.color
        font.pixelSize: fade.pixelSize
        font.weight: fade.weight
        font.features: fade.tabular ? Theme.tabular : ({})
        horizontalAlignment: fade.horizontalAlignment
        elide: fade.elide
        maximumLineCount: 1
        Behavior on opacity { NumberAnimation { duration: 200; easing.type: Easing.OutCubic } }
        Behavior on color { ColorAnimation { duration: 200 } }
    }
    Face { id: front }
    Face { id: back; opacity: 0 }
    Component.onCompleted: front.text = text
}
