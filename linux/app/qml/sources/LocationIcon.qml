import QtQuick

// DESIGN 5 Entry row: a folder, or a document for a database file, tinted in the accent color.
Canvas {
    id: icon
    property bool file: false
    readonly property color ink: Theme.accent
    implicitWidth: 22
    implicitHeight: 22
    antialiasing: true
    onFileChanged: requestPaint()
    onInkChanged: requestPaint()

    onPaint: {
        var ctx = getContext("2d")
        ctx.reset()
        ctx.scale(width / 22, height / 22)
        ctx.lineJoin = "round"
        ctx.lineCap = "round"
        ctx.lineWidth = 1.3
        ctx.strokeStyle = icon.ink
        ctx.fillStyle = Qt.rgba(icon.ink.r, icon.ink.g, icon.ink.b, Theme.dark ? 0.22 : 0.14)
        ctx.beginPath()
        if (icon.file) {
            ctx.moveTo(6, 2.5); ctx.lineTo(13, 2.5); ctx.lineTo(17.5, 7); ctx.lineTo(17.5, 18)
            ctx.quadraticCurveTo(17.5, 19.5, 16, 19.5); ctx.lineTo(6, 19.5); ctx.quadraticCurveTo(4.5, 19.5, 4.5, 18)
            ctx.lineTo(4.5, 4); ctx.quadraticCurveTo(4.5, 2.5, 6, 2.5); ctx.closePath()
            ctx.fill(); ctx.stroke()
            ctx.beginPath(); ctx.moveTo(13, 2.5); ctx.lineTo(13, 7); ctx.lineTo(17.5, 7); ctx.stroke()
            ctx.beginPath()
            ctx.moveTo(7.5, 11.5); ctx.lineTo(14.5, 11.5)
            ctx.moveTo(7.5, 14.5); ctx.lineTo(12.5, 14.5)
            ctx.stroke()
        } else {
            ctx.moveTo(2.5, 6); ctx.quadraticCurveTo(2.5, 4.5, 4, 4.5); ctx.lineTo(8.4, 4.5); ctx.lineTo(10.2, 6.4)
            ctx.lineTo(18, 6.4); ctx.quadraticCurveTo(19.5, 6.4, 19.5, 7.9); ctx.lineTo(19.5, 16.5)
            ctx.quadraticCurveTo(19.5, 18, 18, 18); ctx.lineTo(4, 18); ctx.quadraticCurveTo(2.5, 18, 2.5, 16.5); ctx.closePath()
            ctx.fill(); ctx.stroke()
            ctx.beginPath(); ctx.moveTo(2.5, 9); ctx.lineTo(19.5, 9); ctx.stroke()
        }
    }
}
