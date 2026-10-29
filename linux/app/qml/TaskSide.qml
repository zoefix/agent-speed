import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// One source's side header (DESIGN 3c): name, counts, the median and the score distribution, aligned toward the centre.
Item {
    id: root
    property var side: null
    property bool mirrored: false
    property real distWidth: 120
    readonly property bool present: side !== null && side !== undefined
    implicitHeight: column.implicitHeight

    ColumnLayout {
        id: column
        width: parent.width
        spacing: 3
        RowLayout {
            Layout.alignment: root.mirrored ? Qt.AlignRight : Qt.AlignLeft
            spacing: 6
            Rectangle {
                Layout.preferredWidth: 7
                Layout.preferredHeight: 7
                radius: 3.5
                color: Theme.sourceColor(root.present ? root.side.source : "")
            }
            Text {
                text: root.present ? root.side.name : ""
                color: Theme.ink
                font.pixelSize: 13
                font.weight: Font.DemiBold
            }
        }
        RowLayout {
            Layout.alignment: root.mirrored ? Qt.AlignRight : Qt.AlignLeft
            spacing: 6
            Text {
                text: root.present ? (root.side.tasks > 0 ? root.side.countsText : S.tr2(S.rev, "history.tasks.side.empty")) : ""
                color: Theme.ink2
                font.pixelSize: 11
                font.features: Theme.tabular
            }
            Rectangle {
                visible: root.present && root.side.tasks > 0 && root.side.low
                Layout.preferredHeight: 16
                Layout.preferredWidth: lowText.implicitWidth + 12
                radius: 8
                color: Theme.field
                Text {
                    id: lowText
                    anchors.centerIn: parent
                    text: S.tr2(S.rev, "history.tasks.sample.low")
                    color: Theme.ink2
                    font.pixelSize: 10
                    font.weight: Font.Medium
                }
                HoverHandler { id: lowHover }
                ToolTip.visible: lowHover.hovered
                ToolTip.delay: 300
                ToolTip.text: root.present ? S.tr2(S.rev, "history.tasks.sample.hint").replace("%d", root.side.scored).replace("%d", 10) : ""
            }
        }
        RowLayout {
            Layout.alignment: root.mirrored ? Qt.AlignRight : Qt.AlignLeft
            Layout.topMargin: 4
            spacing: 14
            layoutDirection: root.mirrored ? Qt.RightToLeft : Qt.LeftToRight
            Row {
                spacing: 5
                Text {
                    id: median
                    text: root.present ? root.side.medianText : "–"
                    color: root.present && root.side.median !== undefined && root.side.median !== null ? Theme.ink : Theme.ink3
                    font.pixelSize: 34
                    font.weight: Font.DemiBold
                    font.features: Theme.tabular
                }
                Text {
                    anchors.baseline: median.baseline
                    text: S.tr2(S.rev, "history.tasks.metric.median.short")
                    color: Theme.ink2
                    font.pixelSize: 11
                }
            }
            // Distribution: ten bins from 0 to 100, the median's bin full strength.
            Item {
                id: dist
                Layout.preferredWidth: root.distWidth
                Layout.preferredHeight: 40
                readonly property var bins: root.present ? root.side.histogram : []
                readonly property real peak: Math.max.apply(null, bins.concat([1]))
                Row {
                    y: 0
                    spacing: 2
                    Repeater {
                        model: dist.bins
                        Item {
                            required property var modelData
                            required property int index
                            width: (root.distWidth - 18) / 10
                            height: 28
                            Rectangle {
                                anchors.bottom: parent.bottom
                                width: parent.width
                                height: Math.max(modelData > 0 ? 2 : 0, 28 * modelData / dist.peak)
                                radius: 1.5
                                color: Theme.sourceColor(root.present ? root.side.source : "")
                                opacity: index === (root.present ? root.side.medianBin : -1) ? 1 : 0.7
                                Behavior on height { NumberAnimation { duration: 350; easing.type: Easing.OutCubic } }
                            }
                        }
                    }
                }
                Rectangle {
                    visible: root.present && root.side.median !== undefined && root.side.median !== null
                    x: root.present && root.side.median !== null ? Math.min(root.distWidth - 1, root.distWidth * root.side.median / 100) : 0
                    y: 0
                    width: 1
                    height: 28
                    color: Theme.ink
                }
                Text { y: 30; text: "0"; color: Theme.ink3; font.pixelSize: 9 }
                Text { y: 30; anchors.right: parent.right; text: "100"; color: Theme.ink3; font.pixelSize: 9 }
            }
        }
    }
}
