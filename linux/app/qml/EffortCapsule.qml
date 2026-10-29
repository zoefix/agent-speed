import QtQuick
import "Heat.js" as Heat

// DESIGN 4 effort capsule: a five-bar meter for the effort level, the effort word and bolts for a fast speed.
Item {
    id: capsule
    property int level: -1
    property string word: ""
    property bool topRung: false
    property int bolts: 0
    property bool showWord: true
    readonly property bool present: level >= 0 || word.length > 0 || bolts > 0
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
                    color: !lit ? Theme.meterOff : capsule.topRung ? Heat.color(0.72 + 0.07 * index) : capsule.level >= 5 ? capsule.hot : Theme.ink2
                    Behavior on color { ColorAnimation { duration: 220 } }
                }
            }
        }
        Text {
            visible: capsule.showWord && capsule.word.length > 0
            anchors.verticalCenter: parent.verticalCenter
            text: capsule.word.toLowerCase()
            color: capsule.topRung || capsule.level >= 5 ? capsule.hot : Theme.ink2
            font.pixelSize: 11
            font.weight: Font.Medium
        }
        Item {
            visible: capsule.bolts > 0
            width: capsule.bolts > 1 ? 15 : 9
            height: 10
            anchors.verticalCenter: parent.verticalCenter
            Glyph { visible: capsule.bolts > 1; x: 0; width: 9; height: 10; name: "bolt"; color: Theme.bolt; opacity: 0.6 }
            Glyph { x: capsule.bolts > 1 ? 6 : 0; width: 9; height: 10; name: "bolt"; color: Theme.bolt }
        }
    }
}
