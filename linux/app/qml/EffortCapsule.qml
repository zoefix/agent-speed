import QtQuick
import "Heat.js" as Heat

// DESIGN 4 effort capsule: a five-bar meter for the effort level, the effort word and bolts for a fast speed. A value
// only a config file holds is drawn quietly: a dashed outline when nothing real is in the capsule.
Item {
    id: capsule
    property int level: -1
    property string word: ""
    property bool topRung: false
    property int bolts: 0
    property bool showWord: true
    property bool effortConfigured: false
    property bool speedConfigured: false
    property string mark: ""
    // The boards draw a configured capsule in their own palette (DESIGN 2c).
    property color configuredInk: Theme.ink3
    property color configuredLine: Theme.capsuleDash
    readonly property bool hasEffort: level >= 0 || word.length > 0
    readonly property bool present: hasEffort || bolts > 0
    // A word without a meter stays when the others drop theirs, or the capsule would be empty.
    readonly property bool wordShown: word.length > 0 && (showWord || (level < 0 && bolts === 0))
    readonly property bool configuredForm: present && (!hasEffort || effortConfigured) && (bolts === 0 || speedConfigured)
    readonly property color hot: Heat.color(0.82)
    implicitWidth: present ? parts.implicitWidth + 12 : 0
    implicitHeight: 18
    visible: present

    Rectangle {
        anchors.fill: parent
        radius: height / 2
        color: Theme.capsuleFill
        border.width: 0.5
        border.color: Theme.capsuleLine
        opacity: capsule.configuredForm ? 0 : 1
        Behavior on opacity { NumberAnimation { duration: 200 } }
    }
    Canvas {
        id: dashes
        anchors.fill: parent
        opacity: capsule.configuredForm ? 1 : 0
        visible: opacity > 0
        antialiasing: true
        Behavior on opacity { NumberAnimation { duration: 200 } }
        property color line: capsule.configuredLine
        onLineChanged: requestPaint()
        onWidthChanged: requestPaint()
        onVisibleChanged: if (visible) requestPaint()
        onPaint: {
            var ctx = getContext("2d")
            ctx.reset()
            ctx.strokeStyle = line
            ctx.lineWidth = 0.75
            ctx.lineCap = "round"
            ctx.setLineDash([3 / 0.75, 2.5 / 0.75])
            ctx.beginPath()
            ctx.roundedRect(0.5, 0.5, width - 1, height - 1, (height - 1) / 2, (height - 1) / 2)
            ctx.stroke()
        }
    }
    Row {
        id: parts
        anchors.centerIn: parent
        spacing: 5
        Item {
            visible: capsule.level >= 0
            width: 18.5
            height: 10
            anchors.verticalCenter: parent.verticalCenter
            Repeater {
                model: 5
                Rectangle {
                    required property int index
                    readonly property bool lit: index < capsule.level
                    x: index * 4
                    width: 2.5
                    height: [4, 5.5, 7, 8.5, 10][index]
                    y: 10 - height
                    radius: 0.75
                    color: capsule.effortConfigured ? (lit ? capsule.configuredInk : Theme.meterOffConfigured)
                           : !lit ? Theme.meterOff : capsule.topRung ? Heat.color(0.72 + 0.07 * index) : capsule.level >= 5 ? capsule.hot : Theme.ink2
                    Behavior on color { ColorAnimation { duration: 220 } }
                }
            }
        }
        Text {
            visible: capsule.wordShown
            anchors.verticalCenter: parent.verticalCenter
            text: capsule.word.toLowerCase()
            color: capsule.effortConfigured ? capsule.configuredInk : capsule.topRung || capsule.level >= 5 ? capsule.hot : Theme.ink2
            font.pixelSize: 11
            font.weight: Font.Medium
            Behavior on color { ColorAnimation { duration: 200 } }
        }
        Text {
            visible: capsule.configuredForm && capsule.wordShown && capsule.mark.length > 0
            anchors.verticalCenter: parent.verticalCenter
            text: capsule.mark
            color: capsule.configuredInk
            font.pixelSize: 10
            font.weight: Font.Medium
        }
        Item {
            visible: capsule.bolts > 0
            width: capsule.bolts > 1 ? 15 : 9
            height: 10
            anchors.verticalCenter: parent.verticalCenter
            readonly property string glyph: capsule.speedConfigured ? "boltOutline" : "bolt"
            readonly property color tone: capsule.speedConfigured ? capsule.configuredInk : Theme.bolt
            Glyph { visible: capsule.bolts > 1; x: 0; width: 9; height: 10; name: parent.glyph; color: parent.tone; opacity: 0.6 }
            Glyph { x: capsule.bolts > 1 ? 6 : 0; width: 9; height: 10; name: parent.glyph; color: parent.tone }
        }
    }
}
