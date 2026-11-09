import QtQuick

// A quiet standing figure for a character that is not downloaded.
Canvas {
    id: figure
    property color color: Theme.ink3
    onColorChanged: requestPaint()
    onWidthChanged: requestPaint()
    onHeightChanged: requestPaint()
    onPaint: {
        const ctx = getContext("2d")
        ctx.reset()
        const k = Math.min(width / 28, height / 50)
        ctx.translate((width - 24 * k) / 2, (height - 46 * k) / 2)
        ctx.scale(k, k)
        ctx.fillStyle = figure.color
        ctx.beginPath()
        ctx.ellipse(6, 0.5, 12, 13)
        ctx.fill()
        ctx.beginPath()
        ctx.moveTo(5, 16)
        ctx.lineTo(19, 16)
        ctx.quadraticCurveTo(22, 16, 22, 21)
        ctx.lineTo(24, 34)
        ctx.lineTo(18, 34)
        ctx.lineTo(17, 46)
        ctx.lineTo(7, 46)
        ctx.lineTo(6, 34)
        ctx.lineTo(0, 34)
        ctx.lineTo(2, 21)
        ctx.quadraticCurveTo(2, 16, 5, 16)
        ctx.closePath()
        ctx.fill()
    }
}
