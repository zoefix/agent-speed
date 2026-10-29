import QtQuick
import "Heat.js" as Heat

// The hero gauge: a dark dial whose 270° arc and ticks light up through the heat palette, with a core glow that grows
// with the speed (a light stand-in for the orb until the shader orbs are ported).
Item {
    id: gauge
    property real heat: 0
    property real shownHeat: heat
    Behavior on shownHeat { NumberAnimation { duration: 320; easing.type: Easing.OutCubic } }
    onShownHeatChanged: dial.requestPaint()
    implicitWidth: 112
    implicitHeight: 112

    Rectangle {
        anchors.fill: parent
        radius: width / 2
        gradient: Gradient {
            GradientStop { position: 0; color: Theme.dark ? "#272C36" : "#2B313C" }
            GradientStop { position: 1; color: Theme.dark ? "#0F1218" : "#141820" }
        }
        border.width: 1
        border.color: Qt.rgba(1, 1, 1, Theme.dark ? 0.07 : 0.12)
    }

    Canvas {
        id: dial
        anchors.fill: parent
        antialiasing: true
        onPaint: {
            var ctx = getContext("2d")
            ctx.reset()
            var h = gauge.shownHeat
            var cx = width / 2, cy = height / 2, r = width / 2 - 13
            var start = Math.PI * 0.75, sweep = Math.PI * 1.5
            var tone = Heat.color(h)

            var glow = ctx.createRadialGradient(cx, cy, 0, cx, cy, r * 0.82)
            glow.addColorStop(0, Qt.rgba(tone.r, tone.g, tone.b, 0.18 + 0.5 * h))
            glow.addColorStop(0.45, Qt.rgba(tone.r, tone.g, tone.b, 0.06 + 0.16 * h))
            glow.addColorStop(1, Qt.rgba(tone.r, tone.g, tone.b, 0))
            ctx.fillStyle = glow
            ctx.beginPath(); ctx.arc(cx, cy, r * 0.82, 0, Math.PI * 2); ctx.fill()

            ctx.lineCap = "round"
            ctx.lineWidth = 6
            ctx.strokeStyle = Qt.rgba(1, 1, 1, 0.08)
            ctx.beginPath(); ctx.arc(cx, cy, r, start, start + sweep, false); ctx.stroke()

            for (var i = 0; i <= 24; ++i) {
                var a = start + sweep * i / 24
                var major = i % 6 === 0
                ctx.strokeStyle = i / 24 <= h + 1e-6 && h > 0.005 ? Heat.color(i / 24) : Qt.rgba(1, 1, 1, major ? 0.24 : 0.13)
                ctx.lineWidth = major ? 1.8 : 1.1
                var r0 = r - 11, r1 = r - (major ? 6.5 : 8.5)
                ctx.beginPath()
                ctx.moveTo(cx + Math.cos(a) * r0, cy + Math.sin(a) * r0)
                ctx.lineTo(cx + Math.cos(a) * r1, cy + Math.sin(a) * r1)
                ctx.stroke()
            }

            if (h > 0.005) {
                var segments = Math.max(2, Math.round(32 * h))
                ctx.lineWidth = 6
                for (var s = 0; s < segments; ++s) {
                    ctx.strokeStyle = Heat.color(h * (s + 0.5) / segments)
                    ctx.beginPath()
                    ctx.arc(cx, cy, r, start + sweep * h * s / segments, start + sweep * h * (s + 1) / segments + 0.012, false)
                    ctx.stroke()
                }
            }

            var tip = start + sweep * h
            ctx.strokeStyle = Qt.rgba(tone.r, tone.g, tone.b, h > 0.005 ? 0.9 : 0.4)
            ctx.lineWidth = 2
            ctx.beginPath()
            ctx.moveTo(cx + Math.cos(tip) * 6, cy + Math.sin(tip) * 6)
            ctx.lineTo(cx + Math.cos(tip) * (r - 15), cy + Math.sin(tip) * (r - 15))
            ctx.stroke()
            ctx.fillStyle = Qt.rgba(tone.r, tone.g, tone.b, 0.35)
            ctx.beginPath(); ctx.arc(cx + Math.cos(tip) * r, cy + Math.sin(tip) * r, 6, 0, Math.PI * 2); ctx.fill()
            ctx.fillStyle = h > 0.005 ? "#FFF8E1" : Qt.rgba(1, 1, 1, 0.5)
            ctx.beginPath(); ctx.arc(cx + Math.cos(tip) * r, cy + Math.sin(tip) * r, 3, 0, Math.PI * 2); ctx.fill()

            ctx.fillStyle = "#1A1E26"
            ctx.beginPath(); ctx.arc(cx, cy, 5, 0, Math.PI * 2); ctx.fill()
            ctx.strokeStyle = Qt.rgba(tone.r, tone.g, tone.b, 0.9)
            ctx.lineWidth = 1.5
            ctx.beginPath(); ctx.arc(cx, cy, 5, 0, Math.PI * 2); ctx.stroke()
        }
    }
}
