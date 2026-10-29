import QtQuick
import QtQuick.Controls
import QtQuick.Effects
import "Format.js" as Format
import "Heat.js" as Heat

// DESIGN 2c: a leaderboard board — the board's live value and title, the sub-gauge (智力 · 思考 · 費用), the ranking of
// agent groups by live speed and the footer, in one of three styles (glass 流光玻璃, timing 計時塔, neon 霓虹座艙).
Item {
    id: board
    property string boardId
    property var spec: ({})
    property var view: ({})

    readonly property string style: spec.style || "neon"
    readonly property string size: spec.size || "M"
    readonly property int side: size === "S" ? 280 : size === "L" ? 420 : 340
    readonly property bool dark: Theme.dark
    readonly property var readouts: spec.readouts || ({})
    readonly property bool showIntel: readouts.intelligence !== false
    readonly property bool showThinking: readouts.thinking !== false
    readonly property bool showCost: readouts.cost !== false
    readonly property bool metrics: showIntel || showThinking || showCost
    readonly property real heat: view.heat || 0
    readonly property color heatColor: Heat.color(heat)
    readonly property bool hot: heat > 0.02
    readonly property string effects: prefs.effects

    // Style tokens (the prototypes' palettes).
    readonly property real radius: style === "glass" ? 24 : style === "timing" ? 14 : 22
    readonly property real pad: size === "S" ? (style === "timing" ? 12 : 14) : size === "L" ? (style === "timing" ? 16 : 18) : (style === "timing" ? 14 : 16)
    readonly property real heroHeight: style === "glass" ? (size === "S" ? 78 : size === "L" ? 94 : 86)
                                     : style === "timing" ? (size === "S" ? 72 : size === "L" ? 88 : 80)
                                     : (size === "S" ? 92 : size === "L" ? 108 : 100)
    readonly property real bigSize: style === "timing" ? (size === "S" ? 30 : size === "L" ? 42 : 36) : (size === "S" ? 34 : size === "L" ? 46 : 40)
    readonly property real rowHeight: size === "L" ? 32 : 30
    readonly property real subHeight: style === "timing" ? (size === "L" ? 22 : 21) : (size === "L" ? 23 : 22)
    readonly property bool twoLines: size !== "S"
    readonly property color deck: style === "glass" ? (dark ? Qt.rgba(20 / 255, 22 / 255, 33 / 255, 0.78) : Qt.rgba(252 / 255, 252 / 255, 1, 0.84))
                                : style === "timing" ? (dark ? "#0c0e14" : "#fbfbfd")
                                : (dark ? "#05060c" : "#f7f8fc")
    readonly property color ink: style === "neon" ? (dark ? "#f3f6ff" : "#0a0d18") : style === "timing" ? (dark ? "#f5f7fb" : "#0e1118")
                               : (dark ? Qt.rgba(244 / 255, 246 / 255, 252 / 255, 0.96) : Qt.rgba(17 / 255, 20 / 255, 30 / 255, 0.94))
    readonly property color ink2: dark ? Qt.rgba(226 / 255, 230 / 255, 242 / 255, 0.62) : Qt.rgba(30 / 255, 36 / 255, 52 / 255, 0.6)
    readonly property color ink3: dark ? Qt.rgba(226 / 255, 230 / 255, 242 / 255, 0.38) : Qt.rgba(30 / 255, 36 / 255, 52 / 255, 0.38)
    readonly property color line: style === "neon" ? (dark ? Qt.rgba(140 / 255, 170 / 255, 1, 0.10) : Qt.rgba(20 / 255, 30 / 255, 80 / 255, 0.08))
                                : (dark ? Qt.rgba(1, 1, 1, 0.08) : Qt.rgba(0, 0, 0, 0.07))
    readonly property color fill: dark ? Qt.rgba(1, 1, 1, 0.055) : Qt.rgba(0, 0, 0, 0.045)
    readonly property color unlit: style === "neon" ? (dark ? Qt.rgba(140 / 255, 170 / 255, 1, 0.16) : Qt.rgba(20 / 255, 30 / 255, 80 / 255, 0.14))
                                 : (dark ? Qt.rgba(1, 1, 1, 0.16) : Qt.rgba(0, 0, 0, 0.14))
    readonly property string numFont: style === "glass" ? Qt.application.font.family : "monospace"
    readonly property color led: dark ? "#EAF2FF" : "#141A33"

    // Clips a canvas over the pane to its rounded corners (an Item's clip is rectangular).
    function clipToPane(ctx, w, h) {
        var r = radius
        ctx.beginPath()
        ctx.moveTo(r, 0)
        ctx.arcTo(w, 0, w, h, r)
        ctx.arcTo(w, h, 0, h, r)
        ctx.arcTo(0, h, 0, 0, r)
        ctx.arcTo(0, 0, w, 0, r)
        ctx.closePath()
        ctx.clip()
    }

    // DESIGN 12 Number formats: a row's 性能 cell is measured with `8,888` in its font (two-line group rows at 13).
    FontMetrics { id: perfOneLine; font.family: board.numFont; font.pixelSize: 11; font.weight: Font.Medium }
    FontMetrics { id: perfTwoLine; font.family: board.numFont; font.pixelSize: 13; font.weight: Font.Medium }
    function perfColumn(twoLine) { return Math.ceil((twoLine ? perfTwoLine : perfOneLine).advanceWidth("8,888")) }

    // DESIGN 12 Score card: the tooltip of a group or sub row while 性能 is on, and of the hero's 性能 (its supplying
    // agent). 0.35 s of hover shows it beside the board on the side with more room; it hides 0.15 s after the pointer
    // leaves; another row switches at once.
    ScoreCard { id: boardCard; visible: false }
    property string cardKey: ""
    property Item cardItem: null
    Timer { id: cardShow; interval: 350; onTriggered: board.showCard() }
    Timer { id: cardHide; interval: 150; onTriggered: { boardCard.visible = false; board.cardKey = "" } }
    function cardInfo(key) {
        if (key === "hero") return board.view.heroCard || null
        const groups = board.view.groups || []
        for (let i = 0; i < groups.length; ++i) {
            if (groups[i].id === key) return groups[i].card || null
            const subs = groups[i].subs || []
            for (let j = 0; j < subs.length; ++j)
                if (groups[i].id + "/" + subs[j].id === key) return subs[j].card || null
        }
        return null
    }
    function cardHover(item, key, hovered) {
        if (hovered) {
            cardItem = item
            cardKey = key
            cardHide.stop()
            if (boardCard.visible) showCard()
            else cardShow.restart()
        } else if (cardKey === key) {
            cardShow.stop()
            if (boardCard.visible) cardHide.restart()
            else cardKey = ""
        }
    }
    function showCard() {
        const info = cardInfo(cardKey)
        const win = board.Window.window
        if (!info || !cardItem || !win) return
        const changed = boardCard.info !== info && boardCard.info.project !== info.project
        boardCard.info = info
        const origin = board.mapToGlobal(0, 0)
        const rowTop = cardItem.mapToGlobal(0, 0).y
        const scr = win.screen
        boardCard.availableHeight = scr ? scr.desktopAvailableHeight : 10000
        const room = scr ? scr.virtualX + scr.width - (origin.x + board.width) : 1000
        const left = scr ? origin.x - scr.virtualX : 0
        boardCard.x = room >= left ? origin.x + board.width + 8 - boardCard.pad : origin.x - 8 - 320 - boardCard.pad
        let y = rowTop - boardCard.pad
        if (scr) y = Math.max(scr.virtualY + 8 - boardCard.pad, Math.min(y, scr.virtualY + scr.desktopAvailableHeight - 8 - boardCard.height + boardCard.pad))
        boardCard.y = y
        boardCard.visible = true
        if (changed) boardCard.fit()
    }
    // The open card updates with the engine tick (see onViewChanged below).
    function refreshCard() {
        if (!boardCard.visible) return
        const info = cardInfo(cardKey)
        if (info) boardCard.info = info
        else boardCard.visible = false
    }

    function titleText(level) {
        themes.rev
        var t = themes.title(level)
        return S.language === "en" || style !== "glass" ? t.toUpperCase() : t
    }

    Accessible.role: Accessible.List
    Accessible.name: Format.fill(S.tr2(S.rev, "a11y.board"), [spec.sources === "all" || !spec.sources ? S.tr2(S.rev, "orbs.sources.all") : "", view.liveText || "0", titleText(view.titleLevel || 0)])

    // The bloom that grows with heat and fades out along the rounded outline before the window's edge.
    Rectangle {
        id: bloomSource
        x: 40
        y: 40
        width: board.side
        height: board.side
        radius: board.radius
        color: board.heatColor
        visible: false
        layer.enabled: true
    }
    MultiEffect {
        anchors.fill: bloomSource
        source: bloomSource
        visible: board.effects !== "off"
        blurEnabled: true
        blurMax: 48
        blur: 1
        opacity: Math.min(0.85, 0.15 + 0.6 * board.heat) * (board.dark ? 0.55 : 0.35)
        autoPaddingEnabled: true
        Behavior on opacity { NumberAnimation { duration: 1000 } }
    }

    // The board's square.
    Rectangle {
        id: square
        x: 40
        y: 40
        width: board.side
        height: board.side
        radius: board.radius
        color: board.deck
        clip: true
        border.width: board.style === "glass" ? 1 : board.style === "timing" ? 1 : 0
        border.color: board.style === "glass" ? (board.dark ? Qt.rgba(1, 1, 1, 0.13) : Qt.rgba(0, 0, 0, 0.08)) : (board.dark ? Qt.rgba(1, 1, 1, 0.09) : Qt.rgba(0, 0, 0, 0.08))
        Behavior on width { NumberAnimation { duration: 450; easing.type: Easing.OutCubic } }

        // Style ambience: glass a heat-tinted glow behind the hero, timing a heat rail along the top, neon a grid.
        Rectangle {
            visible: board.style === "glass"
            anchors.fill: parent
            radius: board.radius
            gradient: Gradient {
                GradientStop { position: 0; color: Qt.rgba(board.heatColor.r, board.heatColor.g, board.heatColor.b, (board.dark ? 0.22 : 0.14) * (0.25 + board.heat)) }
                GradientStop { position: 0.45; color: "transparent" }
            }
        }
        Canvas {
            visible: board.style === "neon"
            anchors.fill: parent
            onPaint: {
                var ctx = getContext("2d")
                ctx.reset()
                board.clipToPane(ctx, width, height)
                ctx.strokeStyle = board.dark ? "rgba(120,150,255,0.05)" : "rgba(20,30,80,0.04)"
                ctx.lineWidth = 1
                for (var x = 0.5; x < width; x += 22) { ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, height); ctx.stroke() }
                for (var y = 0.5; y < height; y += 22) { ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(width, y); ctx.stroke() }
            }
            Component.onCompleted: requestPaint()
        }
        // Neon: hyperspace streaks behind the board from h = 0.2 (count 20 → 90 and speed with heat).
        Canvas {
            id: warp
            visible: board.style === "neon" && board.heat >= 0.2 && board.effects !== "off" && board.effects !== "soft"
            anchors.fill: parent
            property real phase: 0
            NumberAnimation on phase {
                running: warp.visible
                from: 0
                to: 1000
                duration: 1000000
                loops: Animation.Infinite
            }
            onPhaseChanged: requestPaint()
            onPaint: {
                var ctx = getContext("2d")
                ctx.reset()
                board.clipToPane(ctx, width, height)
                var h = board.heat
                var count = Math.round((20 + 70 * Math.min(1, (h - 0.2) / 0.8)) * (board.effects === "max" ? 1.5 : 1))
                var speed = 0.15 + 0.6 * h
                var cx = width / 2, cy = height / 2, reach = Math.sqrt(cx * cx + cy * cy)
                var c = board.heatColor
                for (var i = 0; i < count; ++i) {
                    var seed = Math.sin(i * 12.9898) * 43758.5453
                    var angle = (seed - Math.floor(seed)) * Math.PI * 2
                    var offset = Math.cos(i * 78.233) * 0.5 + 0.5
                    var u = ((phase * speed + offset) % 1)
                    var r0 = reach * u * u, r1 = r0 + 6 + 30 * u * h
                    ctx.strokeStyle = Qt.rgba(c.r, c.g, c.b, (board.dark ? 0.35 : 0.2) * u)
                    ctx.lineWidth = 0.6 + u
                    ctx.beginPath()
                    ctx.moveTo(cx + Math.cos(angle) * r0, cy + Math.sin(angle) * r0)
                    ctx.lineTo(cx + Math.cos(angle) * r1, cy + Math.sin(angle) * r1)
                    ctx.stroke()
                }
            }
        }
        Rectangle {
            visible: board.style === "neon"
            anchors.fill: parent
            radius: board.radius
            gradient: Gradient {
                orientation: Gradient.Horizontal
                GradientStop { position: 0.5; color: "transparent" }
                GradientStop { position: 1; color: Qt.rgba(board.heatColor.r, board.heatColor.g, board.heatColor.b, 0.22 * board.heat) }
            }
            opacity: 0.8
        }
        // Timing: the heat rail, teal → indigo → rose → gold with its glow, stretched to the heat and clipped to the
        // rounded top corners.
        Canvas {
            visible: board.style === "timing"
            width: parent.width
            height: 3 + 16
            property real stretch: board.heat
            Behavior on stretch { NumberAnimation { duration: 600; easing.type: Easing.OutCubic } }
            readonly property bool dark: board.dark
            onStretchChanged: requestPaint()
            onDarkChanged: requestPaint()
            onWidthChanged: requestPaint()
            onVisibleChanged: if (visible) requestPaint()
            onPaint: {
                var ctx = getContext("2d")
                ctx.reset()
                var len = width * Math.max(0, Math.min(1, stretch))
                if (len < 0.5) return
                board.clipToPane(ctx, width, board.side)
                ctx.fillStyle = board.line
                ctx.fillRect(0, 0, len, 3)
                var g = ctx.createLinearGradient(0, 0, len, 0)
                g.addColorStop(0, "#2DD4BF")
                g.addColorStop(0.4, "#818CF8")
                g.addColorStop(0.75, "#FB7185")
                g.addColorStop(1, "#FACC15")
                var glow = Heat.color(Math.max(0.1, board.heat))
                ctx.shadowColor = Qt.rgba(glow.r, glow.g, glow.b, 0.7)
                ctx.shadowBlur = 10
                ctx.fillStyle = g
                ctx.fillRect(0, 0, len, 3)
            }
        }

        // ---- Hero
        Item {
            id: hero
            x: board.pad
            y: board.style === "timing" ? board.pad : board.pad - 2
            width: board.side - 2 * board.pad
            height: board.heroHeight
            Column {
                id: heroLeft
                width: hero.width - (board.metrics ? subGauge.width + 8 : 0)
                spacing: board.style === "neon" ? 1 : 3
                Row {
                    spacing: 6
                    Item {
                        id: number
                        width: bigValue.implicitWidth
                        height: bigValue.implicitHeight
                        transform: Scale {
                            id: punchScale
                            origin.x: number.width / 2
                            origin.y: number.height / 2
                        }
                        // Neon from h = 0.85: chromatic fringes on the number.
                        Repeater {
                            model: board.style === "neon" && board.heat >= 0.85 && board.effects !== "off" ? [[-1.2, "#3CD2FF"], [1.2, "#FF3C8C"]] : []
                            delegate: Text {
                                required property var modelData
                                x: bigValue.x + modelData[0]
                                text: bigValue.text
                                color: modelData[1]
                                opacity: 0.45 * Math.min(1, (board.heat - 0.85) / 0.15 + 0.3)
                                font: bigValue.font
                                topPadding: bigValue.topPadding
                                bottomPadding: bigValue.bottomPadding
                            }
                        }
                        // Neon: the digits glow in the heat color (a blurred copy behind them).
                        Text {
                            anchors.fill: bigValue
                            visible: board.style === "neon" && board.effects !== "off"
                            text: bigValue.text
                            color: board.heatColor
                            font: bigValue.font
                            topPadding: bigValue.topPadding
                            bottomPadding: bigValue.bottomPadding
                            layer.enabled: visible
                            layer.effect: MultiEffect {
                                blurEnabled: true
                                blur: 0.7
                                blurMax: 24
                                autoPaddingEnabled: true
                            }
                            opacity: board.dark ? 0.9 : 0.35
                        }
                        Text {
                            id: bigValue
                            text: board.view.liveText || "0"
                            color: board.style === "neon" && board.dark ? "#FFFDF5" : board.ink
                            font.pixelSize: Math.round(board.bigSize)
                            font.weight: Font.Bold
                            font.family: board.numFont
                            font.features: Theme.tabular
                            font.letterSpacing: -board.bigSize * 0.03
                            topPadding: -Math.round(board.bigSize * 0.12)
                            bottomPadding: -Math.round(board.bigSize * 0.1)
                        }
                        // A new peak sweeps a gold sheen across the digits.
                        Item {
                            id: sheen
                            property real at: -1
                            visible: at >= 0
                            x: -24 + (number.width + 48) * Math.max(0, at)
                            width: 22
                            height: number.height
                            clip: true
                            Text {
                                x: -sheen.x
                                text: bigValue.text
                                color: "#FACC15"
                                font: bigValue.font
                                topPadding: bigValue.topPadding
                                bottomPadding: bigValue.bottomPadding
                            }
                            NumberAnimation on at {
                                id: sheenAnimation
                                running: false
                                from: 0
                                to: 1
                                duration: 700
                                onFinished: sheen.at = -1
                            }
                        }
                        // … and pops boards.newPeak above them for 2.2 s.
                        Text {
                            id: peakPop
                            anchors.horizontalCenter: parent.horizontalCenter
                            anchors.bottom: parent.top
                            text: S.tr2(S.rev, "boards.newPeak")
                            color: board.dark ? "#FACC15" : "#B8860B"
                            font.pixelSize: 10
                            font.weight: Font.Bold
                            font.letterSpacing: 0.8
                            opacity: 0
                            SequentialAnimation {
                                id: popAnimation
                                NumberAnimation { target: peakPop; property: "opacity"; to: 1; duration: 200 }
                                PauseAnimation { duration: 1700 }
                                NumberAnimation { target: peakPop; property: "opacity"; to: 0; duration: 300 }
                            }
                        }
                        SequentialAnimation {
                            id: punchAnimation
                            NumberAnimation { target: punchScale; properties: "xScale,yScale"; to: 1.06; duration: 120; easing.type: Easing.OutCubic }
                            NumberAnimation { target: punchScale; properties: "xScale,yScale"; to: 1; duration: 280; easing.type: Easing.OutBack }
                        }
                    }
                    Text {
                        anchors.bottom: parent.bottom
                        anchors.bottomMargin: 2
                        text: board.style === "glass" ? S.tr2(S.rev, "unit.tokS") : S.tr2(S.rev, "unit.tokS").toUpperCase()
                        color: board.style === "glass" ? board.ink2 : board.heatColor
                        font.pixelSize: Math.round(board.style === "glass" ? 13 : 10.5)
                        font.weight: board.style === "glass" ? Font.Medium : Font.Bold
                        font.family: board.numFont
                        font.letterSpacing: board.style === "glass" ? 0 : 1.4
                    }
                }
                Text {
                    width: parent.width
                    text: board.titleText(board.view.titleLevel || 0)
                    color: (board.view.titleLevel || 0) > 0 ? board.heatColor : board.ink3
                    font.pixelSize: 12
                    font.weight: Font.Bold
                    font.letterSpacing: board.style === "glass" ? 0.4 : 0.8
                    elide: Text.ElideRight
                    Behavior on color { ColorAnimation { duration: 300 } }
                }
                Text {
                    width: parent.width
                    text: Format.fill(S.tr2(S.rev, "agents.context"), [board.view.generating || 0, board.view.tools || 0])
                    color: board.ink3
                    font.pixelSize: Math.round(10.5)
                    font.family: board.numFont
                    elide: Text.ElideRight
                }
            }
            // The neon LED bar: 24 segments lit to the heat, today's peak marked.
            Row {
                visible: board.style === "neon"
                anchors.left: parent.left
                anchors.right: parent.right
                anchors.bottom: parent.bottom
                anchors.bottomMargin: 3
                anchors.rightMargin: board.metrics ? subGauge.width + 8 : 0
                height: 7
                spacing: 2
                readonly property int lit: Math.round(board.heat * 24)
                readonly property int peakIndex: Math.round(Heat.heatOf(board.view.peak || 0, prefs.redline) * 24) - 1
                Repeater {
                    model: 24
                    Rectangle {
                        required property int index
                        width: (parent.width - 23 * 2) / 24
                        height: 7
                        radius: 1.5
                        color: index === parent.peakIndex ? "#FFF7D6" : index < parent.lit ? Heat.color((index + 1) / 24) : board.unlit
                        Behavior on color { ColorAnimation { duration: 250 } }
                    }
                }
            }
            // The glass sparkline / the timing bars: here when there is no sub-gauge, else the strip under the hero.
            HistoryStrip {
                visible: board.style !== "neon" && !board.metrics
                anchors.right: parent.right
                anchors.verticalCenter: parent.verticalCenter
                width: hero.width * 0.42
                height: hero.height * 0.55
            }

            // ---- Sub-gauge (性能 · 思考 · 費用), right-aligned in the hero.
            Column {
                id: subGauge
                visible: board.metrics
                anchors.right: parent.right
                anchors.verticalCenter: parent.verticalCenter
                anchors.verticalCenterOffset: board.style === "neon" ? -8 : 0
                width: Math.max(board.size === "S" ? 92 : board.size === "L" ? 128 : 112, table.implicitWidth)
                spacing: 3
                readonly property var intel: board.view.intel || ({ shown: -1, form: "none" })
                // DESIGN 12 Motion: the shown integer counts to each new value; the arc's end and the meter follow it.
                property real shownValue: intel.shown === undefined ? -1 : intel.shown
                readonly property real target: intel.shown === undefined ? -1 : intel.shown
                onTargetChanged: {
                    const from = Math.round(shownValue)
                    if (Format.perfCounts(from, target)) {
                        heroCount.stop()
                        heroCount.from = from
                        heroCount.to = target
                        heroCount.duration = Format.perfCountMs(from, target)
                        heroCount.start()
                    } else {
                        heroCount.stop()
                        shownValue = target
                    }
                }
                NumberAnimation { id: heroCount; target: subGauge; property: "shownValue"; easing.type: Easing.OutCubic }
                readonly property bool scored: shownValue >= 0
                readonly property bool provisional: intel.provisional === true
                // Glass's dial shows the compact form; neon and timing the full form.
                readonly property string shownText: scored ? (board.style === "glass" ? Format.perfCompact(Math.round(shownValue)) : Format.perfFull(Math.round(shownValue))) : "–"
                readonly property real shownScale: scored ? Format.perfScale(shownValue) : 0
                // Glass: the pearl arc around 性能. Neon: LED digits and a 12-segment meter. Timing: the 性能 row.
                Item {
                    visible: board.showIntel
                    HoverHandler { onHoveredChanged: board.cardHover(parent, "hero", hovered) }
                    width: parent.width
                    height: board.style === "glass" ? (board.size === "S" ? 54 : board.size === "L" ? 74 : 66) * 0.742
                          : board.style === "neon" ? neonIntel.implicitHeight + 2 : (board.size === "S" ? 21 : board.size === "L" ? 27 : 24)
                    Canvas {
                        id: arc
                        visible: board.style === "glass"
                        readonly property real d: board.size === "S" ? 54 : board.size === "L" ? 74 : 66
                        width: d
                        height: d * 0.742
                        anchors.horizontalCenter: parent.horizontalCenter
                        readonly property real fraction: subGauge.shownScale
                        readonly property bool dotted: subGauge.provisional
                        onFractionChanged: requestPaint()
                        onDottedChanged: requestPaint()
                        onVisibleChanged: requestPaint()
                        onPaint: {
                            var ctx = getContext("2d")
                            ctx.reset()
                            var r = 0.439 * d, cx = d / 2, cy = d / 2
                            ctx.lineWidth = 0.0455 * d
                            ctx.lineCap = "round"
                            ctx.strokeStyle = board.dark ? "rgba(255,255,255,0.14)" : "rgba(0,0,0,0.11)"
                            ctx.beginPath()
                            ctx.arc(cx, cy, r, Math.PI * 150 / 180, Math.PI * 390 / 180, false)
                            ctx.stroke()
                            if (!subGauge.scored) return
                            var g = ctx.createLinearGradient(0, height, width, 0)
                            g.addColorStop(0, board.dark ? "#A5F3FC" : "#1AA3C4")
                            g.addColorStop(0.5, board.dark ? "#C7D2FE" : "#6D7CF0")
                            g.addColorStop(1, board.dark ? "#F5D0FE" : "#C05BD9")
                            ctx.strokeStyle = g
                            ctx.globalAlpha = subGauge.intel.form === "resting" ? 0.35 : 1
                            // A provisional arc: round dots (dash 0, gap 2 × the stroke) with a hollow tip ring.
                            if (dotted) ctx.setLineDash([0, 2 * ctx.lineWidth])
                            ctx.beginPath()
                            var end = Math.PI * (150 + 240 * fraction) / 180
                            ctx.arc(cx, cy, r, Math.PI * 150 / 180, end, false)
                            ctx.stroke()
                            ctx.setLineDash([])
                            var tip = board.dark ? "white" : "#C05BD9"
                            ctx.beginPath()
                            ctx.arc(cx + r * Math.cos(end), cy + r * Math.sin(end), 0.035 * d, 0, Math.PI * 2)
                            if (dotted) {
                                ctx.strokeStyle = tip
                                ctx.lineWidth = 1
                                ctx.stroke()
                            } else {
                                ctx.fillStyle = tip
                                ctx.fill()
                            }
                        }
                    }
                    Column {
                        id: neonIntel
                        visible: board.style !== "timing"
                        anchors.horizontalCenter: parent.horizontalCenter
                        y: board.style === "glass" ? arc.height * 0.18 : 0
                        spacing: 0
                        Text {
                            anchors.horizontalCenter: parent.horizontalCenter
                            text: S.tr2(S.rev, "intel.glyph")
                            color: board.ink3
                            font.pixelSize: Math.round(board.size === "S" ? 7 : 8)
                            font.weight: Font.DemiBold
                            font.letterSpacing: 1
                        }
                        Text {
                            id: heroNumber
                            anchors.horizontalCenter: parent.horizontalCenter
                            text: subGauge.shownText
                            color: subGauge.scored && subGauge.intel.form === "live" ? (board.style === "neon" ? board.led : board.ink) : board.ink3
                            font.pixelSize: Math.round(board.style === "neon" ? (board.size === "S" ? 21 : board.size === "L" ? 28 : 24) : (board.size === "S" ? 16 : board.size === "L" ? 21 : 19))
                            font.weight: board.style === "neon" ? Font.Bold : Font.DemiBold
                            font.family: board.numFont
                            font.features: Theme.tabular
                            Behavior on color { ColorAnimation { duration: 200 } }
                            ProvisionalDots {
                                y: heroNumber.baselineOffset + 0.22 * heroNumber.font.pixelSize - height / 2
                                textWidth: heroNumber.advance.width > 0 ? heroNumber.advance.width : heroNumber.implicitWidth
                                pixelSize: heroNumber.font.pixelSize
                                color: heroNumber.color
                                shown: subGauge.provisional && subGauge.scored
                            }
                        }
                        Row {
                            visible: board.style === "neon"
                            anchors.horizontalCenter: parent.horizontalCenter
                            spacing: 2
                            readonly property int lit: Math.round(subGauge.shownScale * 12)
                            Repeater {
                                model: 12
                                Rectangle {
                                    required property int index
                                    readonly property bool on: index < parent.lit
                                    readonly property color segment: board.dark ? "#CFE0FF" : "#3D4F9A"
                                    width: ((board.size === "S" ? 62 : board.size === "L" ? 84 : 74) - 22) / 12
                                    height: 3
                                    radius: 1
                                    // A provisional meter's lit segments are hollow (a 1 pt inner stroke).
                                    color: on ? (subGauge.provisional ? "transparent" : segment) : board.unlit
                                    border.width: on && subGauge.provisional ? 1 : 0
                                    border.color: segment
                                    opacity: on && subGauge.intel.form === "resting" ? 0.35 : 1
                                }
                            }
                        }
                    }
                    // Timing: the 性能 tab of the data box.
                    Rectangle {
                        visible: board.style === "timing"
                        anchors.fill: parent
                        radius: 5
                        color: board.dark ? "#12151D" : "#F1F3F7"
                        border.width: 1
                        border.color: board.dark ? Qt.rgba(1, 1, 1, 0.1) : Qt.rgba(0, 0, 0, 0.1)
                        Rectangle {
                            width: intelTab.implicitWidth + 12
                            height: parent.height
                            radius: 5
                            color: board.dark ? "#1B1F2A" : "#E6E9F0"
                            Text {
                                id: intelTab
                                anchors.centerIn: parent
                                text: S.tr2(S.rev, "intel.glyph").toUpperCase()
                                color: board.dark ? "#E8ECF5" : "#1A1E28"
                                font.pixelSize: 9
                                font.weight: Font.Bold
                            }
                        }
                        Text {
                            id: timingNumber
                            anchors.right: parent.right
                            anchors.rightMargin: 7
                            anchors.verticalCenter: parent.verticalCenter
                            text: subGauge.shownText
                            color: subGauge.scored && subGauge.intel.form === "live" ? board.ink : board.ink3
                            font.pixelSize: Math.round(board.size === "S" ? 15 : board.size === "L" ? 19 : 17)
                            font.weight: Font.Black
                            font.family: "monospace"
                            font.features: Theme.tabular
                            ProvisionalDots {
                                y: timingNumber.baselineOffset + 0.22 * timingNumber.font.pixelSize - height / 2
                                textWidth: timingNumber.advance.width > 0 ? timingNumber.advance.width : timingNumber.implicitWidth
                                pixelSize: timingNumber.font.pixelSize
                                color: timingNumber.color
                                shown: subGauge.provisional && subGauge.scored
                            }
                        }
                    }
                }
                // The table: 思考 and 費用 on fixed cells, so the numbers never move it.
                Grid {
                    id: table
                    anchors.horizontalCenter: parent.horizontalCenter
                    columns: 3
                    columnSpacing: 3
                    rowSpacing: 2
                    readonly property real numberSize: board.size === "S" ? 9.5 : board.size === "L" ? 12 : 11
                    readonly property bool thinkingShown: board.showThinking && (!(board.showIntel || board.showCost) || (board.view.thinking || 0) >= 0.05)
                    Text {
                        visible: table.thinkingShown
                        text: S.tr2(S.rev, "intel.thinking.glyph")
                        color: board.ink3
                        font.pixelSize: Math.round(table.numberSize - 2)
                        font.weight: Font.DemiBold
                    }
                    Text {
                        visible: table.thinkingShown
                        horizontalAlignment: Text.AlignRight
                        text: (board.view.thinking || 0) > 0 ? board.view.thinkingText : "–"
                        color: (board.view.thinking || 0) > 0 ? (board.style === "neon" ? board.led : board.ink) : board.ink3
                        font.pixelSize: Math.round(table.numberSize)
                        font.weight: Font.DemiBold
                        font.family: board.numFont
                        font.features: Theme.tabular
                    }
                    Text {
                        visible: table.thinkingShown
                        text: S.tr2(S.rev, "unit.tokS")
                        color: board.ink3
                        font.pixelSize: Math.round(table.numberSize - 2.5)
                    }
                    Text {
                        visible: board.showCost
                        text: S.tr2(S.rev, "cost.glyph")
                        color: board.ink3
                        font.pixelSize: Math.round(table.numberSize - 2)
                        font.weight: Font.DemiBold
                    }
                    Text {
                        visible: board.showCost
                        horizontalAlignment: Text.AlignRight
                        text: board.view.costSubscription ? S.tr2(S.rev, "cost.subscription.tag")
                              : board.view.costUnknown ? "–" : (board.view.costPartial ? "≥" : "") + (board.view.costNumber || "0")
                        color: board.view.costSubscription || board.view.costUnknown ? board.ink3 : (board.style === "neon" ? board.led : board.ink)
                        font.pixelSize: Math.round(board.view.costSubscription ? table.numberSize * 0.8 : table.numberSize)
                        font.weight: Font.DemiBold
                        font.family: board.view.costSubscription ? "" : board.numFont
                        font.features: Theme.tabular
                    }
                    Text {
                        visible: board.showCost
                        text: board.view.costSubscription ? "" : (board.view.costUnit || "")
                        color: board.ink3
                        font.pixelSize: Math.round(table.numberSize - 2.5)
                    }
                }
            }
        }

        // The full-width history strip under the hero (glass, timing) while the sub-gauge takes the hero's right side.
        HistoryStrip {
            id: strip
            visible: board.style !== "neon" && board.metrics
            x: board.pad
            y: hero.y + hero.height + (board.style === "timing" ? 8 : 0)
            width: board.side - 2 * board.pad
            height: board.style === "glass" ? (board.size === "S" ? 14 : board.size === "L" ? 18 : 16) : (board.size === "S" ? 10 : 12)
        }

        component HistoryStrip: Item {
            id: historyStrip
            readonly property var values: board.view.history || []
            Canvas {
                id: sparkline
                anchors.fill: parent
                visible: board.style === "glass"
                onVisibleChanged: requestPaint()
                Connections {
                    target: board
                    function onViewChanged() { sparkline.requestPaint() }
                }
                onPaint: {
                    var ctx = getContext("2d")
                    ctx.reset()
                    var v = historyStrip.values
                    if (v.length < 2) return
                    var max = 10
                    for (var i = 0; i < v.length; ++i) max = Math.max(max, v[i])
                    max *= 1.12
                    var step = width / 59
                    var x0 = width - (v.length - 1) * step
                    ctx.beginPath()
                    for (var k = 0; k < v.length; ++k) {
                        var x = x0 + k * step, y = height - 1.5 - (v[k] / max) * (height - 3)
                        if (k === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y)
                    }
                    ctx.strokeStyle = board.heatColor
                    ctx.lineWidth = 1.25
                    ctx.stroke()
                    ctx.lineTo(width, height)
                    ctx.lineTo(x0, height)
                    ctx.closePath()
                    var g = ctx.createLinearGradient(0, 0, 0, height)
                    g.addColorStop(0, Qt.rgba(board.heatColor.r, board.heatColor.g, board.heatColor.b, 0.28))
                    g.addColorStop(1, Qt.rgba(board.heatColor.r, board.heatColor.g, board.heatColor.b, 0))
                    ctx.fillStyle = g
                    ctx.fill()
                }
            }
            Row {
                visible: board.style === "timing"
                anchors.fill: parent
                spacing: (width - 30 * 3) / 29
                readonly property var bars: historyStrip.values.slice(-30)
                readonly property real max: Math.max(10, Math.max.apply(null, bars.length ? bars : [0]))
                Repeater {
                    model: 30
                    Rectangle {
                        required property int index
                        readonly property var v: { var b = parent.bars; var i = index - (30 - b.length); return i >= 0 ? b[i] : 0 }
                        width: 3
                        radius: 1
                        y: parent.height - height
                        height: Math.max(1.5, v / parent.max * parent.height)
                        color: Qt.rgba(board.heatColor.r, board.heatColor.g, board.heatColor.b, 0.85)
                    }
                }
            }
        }

        // ---- Ranking
        Item {
            id: ranking
            x: board.pad - 6
            width: board.side - 2 * board.pad + 12
            y: board.style === "neon" ? hero.y + hero.height + 4
               : board.metrics ? strip.y + strip.height + (board.style === "timing" ? 5 : 0) : hero.y + hero.height + 2
            height: footer.y - y - 4
            clip: true
            readonly property int maxGroups: board.size === "S" ? (board.metrics ? 3 : 4) : board.size === "L" ? 8 : 6
            readonly property real captions: board.showIntel ? (board.style === "timing" ? 14 : 12) : 0
            readonly property real groupHeight: board.twoLines ? board.rowHeight + 8 : board.rowHeight
            // Groups first while they fit; sub rows only in the room that remains; `boards.moreGroups` for the rest.
            readonly property var layout: {
                var groups = board.view.groups || []
                var rows = []
                var y = captions + (captions > 0 ? 2 : 0)
                var room = height
                var kept = 0
                for (var i = 0; i < groups.length && kept < maxGroups; ++i) {
                    if (y + groupHeight > room - (i < groups.length - 1 ? 20 : 0)) break
                    rows.push({ kind: "group", key: groups[i].id, data: groups[i], y: y, h: groupHeight })
                    y += groupHeight + (board.twoLines ? 0 : 2)
                    ++kept
                }
                var rest = groups.slice(kept)
                if (rest.length > 0) {
                    var sum = 0
                    for (var r = 0; r < rest.length; ++r) sum += rest[r].value
                    rows.push({ kind: "more", key: "more-groups", text: Format.fill(S.tr2(S.rev, "boards.moreGroups"), [rest.length, Format.speed(sum)]), y: y, h: 20 })
                    y += 20
                }
                // Sub rows fill the remaining room: #1 two, #2–#3 one at M; three for #1–#3, two for #4–#5 at L.
                var allowance = board.size === "S" ? [] : board.size === "L" ? [3, 3, 3, 2, 2] : [2, 1, 1]
                var out = []
                var shift = 0
                for (var j = 0; j < rows.length; ++j) {
                    var row = rows[j]
                    row.y += shift
                    out.push(row)
                    if (row.kind !== "group") continue
                    var subs = row.data.subs || []
                    var wantSubs = Math.min(subs.length, allowance[out.filter(o => o.kind === "group").length - 1] || 0)
                    for (var s = 0; s < wantSubs; ++s) {
                        if (y + shift + board.subHeight > room) break
                        out.push({ kind: "sub", key: row.data.id + "/" + subs[s].id, data: subs[s], y: row.y + row.h + s * board.subHeight, h: board.subHeight })
                        shift += board.subHeight
                    }
                }
                return out
            }

            // The captions line over the 性能 and value columns.
            Item {
                visible: board.showIntel
                width: parent.width
                height: ranking.captions
                Text {
                    anchors.right: parent.right
                    anchors.rightMargin: 6
                    anchors.verticalCenter: parent.verticalCenter
                    text: S.tr2(S.rev, "unit.tokS")
                    color: board.ink3
                    font.pixelSize: Math.round(board.style === "glass" ? 9 : 8.5)
                    font.weight: Font.DemiBold
                    font.family: board.numFont
                }
                Text {
                    anchors.right: parent.right
                    anchors.rightMargin: 6 + 44 + 8
                    anchors.verticalCenter: parent.verticalCenter
                    text: S.tr2(S.rev, "intel.glyph")
                    color: board.ink3
                    font.pixelSize: Math.round(board.style === "glass" ? 9 : 8.5)
                    font.weight: Font.DemiBold
                }
                Rectangle {
                    anchors.left: parent.left
                    anchors.leftMargin: 6
                    anchors.right: parent.right
                    anchors.rightMargin: 6 + 44 + 8 + board.perfColumn(false) + 6
                    anchors.verticalCenter: parent.verticalCenter
                    height: 1
                    color: board.line
                }
            }

            // Rows stay the same items while their data changes, so they glide to new places instead of reappearing.
            ListModel { id: rowModel }
            property var rowData: ({})
            onLayoutChanged: {
                var index = {}
                for (var i = 0; i < layout.length; ++i) index[layout[i].key] = layout[i]
                for (var j = rowModel.count - 1; j >= 0; --j)
                    if (index[rowModel.get(j).key] === undefined) rowModel.remove(j)
                for (var k = 0; k < layout.length; ++k) {
                    var item = layout[k], found = -1
                    for (var m = 0; m < rowModel.count; ++m) if (rowModel.get(m).key === item.key) { found = m; break }
                    if (found < 0) rowModel.append({ key: item.key, kind: item.kind, rowY: item.y, rowH: item.h })
                    else { rowModel.setProperty(found, "rowY", item.y); rowModel.setProperty(found, "rowH", item.h) }
                }
                rowData = index
            }
            Repeater {
                model: rowModel
                delegate: Item {
                    id: rowItem
                    required property string key
                    required property string kind
                    required property real rowY
                    required property real rowH
                    readonly property var modelData: ranking.rowData[key] || ({ kind: kind, data: {} })
                    readonly property var row: modelData.data || ({})
                    readonly property bool group: kind === "group"
                    width: ranking.width
                    height: rowH
                    y: rowY
                    Behavior on y { SpringAnimation { spring: 4.2; damping: board.style === "timing" ? 0.42 : 0.6; epsilon: 0.2 } }
                    opacity: 0
                    Component.onCompleted: opacity = 1
                    Behavior on opacity { NumberAnimation { duration: 300 } }

                    // The leader's warm plasma tint (neon) / a quiet fill (others).
                    Rectangle {
                        anchors.fill: parent
                        radius: 9
                        visible: rowItem.group && rowItem.row.rank === 1
                        gradient: Gradient {
                            orientation: Gradient.Horizontal
                            GradientStop { position: 0; color: Qt.rgba(250 / 255, 204 / 255, 21 / 255, board.dark ? 0.12 : 0.10) }
                            GradientStop { position: 0.45; color: Qt.rgba(232 / 255, 121 / 255, 249 / 255, 0.05) }
                            GradientStop { position: 0.75; color: "transparent" }
                        }
                    }
                    HoverHandler {
                        id: rowHover
                        onHoveredChanged: if (board.showIntel && rowItem.kind !== "more") board.cardHover(rowItem, rowItem.key, hovered)
                    }
                    Rectangle {
                        anchors.fill: parent
                        radius: 9
                        color: board.fill
                        visible: rowHover.hovered && modelData.kind !== "more"
                    }
                    Text {
                        visible: modelData.kind === "more"
                        x: 18
                        anchors.verticalCenter: parent.verticalCenter
                        text: modelData.text || ""
                        color: board.ink3
                        font.pixelSize: 10
                        font.family: board.numFont
                    }
                    // Rank, phase badge, name, capsule, 智力, value.
                    Text {
                        id: rank
                        visible: rowItem.group
                        x: 6
                        width: 16
                        y: board.twoLines ? 16 - font.pixelSize * 0.8 : (parent.height - height) / 2
                        horizontalAlignment: Text.AlignHCenter
                        text: rowItem.row.rank || ""
                        color: rowItem.row.rank === 1 ? (board.dark ? "#FFE58A" : "#B07800") : board.ink3
                        font.pixelSize: 12
                        font.weight: Font.Bold
                        font.family: board.numFont
                    }
                    Item {
                        id: badge
                        visible: modelData.kind !== "more"
                        x: rowItem.group ? 30 : 30
                        width: 10
                        height: 10
                        y: rowItem.group && board.twoLines ? 16 - 8 : (parent.height - height) / 2
                        readonly property color tint: Theme.sourceColor(rowItem.row.source || "")
                        Rectangle {
                            anchors.centerIn: parent
                            width: rowItem.row.phase === "generating" ? 6 : 4
                            height: width
                            radius: width / 2
                            visible: rowItem.row.phase !== "tools"
                            color: rowItem.row.phase === "generating" ? badge.tint : board.ink3
                            SequentialAnimation on scale {
                                running: rowItem.row.phase === "generating" && board.effects !== "off"
                                loops: Animation.Infinite
                                NumberAnimation { to: 0.65; duration: 500; easing.type: Easing.InOutSine }
                                NumberAnimation { to: 1; duration: 500; easing.type: Easing.InOutSine }
                            }
                        }
                        Rectangle {
                            anchors.centerIn: parent
                            visible: rowItem.row.phase === "tools"
                            width: 7
                            height: 7
                            rotation: 45
                            color: "transparent"
                            border.width: 1.5
                            border.color: badge.tint
                        }
                    }
                    Column {
                        id: nameBlock
                        visible: modelData.kind !== "more"
                        x: rowItem.group ? 48 : 56
                        width: capsule.x - x - 6
                        y: rowItem.group && board.twoLines ? 3 : (parent.height - height) / 2
                        spacing: 1
                        Text {
                            width: parent.width
                            text: rowItem.group ? (rowItem.row.project || "") : (rowItem.row.name || "")
                            color: rowItem.group ? board.ink : board.ink2
                            font.pixelSize: Math.round(rowItem.group ? 12.5 : 11.5)
                            font.weight: rowItem.group ? Font.DemiBold : Font.Medium
                            elide: Text.ElideRight
                        }
                        Text {
                            visible: rowItem.group && board.twoLines
                            width: parent.width + capsule.width
                            text: (rowItem.row.model || "") + ((rowItem.row.subCount || 0) > 0 ? " · " + Format.fill(S.tr2(S.rev, "boards.subCount"), [rowItem.row.subCount]) : "")
                            color: board.ink3
                            font.pixelSize: 10
                            font.family: board.numFont
                            elide: Text.ElideRight
                        }
                    }
                    EffortCapsule {
                        id: capsule
                        visible: modelData.kind !== "more"
                        anchors.right: intelCell.left
                        anchors.rightMargin: 6
                        y: rowItem.group && board.twoLines ? 4 : (parent.height - height) / 2
                        level: rowItem.row.effortLevel === undefined ? -1 : rowItem.row.effortLevel
                        word: rowItem.row.effort || ""
                        topRung: rowItem.row.effortTop || false
                        bolts: rowItem.row.bolts || 0
                        showWord: board.size !== "S" && !(board.style === "timing" && board.size !== "L" && board.showIntel)
                    }
                    PerfCell {
                        id: intelCell
                        visible: board.showIntel && modelData.kind !== "more"
                        width: visible ? board.perfColumn(rowItem.group && board.twoLines) : 0
                        anchors.right: valueText.left
                        anchors.rightMargin: 8
                        anchors.verticalCenter: parent.verticalCenter
                        value: rowItem.row.intel ? rowItem.row.intel.shown : -1
                        form: rowItem.row.intel ? rowItem.row.intel.form : "none"
                        provisional: rowItem.row.intel ? rowItem.row.intel.provisional === true : false
                        pixelSize: rowItem.group && board.twoLines ? 13 : rowItem.group ? 11 : 10.5
                        family: board.numFont
                    }
                    Text {
                        id: valueText
                        visible: modelData.kind !== "more"
                        anchors.right: parent.right
                        anchors.rightMargin: 6
                        anchors.verticalCenter: parent.verticalCenter
                        width: 44
                        horizontalAlignment: Text.AlignRight
                        text: (rowItem.row.value || 0) < 0.05 ? "–" : rowItem.row.valueText || ""
                        color: (rowItem.row.value || 0) < 0.05 ? board.ink3 : rowItem.group ? board.ink : board.ink2
                        font.pixelSize: Math.round(rowItem.group ? (board.twoLines ? 15 : 13) : 11)
                        font.weight: rowItem.group ? Font.Bold : Font.DemiBold
                        font.family: board.numFont
                        font.features: Theme.tabular
                    }
                    // The share of the board's value: a glowing underline (glass, neon) from the name to the value.
                    Rectangle {
                        visible: rowItem.group && board.style !== "timing"
                        x: 40
                        y: parent.height - 1.5
                        height: 1.5
                        radius: 1
                        width: (parent.width - 46) * (rowItem.row.share || 0)
                        color: Heat.color(Heat.heatOf(rowItem.row.value || 0, prefs.redline))
                        opacity: 0.8
                        Behavior on width { NumberAnimation { duration: 550; easing.type: Easing.OutCubic } }
                    }
                    // Overtake: ▲n before the value for 1.6 s.
                    Rectangle {
                        id: chip
                        anchors.right: intelCell.visible ? intelCell.left : valueText.left
                        anchors.rightMargin: 4
                        anchors.verticalCenter: parent.verticalCenter
                        width: chipText.implicitWidth + 10
                        height: 15
                        radius: 4
                        color: "#34D399"
                        opacity: 0
                        Text {
                            id: chipText
                            anchors.centerIn: parent
                            color: "#021a10"
                            font.pixelSize: 10
                            font.weight: Font.Bold
                            font.family: "monospace"
                        }
                        SequentialAnimation {
                            id: chipAnimation
                            NumberAnimation { target: chip; property: "opacity"; to: 1; duration: 220 }
                            PauseAnimation { duration: 980 }
                            NumberAnimation { target: chip; property: "opacity"; to: 0; duration: 400 }
                        }
                        Connections {
                            target: board
                            function onViewChanged() {
                                var events = board.view.events || []
                                for (var i = 0; i < events.length; ++i) {
                                    if (events[i].id === rowItem.row.id && rowItem.group && prefs.effects !== "off") {
                                        chipText.text = "▲" + (events[i].from - events[i].to)
                                        chipAnimation.restart()
                                    }
                                }
                            }
                        }
                    }
                    TapHandler {
                        acceptedButtons: Qt.RightButton
                        onTapped: contextMenu.popup()
                    }
                    ToolTip.visible: rowHover.hovered && rowItem.group && !board.showIntel
                    ToolTip.delay: 350
                    ToolTip.text: rowItem.group ? [rowItem.row.project, rowItem.row.model, rowItem.row.valueText + " " + S.tr2(S.rev, "unit.tokS")].filter(x => x).join("\n") : ""
                    Accessible.role: Accessible.ListItem
                    Accessible.name: rowItem.group ? Format.fill(S.tr2(S.rev, "a11y.board.row"), [rowItem.row.rank, rowItem.row.project, rowItem.row.valueText, rowItem.row.effort || "", rowItem.row.speedMode || "", rowItem.row.subCount]) : ""
                }
            }
        }

        // ---- Footer
        Item {
            id: footer
            x: board.pad
            width: board.side - 2 * board.pad
            height: 28
            y: board.side - height - 2
            Rectangle {
                width: parent.width
                height: 1
                color: board.line
            }
            Row {
                anchors.verticalCenter: parent.verticalCenter
                spacing: 6
                Text {
                    text: Format.fill(S.tr2(S.rev, "boards.today"), [board.view.todayText || "0"])
                    color: board.ink2
                    font.pixelSize: 11
                    font.family: board.numFont
                    font.features: Theme.tabular
                }
                Text {
                    text: "· " + (board.view.costSubscription ? S.tr2(S.rev, "cost.subscription.tag") : (board.view.costTodayText || ""))
                    color: board.ink3
                    font.pixelSize: Math.round(10.5)
                    font.family: board.numFont
                }
            }
            Row {
                anchors.right: parent.right
                anchors.verticalCenter: parent.verticalCenter
                spacing: 6
                Text {
                    text: Format.fill(S.tr2(S.rev, "boards.peak"), [board.view.peakText || "0"])
                    color: board.style === "neon" ? Heat.color(Heat.heatOf(board.view.peak || 0, prefs.redline)) : board.ink2
                    font.pixelSize: 11
                    font.family: board.numFont
                    font.features: Theme.tabular
                }
            }
        }

        // Neon: the breathing tube on the edge, brighter and faster with heat.
        Rectangle {
            visible: board.style === "neon"
            anchors.fill: parent
            radius: board.radius
            color: "transparent"
            border.width: 1.2
            border.color: Qt.lighter(board.heatColor, 1.1)
            opacity: 0.45 + board.heat * 0.55
            SequentialAnimation on opacity {
                running: board.style === "neon" && board.effects !== "off" && board.hot
                loops: Animation.Infinite
                NumberAnimation { to: 0.3 + board.heat * 0.5; duration: Math.max(500, 1600 - board.heat * 1100); easing.type: Easing.InOutSine }
                NumberAnimation { to: 0.45 + board.heat * 0.55; duration: Math.max(500, 1600 - board.heat * 1100); easing.type: Easing.InOutSine }
            }
        }
        // Glass: the top highlight of the pane, following the rounded corners and fading down their curve.
        Canvas {
            visible: board.style === "glass"
            anchors.fill: parent
            readonly property bool dark: board.dark
            onDarkChanged: requestPaint()
            onWidthChanged: requestPaint()
            onVisibleChanged: if (visible) requestPaint()
            onPaint: {
                var ctx = getContext("2d")
                ctx.reset()
                var w = width, r = board.radius, inset = 1.5, alpha = board.dark ? 0.22 : 0.9
                var g = ctx.createLinearGradient(0, inset, 0, r)
                g.addColorStop(0, Qt.rgba(1, 1, 1, alpha))
                g.addColorStop(1, Qt.rgba(1, 1, 1, 0))
                ctx.strokeStyle = g
                ctx.lineWidth = 1
                ctx.beginPath()
                ctx.moveTo(inset, r)
                ctx.arcTo(inset, inset, r, inset, r - inset)
                ctx.lineTo(w - r, inset)
                ctx.arcTo(w - inset, inset, w - inset, r, r - inset)
                ctx.stroke()
            }
        }
    }

    // The perimeter comets: count by effect tier, lap 7 s at idle down to 1.6 s at the redline (30 fps while hot).
    Canvas {
        id: comets
        x: 40
        y: 40
        width: board.side
        height: board.side
        visible: board.effects !== "off" && board.hot
        property real phase: 0
        property real goldUntil: 0
        property real lastSpark: 0
        readonly property bool gold: Date.now() / 1000 < goldUntil
        readonly property int count: { var h = board.heat; var n = h < 0.25 ? 1 : h < 0.55 ? 2 : h < 0.85 ? 3 : 4; return board.effects === "soft" ? 1 : n }
        NumberAnimation on phase {
            running: comets.visible
            from: 0
            to: 1
            loops: Animation.Infinite
            duration: Math.max(1600, 7000 - board.heat * 5400) / (comets.goldUntil > 0 ? 1.8 : 1)
        }
        Timer {
            running: comets.goldUntil > 0
            interval: Math.max(1, (comets.goldUntil - Date.now() / 1000) * 1000)
            onTriggered: comets.goldUntil = 0
        }
        onPhaseChanged: requestPaint()
        onPaint: {
            var ctx = getContext("2d")
            ctx.reset()
            var w = width, h = height, r = board.radius
            var perimeter = 2 * (w + h - 4 * r) + 2 * Math.PI * r
            function point(d) {
                d = ((d % perimeter) + perimeter) % perimeter
                var top = w - 2 * r, right = h - 2 * r, arcLen = Math.PI * r / 2
                if (d < top) return [r + d, 1]
                d -= top
                if (d < arcLen) { var a = -Math.PI / 2 + d / r; return [w - r + (r - 1) * Math.cos(a), r + (r - 1) * Math.sin(a)] }
                d -= arcLen
                if (d < right) return [w - 1, r + d]
                d -= right
                if (d < arcLen) { var b = d / r; return [w - r + (r - 1) * Math.cos(b), h - r + (r - 1) * Math.sin(b)] }
                d -= arcLen
                if (d < top) return [w - r - d, h - 1]
                d -= top
                if (d < arcLen) { var c = Math.PI / 2 + d / r; return [r + (r - 1) * Math.cos(c), h - r + (r - 1) * Math.sin(c)] }
                d -= arcLen
                if (d < right) return [1, h - r - d]
                d -= right
                var e = Math.PI + d / r
                return [r + (r - 1) * Math.cos(e), r + (r - 1) * Math.sin(e)]
            }
            var color = comets.goldUntil > 0 ? Qt.color("#FACC15") : board.heatColor
            // From h = 0.55 a full ring lights around the board.
            if (board.heat >= 0.55) {
                ctx.strokeStyle = Qt.rgba(color.r, color.g, color.b, 0.18 + 0.4 * Math.min(1, (board.heat - 0.55) / 0.45))
                ctx.lineWidth = 1.5
                ctx.beginPath()
                ctx.moveTo(r, 1); ctx.arcTo(w - 1, 1, w - 1, h - 1, r - 1); ctx.arcTo(w - 1, h - 1, 1, h - 1, r - 1)
                ctx.arcTo(1, h - 1, 1, 1, r - 1); ctx.arcTo(1, 1, w - 1, 1, r - 1); ctx.closePath()
                ctx.stroke()
            }
            // At the redline sparks fly outward from each comet head every 120 ms.
            var nowSeconds = Date.now() / 1000
            var sparking = board.heat >= 0.999 && nowSeconds - comets.lastSpark >= 0.12 && board.effects !== "soft"
            if (sparking) comets.lastSpark = nowSeconds
            for (var k = 0; k < count; ++k) {
                var head = (phase + k / count) * perimeter
                for (var t = 0; t < 16; ++t) {
                    var p = point(head - t * 4)
                    var alpha = (1 - t / 16) * (0.35 + 0.65 * board.heat)
                    ctx.fillStyle = Qt.rgba(color.r, color.g, color.b, alpha * 0.5)
                    ctx.beginPath()
                    ctx.arc(p[0], p[1], 2.6 - t * 0.12, 0, Math.PI * 2)
                    ctx.fill()
                }
                var hp = point(head)
                if (sparking) {
                    var ox = hp[0] - w / 2, oy = hp[1] - h / 2, len = Math.max(1, Math.sqrt(ox * ox + oy * oy))
                    for (var sp = 0; sp < 2; ++sp) {
                        boardFx.spark(hp[0] + 40, hp[1] + 40, ox / len * (40 + 50 * Math.random()) + (Math.random() - 0.5) * 30,
                                      oy / len * (40 + 50 * Math.random()) + (Math.random() - 0.5) * 30, 0.35, color, 1, 0, true)
                    }
                    boardFx.start()
                }
                ctx.fillStyle = board.heat > 0.85 ? "white" : Qt.lighter(color, 1.4)
                ctx.beginPath()
                ctx.arc(hp[0], hp[1], 1.8, 0, Math.PI * 2)
                ctx.fill()
            }
        }
    }

    BoardEffects {
        id: boardFx
        x: 0
        y: 0
        width: board.side + 80
        height: board.side + 80
        style: board.style
        level: board.effects
        dark: board.dark
    }

    // Events of each engine tick: a punch on a sharp rise, the new-peak celebration, overtakes and a new leader.
    property real lastLive: -1
    onViewChanged: {
        refreshCard()
        const live = view.live || 0
        if (lastLive >= 0 && live > lastLive * 1.08 + 20 && board.effects !== "off") {
            punchAnimation.restart()
            const p = number.mapToItem(board, number.width / 2, number.height / 2)
            boardFx.punch(p.x, p.y, board.heatColor)
        }
        lastLive = live
        if (view.newPeak && prefs.celebratePeaks && board.effects !== "off") {
            sheenAnimation.restart()
            popAnimation.restart()
            const c = number.mapToItem(board, number.width / 2, number.height / 2)
            boardFx.goldRing(c.x, c.y)
        }
        const events = view.events || []
        for (let i = 0; i < events.length; ++i) {
            const e = events[i]
            let rowY = -1, rowH = 0, passedY = undefined
            for (let k = 0; k < rowModel.count; ++k) {
                const item = rowModel.get(k)
                if (item.key === e.id) { rowY = item.rowY; rowH = item.rowH }
            }
            if (rowY < 0) continue
            const top = ranking.mapToItem(board, 0, rowY)
            if (e.type === "newLeader" && prefs.celebratePeaks) {
                boardFx.newLeader(top.x + 18, top.y + rowH / 2, board.side * 0.95)
                comets.goldUntil = Date.now() / 1000 + 2.2
            } else if (e.type === "overtake") {
                const groups = view.groups || []
                let color = board.heatColor
                for (let g = 0; g < groups.length; ++g) if (groups[g].id === e.id) color = Theme.sourceColor(groups[g].source)
                passedY = top.y + rowH * 1.5
                boardFx.overtake(top.x, top.y, ranking.width, rowH, color, passedY)
            }
        }
    }

    // Drag anywhere to move; the right button opens the context menu.
    MouseArea {
        x: 40
        y: 40
        width: board.side
        height: board.side
        acceptedButtons: Qt.LeftButton | Qt.RightButton
        z: -1
        onPressed: (mouse) => {
            if (mouse.button === Qt.RightButton) contextMenu.popup()
            else boardWindows.startMove(board.boardId)
        }
    }

    Menu {
        id: contextMenu
        MenuItem {
            enabled: false
            text: S.tr2(S.rev, "boards.style." + board.style) + " · " + (board.spec.sources === "all" || !board.spec.sources ? S.tr2(S.rev, "orbs.sources.all") : board.spec.sources.length)
        }
        MenuSeparator {}
        Menu {
            title: S.tr2(S.rev, "boards.style")
            Repeater {
                model: ["glass", "timing", "neon"]
                MenuItem {
                    required property string modelData
                    text: S.tr2(S.rev, "boards.style." + modelData)
                    checkable: true
                    checked: board.style === modelData
                    onTriggered: boardStore.set(board.boardId, "style", modelData)
                }
            }
        }
        Menu {
            title: S.tr2(S.rev, "boards.readouts")
            MenuItem {
                text: S.tr2(S.rev, "boards.readouts.intel")
                checkable: true
                checked: board.showIntel
                onTriggered: boardStore.setReadout(board.boardId, "intelligence", !board.showIntel)
            }
            MenuItem {
                text: S.tr2(S.rev, "boards.readouts.thinking")
                checkable: true
                checked: board.showThinking
                onTriggered: boardStore.setReadout(board.boardId, "thinking", !board.showThinking)
            }
            MenuItem {
                text: S.tr2(S.rev, "boards.readouts.cost")
                checkable: true
                checked: board.showCost
                onTriggered: boardStore.setReadout(board.boardId, "cost", !board.showCost)
            }
        }
        Menu {
            title: S.tr2(S.rev, "menu.size")
            Repeater {
                model: ["S", "M", "L"]
                MenuItem {
                    required property string modelData
                    text: modelData
                    checkable: true
                    checked: board.size === modelData
                    onTriggered: boardStore.set(board.boardId, "size", modelData)
                }
            }
        }
        MenuItem {
            text: S.tr2(S.rev, "menu.alwaysOnTop")
            checkable: true
            checked: board.spec.alwaysOnTop !== false
            onTriggered: boardStore.set(board.boardId, "alwaysOnTop", !(board.spec.alwaysOnTop !== false))
        }
        MenuItem {
            text: S.tr2(S.rev, "menu.clickThrough")
            checkable: true
            checked: board.spec.clickThrough === true
            onTriggered: boardStore.set(board.boardId, "clickThrough", !(board.spec.clickThrough === true))
        }
        MenuSeparator {}
        MenuItem {
            text: S.tr2(S.rev, "boards.add")
            enabled: boardStore.canAdd
            onTriggered: boardStore.add()
        }
        MenuItem {
            text: S.tr2(S.rev, "boards.remove")
            onTriggered: boardStore.remove(board.boardId)
        }
        MenuSeparator {}
        MenuItem {
            text: S.tr2(S.rev, "menu.dashboard")
            onTriggered: boardWindows.dashboardRequested()
        }
        MenuItem {
            text: S.tr2(S.rev, "boards.settings")
            onTriggered: boardWindows.settingsRequested()
        }
    }
}
