import QtQuick
import QtQuick.Controls
import QtQuick.Effects

// A quota line's tooltip (DESIGN 4 Quota lines, Tooltip of a line): the long reset, the snapshot's time and the pace;
// while an estimate exists a hairline, the estimate's lines in secondary text and its note in tertiary. As wide as its
// widest line, at most 320 px; longer lines wrap.
ToolTip {
    id: tip
    // Each part's lines joined by "\n".
    property string head
    property string details
    property string note
    readonly property var headLines: head.length > 0 ? head.split("\n") : []
    readonly property var detailLines: details.length > 0 ? details.split("\n") : []
    readonly property bool hasDetails: detailLines.length > 0
    readonly property real textWidth: {
        let widest = 0
        const lines = headLines.concat(detailLines)
        if (hasDetails && note.length > 0) lines.push(note)
        for (let i = 0; i < lines.length; ++i) widest = Math.max(widest, Math.ceil(lineMetrics.advanceWidth(lines[i])) + 1)
        return Math.min(320 - leftPadding - rightPadding, widest)
    }

    text: [head, details, hasDetails ? note : ""].filter(t => t.length > 0).join("\n")
    leftPadding: 10
    rightPadding: 10
    topPadding: 8
    bottomPadding: 8
    enter: Transition { NumberAnimation { property: "opacity"; from: 0; to: 1; duration: 150; easing.type: Easing.OutCubic } }
    exit: Transition { NumberAnimation { property: "opacity"; to: 0; duration: 100 } }
    background: Item {
        Rectangle {
            id: caster
            anchors.fill: parent
            radius: 8
            color: "black"
            visible: false
        }
        MultiEffect {
            anchors.fill: caster
            source: caster
            visible: !softwareRendering
            shadowEnabled: true
            shadowColor: "#000000"
            shadowOpacity: Theme.dark ? 0.45 : 0.14
            shadowBlur: 0.8
            blurMax: 16
            shadowVerticalOffset: 3
        }
        Rectangle {
            anchors.fill: parent
            radius: 8
            color: Theme.cardFill
            border.width: 1
            border.color: Theme.cardLine
        }
    }

    component Line: Text {
        width: tip.textWidth
        textFormat: Text.PlainText
        font.pixelSize: 11
        font.features: Theme.tabular
        wrapMode: Text.Wrap
    }

    contentItem: Column {
        spacing: 3
        FontMetrics { id: lineMetrics; font.pixelSize: 11; font.features: Theme.tabular }
        Repeater {
            model: tip.headLines
            Line {
                required property string modelData
                text: modelData
                color: Theme.ink
            }
        }
        Item {
            visible: tip.hasDetails
            width: tip.textWidth
            height: 7
            Rectangle {
                anchors.verticalCenter: parent.verticalCenter
                width: parent.width
                height: 1
                color: Theme.cardLine
            }
        }
        Repeater {
            model: tip.hasDetails ? tip.detailLines : []
            Line {
                required property string modelData
                text: modelData
                color: Theme.ink2
            }
        }
        Line {
            visible: tip.hasDetails && tip.note.length > 0
            topPadding: 2
            text: tip.note
            color: Theme.ink3
            lineHeight: 1.15
        }
    }
}
