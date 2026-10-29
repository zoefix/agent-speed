import QtQuick

// Small line glyphs drawn on a 16-unit grid, so they follow the theme color without an icon theme.
Canvas {
    id: glyph
    property string name
    property color color: Theme.ink2
    implicitWidth: 16
    implicitHeight: 16
    antialiasing: true
    onColorChanged: requestPaint()
    onNameChanged: requestPaint()

    onPaint: {
        var ctx = getContext("2d")
        ctx.reset()
        ctx.scale(width / 16, height / 16)
        ctx.strokeStyle = glyph.color
        ctx.fillStyle = glyph.color
        ctx.lineCap = "round"
        ctx.lineJoin = "round"
        ctx.lineWidth = 1.4
        switch (glyph.name) {
        case "hourglass":
            ctx.beginPath()
            ctx.moveTo(3.5, 2); ctx.lineTo(12.5, 2)
            ctx.moveTo(3.5, 14); ctx.lineTo(12.5, 14)
            ctx.moveTo(5, 2.2); ctx.bezierCurveTo(5, 6, 8, 6.5, 8, 8); ctx.bezierCurveTo(8, 9.5, 5, 10, 5, 13.8)
            ctx.moveTo(11, 2.2); ctx.bezierCurveTo(11, 6, 8, 6.5, 8, 8); ctx.bezierCurveTo(8, 9.5, 11, 10, 11, 13.8)
            ctx.stroke()
            ctx.beginPath()
            ctx.moveTo(6.2, 13.2); ctx.lineTo(9.8, 13.2); ctx.lineTo(8, 10.6); ctx.closePath()
            ctx.moveTo(6.6, 4.6); ctx.lineTo(9.4, 4.6); ctx.lineTo(8, 6.4); ctx.closePath()
            ctx.fill()
            break
        case "wrench":
            ctx.translate(8, 8)
            ctx.rotate(Math.PI / 4)
            ctx.beginPath()
            ctx.arc(0, -3.4, 3.7, 0, Math.PI * 2)
            ctx.fill()
            ctx.beginPath()
            ctx.roundedRect(-1.35, -1.5, 2.7, 9, 1.35, 1.35)
            ctx.fill()
            ctx.globalCompositeOperation = "destination-out"
            ctx.beginPath()
            ctx.roundedRect(-1.25, -8.2, 2.5, 5.2, 0.8, 0.8)
            ctx.fill()
            break
        case "info":
            ctx.lineWidth = 1.3
            ctx.beginPath(); ctx.arc(8, 8, 6.4, 0, Math.PI * 2); ctx.stroke()
            ctx.beginPath(); ctx.arc(8, 5.1, 0.95, 0, Math.PI * 2); ctx.fill()
            ctx.lineWidth = 1.5
            ctx.beginPath(); ctx.moveTo(8, 7.4); ctx.lineTo(8, 11.2); ctx.stroke()
            break
        case "power":
            ctx.lineWidth = 1.5
            ctx.beginPath(); ctx.arc(8, 8.6, 5.4, -Math.PI / 2 + 0.72, Math.PI * 1.5 - 0.72, false); ctx.stroke()
            ctx.beginPath(); ctx.moveTo(8, 2.2); ctx.lineTo(8, 7.8); ctx.stroke()
            break
        case "chevron":
            ctx.lineWidth = 1.5
            ctx.beginPath(); ctx.moveTo(4.5, 6.2); ctx.lineTo(8, 9.8); ctx.lineTo(11.5, 6.2); ctx.stroke()
            break
        case "gauge":
            ctx.lineWidth = 1.4
            ctx.beginPath(); ctx.arc(8, 9, 6, Math.PI * 0.8, Math.PI * 2.2, false); ctx.stroke()
            ctx.beginPath(); ctx.moveTo(8, 9); ctx.lineTo(5.4, 6.6); ctx.stroke()
            ctx.beginPath(); ctx.arc(8, 9, 1.2, 0, Math.PI * 2); ctx.fill()
            break
        }
    }
}
