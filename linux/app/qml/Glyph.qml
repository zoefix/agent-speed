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
        case "gear":
            ctx.lineWidth = 1.3
            ctx.beginPath()
            for (var t = 0; t < 8; ++t) {
                var a0 = t * Math.PI / 4 - 0.2, a1 = t * Math.PI / 4 + 0.2
                ctx.moveTo(8 + Math.cos(a0) * 5.2, 8 + Math.sin(a0) * 5.2)
                ctx.lineTo(8 + Math.cos(a0) * 6.6, 8 + Math.sin(a0) * 6.6)
                ctx.lineTo(8 + Math.cos(a1) * 6.6, 8 + Math.sin(a1) * 6.6)
                ctx.lineTo(8 + Math.cos(a1) * 5.2, 8 + Math.sin(a1) * 5.2)
            }
            ctx.stroke()
            ctx.beginPath(); ctx.arc(8, 8, 4.6, 0, Math.PI * 2); ctx.stroke()
            ctx.beginPath(); ctx.arc(8, 8, 1.9, 0, Math.PI * 2); ctx.stroke()
            break
        case "sliders":
            ctx.lineWidth = 1.4
            ctx.beginPath()
            ctx.moveTo(2.5, 4.5); ctx.lineTo(13.5, 4.5)
            ctx.moveTo(2.5, 11.5); ctx.lineTo(13.5, 11.5)
            ctx.stroke()
            ctx.beginPath(); ctx.arc(10.5, 4.5, 1.9, 0, Math.PI * 2); ctx.fill()
            ctx.beginPath(); ctx.arc(5.5, 11.5, 1.9, 0, Math.PI * 2); ctx.fill()
            break
        case "orb":
            ctx.lineWidth = 1.4
            ctx.beginPath(); ctx.arc(8, 8, 6, 0, Math.PI * 2); ctx.stroke()
            ctx.beginPath(); ctx.arc(8, 8, 3.4, Math.PI * 0.75, Math.PI * 2.25, false); ctx.stroke()
            ctx.beginPath(); ctx.arc(8, 8, 1.1, 0, Math.PI * 2); ctx.fill()
            break
        case "sparkle":
            ctx.beginPath()
            ctx.moveTo(7, 1.8); ctx.quadraticCurveTo(7.6, 6.4, 12.2, 7); ctx.quadraticCurveTo(7.6, 7.6, 7, 12.2)
            ctx.quadraticCurveTo(6.4, 7.6, 1.8, 7); ctx.quadraticCurveTo(6.4, 6.4, 7, 1.8)
            ctx.fill()
            ctx.beginPath()
            ctx.moveTo(12.4, 10); ctx.quadraticCurveTo(12.7, 12, 14.6, 12.3); ctx.quadraticCurveTo(12.7, 12.6, 12.4, 14.6)
            ctx.quadraticCurveTo(12.1, 12.6, 10.2, 12.3); ctx.quadraticCurveTo(12.1, 12, 12.4, 10)
            ctx.fill()
            break
        case "coin":
            ctx.lineWidth = 1.4
            ctx.beginPath(); ctx.arc(8, 8, 6, 0, Math.PI * 2); ctx.stroke()
            ctx.lineWidth = 1.3
            ctx.beginPath()
            ctx.moveTo(10.1, 5.8); ctx.bezierCurveTo(9.3, 4.9, 6, 4.8, 6, 6.6); ctx.bezierCurveTo(6, 8.2, 10.2, 7.6, 10.2, 9.5)
            ctx.bezierCurveTo(10.2, 11.2, 6.9, 11.2, 5.8, 10.2)
            ctx.moveTo(8, 3.6); ctx.lineTo(8, 4.7); ctx.moveTo(8, 11.3); ctx.lineTo(8, 12.4)
            ctx.stroke()
            break
        case "folder":
            ctx.lineWidth = 1.4
            ctx.beginPath()
            ctx.moveTo(2, 4.6); ctx.quadraticCurveTo(2, 3.4, 3.2, 3.4); ctx.lineTo(6, 3.4); ctx.lineTo(7.4, 4.9)
            ctx.lineTo(12.8, 4.9); ctx.quadraticCurveTo(14, 4.9, 14, 6.1); ctx.lineTo(14, 11.6); ctx.quadraticCurveTo(14, 12.8, 12.8, 12.8)
            ctx.lineTo(3.2, 12.8); ctx.quadraticCurveTo(2, 12.8, 2, 11.6); ctx.closePath()
            ctx.stroke()
            break
        case "bolt":
            ctx.beginPath()
            ctx.moveTo(9.4, 1); ctx.lineTo(3, 9.2); ctx.lineTo(7.4, 9.2); ctx.lineTo(6.4, 15)
            ctx.lineTo(13, 6.6); ctx.lineTo(8.6, 6.6); ctx.closePath()
            ctx.fill()
            break
        case "loop":
            ctx.lineWidth = 1.5
            ctx.beginPath(); ctx.arc(8, 8, 5.2, Math.PI * 1.08, Math.PI * 1.92, false); ctx.stroke()
            ctx.beginPath(); ctx.moveTo(11.2, 1.9); ctx.lineTo(13.2, 4.4); ctx.lineTo(10.2, 5.2); ctx.stroke()
            ctx.beginPath(); ctx.arc(8, 8, 5.2, Math.PI * 0.08, Math.PI * 0.92, false); ctx.stroke()
            ctx.beginPath(); ctx.moveTo(4.8, 14.1); ctx.lineTo(2.8, 11.6); ctx.lineTo(5.8, 10.8); ctx.stroke()
            break
        case "checkCircle":
            ctx.beginPath(); ctx.arc(8, 8, 7, 0, Math.PI * 2); ctx.fill()
            ctx.globalCompositeOperation = "destination-out"
            ctx.lineWidth = 1.8
            ctx.beginPath(); ctx.moveTo(4.6, 8.3); ctx.lineTo(7, 10.6); ctx.lineTo(11.4, 5.6); ctx.stroke()
            break
        case "alertCircle":
            ctx.beginPath(); ctx.arc(8, 8, 7, 0, Math.PI * 2); ctx.fill()
            ctx.globalCompositeOperation = "destination-out"
            ctx.lineWidth = 1.9
            ctx.beginPath(); ctx.moveTo(8, 4.2); ctx.lineTo(8, 8.8); ctx.stroke()
            ctx.beginPath(); ctx.arc(8, 11.6, 1.05, 0, Math.PI * 2); ctx.fill()
            break
        case "clock":
            ctx.lineWidth = 1.4
            ctx.beginPath(); ctx.arc(8, 8, 6.2, 0, Math.PI * 2); ctx.stroke()
            ctx.beginPath(); ctx.moveTo(8, 4.4); ctx.lineTo(8, 8.3); ctx.lineTo(10.6, 9.8); ctx.stroke()
            break
        case "dotRing":
            for (var k = 0; k < 8; ++k) {
                var a = k * Math.PI / 4
                ctx.beginPath(); ctx.arc(8 + 5.6 * Math.cos(a), 8 + 5.6 * Math.sin(a), 1.15, 0, Math.PI * 2); ctx.fill()
            }
            break
        case "check":
            ctx.lineWidth = 1.7
            ctx.beginPath(); ctx.moveTo(3.4, 8.4); ctx.lineTo(6.6, 11.4); ctx.lineTo(12.6, 4.8); ctx.stroke()
            break
        case "search":
            ctx.lineWidth = 1.5
            ctx.beginPath(); ctx.arc(7, 7, 4.3, 0, Math.PI * 2); ctx.stroke()
            ctx.beginPath(); ctx.moveTo(10.2, 10.2); ctx.lineTo(13.6, 13.6); ctx.stroke()
            break
        case "reset":
            ctx.lineWidth = 1.4
            ctx.beginPath(); ctx.arc(8, 8.4, 5, -Math.PI * 0.9, Math.PI * 0.75, false); ctx.stroke()
            ctx.beginPath(); ctx.moveTo(2.4, 2.8); ctx.lineTo(3.2, 6.2); ctx.lineTo(6.5, 5.2); ctx.stroke()
            break
        case "chart":
            ctx.lineWidth = 1.5
            ctx.beginPath()
            ctx.moveTo(3, 13.5); ctx.lineTo(3, 9)
            ctx.moveTo(6.5, 13.5); ctx.lineTo(6.5, 5.5)
            ctx.moveTo(10, 13.5); ctx.lineTo(10, 8)
            ctx.moveTo(13.5, 13.5); ctx.lineTo(13.5, 3)
            ctx.stroke()
            break
        case "broadcast":
            ctx.lineWidth = 1.4
            ctx.beginPath(); ctx.arc(8, 8, 1.5, 0, Math.PI * 2); ctx.fill()
            ctx.beginPath(); ctx.arc(8, 8, 4, -Math.PI * 0.27, Math.PI * 0.27); ctx.stroke()
            ctx.beginPath(); ctx.arc(8, 8, 4, Math.PI * 0.73, Math.PI * 1.27); ctx.stroke()
            ctx.beginPath(); ctx.arc(8, 8, 6.6, -Math.PI * 0.25, Math.PI * 0.25); ctx.stroke()
            ctx.beginPath(); ctx.arc(8, 8, 6.6, Math.PI * 0.75, Math.PI * 1.25); ctx.stroke()
            break
        case "dice":
            ctx.lineWidth = 1.3
            ctx.beginPath(); ctx.roundedRect(2.6, 2.6, 10.8, 10.8, 2.6, 2.6); ctx.stroke()
            var pips = [[5.6, 5.6], [10.4, 10.4], [8, 8], [10.4, 5.6], [5.6, 10.4]]
            for (var q = 0; q < pips.length; ++q) {
                ctx.beginPath(); ctx.arc(pips[q][0], pips[q][1], 1, 0, Math.PI * 2); ctx.fill()
            }
            break
        case "eye":
            ctx.lineWidth = 1.3
            ctx.beginPath()
            ctx.moveTo(1.8, 8); ctx.bezierCurveTo(4, 3.8, 12, 3.8, 14.2, 8)
            ctx.bezierCurveTo(12, 12.2, 4, 12.2, 1.8, 8)
            ctx.stroke()
            ctx.beginPath(); ctx.arc(8, 8, 2.1, 0, Math.PI * 2); ctx.fill()
            break
        case "close":
            ctx.lineWidth = 1.8
            ctx.beginPath(); ctx.moveTo(4.5, 4.5); ctx.lineTo(11.5, 11.5); ctx.moveTo(11.5, 4.5); ctx.lineTo(4.5, 11.5); ctx.stroke()
            break
        case "eyeOff":
            ctx.lineWidth = 1.3
            ctx.beginPath()
            ctx.moveTo(1.8, 8); ctx.bezierCurveTo(4, 3.8, 12, 3.8, 14.2, 8)
            ctx.bezierCurveTo(12, 12.2, 4, 12.2, 1.8, 8)
            ctx.stroke()
            ctx.beginPath(); ctx.moveTo(3, 13); ctx.lineTo(13, 3); ctx.stroke()
            break
        case "plus":
            ctx.lineWidth = 1.6
            ctx.beginPath(); ctx.moveTo(8, 3.5); ctx.lineTo(8, 12.5); ctx.moveTo(3.5, 8); ctx.lineTo(12.5, 8); ctx.stroke()
            break
        case "minus":
            ctx.lineWidth = 1.6
            ctx.beginPath(); ctx.moveTo(3.5, 8); ctx.lineTo(12.5, 8); ctx.stroke()
            break
        case "copy":
            ctx.lineWidth = 1.3
            ctx.beginPath(); ctx.roundedRect(5.5, 5.5, 8, 8, 1.8, 1.8); ctx.stroke()
            ctx.beginPath(); ctx.moveTo(3.5, 10.5); ctx.lineTo(3.2, 10.5); ctx.arcTo(2.5, 10.5, 2.5, 9.8, 0.7)
            ctx.lineTo(2.5, 3.2); ctx.arcTo(2.5, 2.5, 3.2, 2.5, 0.7); ctx.lineTo(9.8, 2.5); ctx.arcTo(10.5, 2.5, 10.5, 3.2, 0.7)
            ctx.lineTo(10.5, 3.5); ctx.stroke()
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
