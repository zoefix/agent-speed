import QtQuick

// DESIGN 2c Motion and effects: the board's particles and bursts over its content — hero embers (glass rising sparks,
// timing a horizontal spray, neon sparks plus a lightning arc), the new-peak gold ring, the new-leader gold and white
// shockwaves with ~70 gold sparks, overtake light sweeps with streaks (neon lightning), and sparks from the comet heads
// at the redline. Draws only while something is alive.
Canvas {
    id: fx
    property string style: "neon"
    property string level: "standard"   // off, soft, standard, max
    property bool dark: true
    property var particles: []
    property var rings: []
    property var bolts: []
    property var sweeps: []
    property real last: 0
    readonly property bool enabledFx: level !== "off"
    readonly property real density: level === "max" ? 1.5 : 1
    readonly property bool alive: particles.length + rings.length + bolts.length + sweeps.length > 0

    function now() { return Date.now() / 1000 }
    function rgba(c, a) { return Qt.rgba(c.r, c.g, c.b, a) }

    function spark(x, y, vx, vy, life, color, size, gravity, streak) {
        particles.push({ x: x, y: y, vx: vx, vy: vy, age: 0, life: life, color: color, size: size, gravity: gravity, streak: streak })
    }

    function burst(x, y, count, color, speedLo, speedHi, gravity, life) {
        if (level === "soft" || level === "off") return
        var n = Math.round(count * density)
        for (var i = 0; i < n; ++i) {
            var a = Math.random() * Math.PI * 2, v = speedLo + (speedHi - speedLo) * Math.random()
            spark(x, y, Math.cos(a) * v, Math.sin(a) * v, life * (0.7 + 0.6 * Math.random()), color, 1.2 + Math.random(), gravity, true)
        }
        start()
    }

    function bolt(x0, y0, x1, y1, color, life) {
        bolts.push({ x0: x0, y0: y0, x1: x1, y1: y1, color: color, age: 0, life: life, seed: Math.random() * 1000 })
        start()
    }

    // A rise of more than 8 % + 20 tok/s punches the number and releases embers.
    function punch(x, y, color) {
        if (!enabledFx || level === "soft") return
        var n = Math.round(14 * density)
        for (var i = 0; i < n; ++i) {
            if (style === "timing") spark(x, y + (Math.random() - 0.5) * 10, 60 + 90 * Math.random(), (Math.random() - 0.5) * 20, 0.5, color, 1.2, 0, true)
            else spark(x + (Math.random() - 0.5) * 40, y, (Math.random() - 0.5) * 30, -(40 + 60 * Math.random()), 0.7, color, 1.4, -20, true)
        }
        if (style === "neon") bolt(x - 30, y - 6, x + 30, y + 4, color, 0.18)
        start()
    }

    // A new peak: a gold ring from the number.
    function goldRing(x, y) {
        if (!enabledFx) return
        rings.push({ x: x, y: y, r0: 8, r1: 70, age: 0, life: 0.9, color: "#FACC15", width: 2.4 })
        start()
    }

    // A new leader: a gold and a white shockwave across the board, ~70 gold sparks.
    function newLeader(x, y, reach) {
        if (!enabledFx) return
        rings.push({ x: x, y: y, r0: 6, r1: reach, age: 0, life: 0.9, color: "#FACC15", width: 3 })
        rings.push({ x: x, y: y, r0: 4, r1: reach * 0.8, age: -0.08, life: 0.8, color: "#FFFFFF", width: 2 })
        burst(x, y, 70, "#FACC15", 50, 170, 60, 0.9)
        start()
    }

    // An overtake: a light sweep across the climber's row in its source color, streaks along it.
    function overtake(x, y, w, h, color, fromY) {
        if (!enabledFx) return
        sweeps.push({ x: x, y: y, w: w, h: h, color: color, age: 0, life: 0.7 })
        if (level !== "soft") {
            for (var i = 0; i < Math.round(10 * density); ++i) spark(x + w * Math.random(), y + h / 2, 80 + 120 * Math.random(), (Math.random() - 0.5) * 12, 0.45, color, 1, 0, true)
        }
        if (style === "neon" && fromY !== undefined) bolt(x + 14, y + h / 2, x + 14, fromY, color, 0.25)
        start()
    }

    function start() {
        last = now()
        if (!ticker.running) ticker.start()
    }

    Timer {
        id: ticker
        interval: 16
        repeat: true
        onTriggered: {
            var t = fx.now(), dt = Math.min(0.05, t - fx.last)
            fx.last = t
            var next = []
            for (var i = 0; i < fx.particles.length; ++i) {
                var p = fx.particles[i]
                p.age += dt
                if (p.age >= p.life) continue
                p.vy += p.gravity * dt
                p.vx *= Math.exp(-1.6 * dt)
                p.vy *= Math.exp(-1.6 * dt)
                p.x += p.vx * dt
                p.y += p.vy * dt
                next.push(p)
            }
            fx.particles = next
            fx.rings = fx.rings.filter(function(r) { r.age += dt; return r.age < r.life })
            fx.bolts = fx.bolts.filter(function(b) { b.age += dt; return b.age < b.life })
            fx.sweeps = fx.sweeps.filter(function(s) { s.age += dt; return s.age < s.life })
            if (!fx.alive) stop()
            fx.requestPaint()
        }
    }

    onPaint: {
        var ctx = getContext("2d")
        ctx.reset()
        ctx.globalCompositeOperation = dark ? "lighter" : "source-over"
        for (var s = 0; s < sweeps.length; ++s) {
            var sw = sweeps[s], u = sw.age / sw.life
            var cx = sw.x - sw.w * 0.3 + sw.w * 1.6 * u
            var g = ctx.createLinearGradient(cx - 40, 0, cx + 40, 0)
            g.addColorStop(0, rgba(sw.color, 0))
            g.addColorStop(0.5, rgba(sw.color, 0.35 * (1 - u)))
            g.addColorStop(1, rgba(sw.color, 0))
            ctx.fillStyle = g
            ctx.fillRect(sw.x, sw.y, sw.w, sw.h)
        }
        for (var r = 0; r < rings.length; ++r) {
            var ring = rings[r]
            if (ring.age < 0) continue
            var ru = ring.age / ring.life, eased = 1 - Math.pow(1 - ru, 3)
            ctx.strokeStyle = rgba(Qt.color(ring.color), 0.8 * (1 - ru))
            ctx.lineWidth = Math.max(0.2, ring.width * (1 - ru))
            ctx.beginPath()
            ctx.arc(ring.x, ring.y, ring.r0 + (ring.r1 - ring.r0) * eased, 0, Math.PI * 2)
            ctx.stroke()
        }
        for (var b = 0; b < bolts.length; ++b) {
            var bo = bolts[b], bu = bo.age / bo.life
            var alpha = bu < 0.2 ? 1 : 1 - (bu - 0.2) / 0.8
            var step = Math.floor(bo.age / 0.05)
            var pts = []
            for (var k = 0; k <= 8; ++k) {
                var f = k / 8
                var jitter = k === 0 || k === 8 ? 0 : (Math.sin(bo.seed + step * 31 + k * 12.9898) * 43758.5453 % 1) * 9 * Math.sin(Math.PI * f)
                var dx = bo.x1 - bo.x0, dy = bo.y1 - bo.y0, len = Math.max(1, Math.sqrt(dx * dx + dy * dy))
                pts.push([bo.x0 + dx * f - dy / len * jitter, bo.y0 + dy * f + dx / len * jitter])
            }
            var widths = [6, 2.2, 0.9], alphas = [0.28, 0.75, 1]
            for (var w = 0; w < 3; ++w) {
                ctx.strokeStyle = w === 2 ? Qt.rgba(1, 0.97, 0.84, alpha) : rgba(Qt.color(bo.color), alphas[w] * alpha)
                ctx.lineWidth = widths[w]
                ctx.lineCap = "round"
                ctx.lineJoin = "round"
                ctx.beginPath()
                ctx.moveTo(pts[0][0], pts[0][1])
                for (var q = 1; q < pts.length; ++q) ctx.lineTo(pts[q][0], pts[q][1])
                ctx.stroke()
            }
        }
        for (var i = 0; i < particles.length; ++i) {
            var p = particles[i], pu = p.age / p.life
            var c = Qt.color(p.color)
            ctx.strokeStyle = rgba(c, 1 - pu)
            ctx.lineWidth = p.size * (1 - 0.5 * pu)
            ctx.lineCap = "round"
            ctx.beginPath()
            ctx.moveTo(p.x, p.y)
            ctx.lineTo(p.x - p.vx * 0.03, p.y - p.vy * 0.03)
            ctx.stroke()
        }
    }
}
