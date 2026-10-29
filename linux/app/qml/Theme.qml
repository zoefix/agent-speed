pragma Singleton
import QtQuick

// Panel colors for the system light or dark appearance (DESIGN 0, 4).
QtObject {
    readonly property SystemPalette system: SystemPalette { colorGroup: SystemPalette.Active }
    readonly property bool dark: system.window.hslLightness < 0.5

    readonly property color panel: dark ? "#17191F" : "#FBFBFD"
    readonly property color border: dark ? Qt.rgba(1, 1, 1, 0.08) : Qt.rgba(0, 0, 0, 0.09)
    readonly property color ink: dark ? "#ECEFF4" : "#16181D"
    readonly property color ink2: dark ? Qt.rgba(0.925, 0.937, 0.957, 0.62) : Qt.rgba(0.086, 0.094, 0.114, 0.62)
    readonly property color ink3: dark ? Qt.rgba(0.925, 0.937, 0.957, 0.36) : Qt.rgba(0.086, 0.094, 0.114, 0.38)
    readonly property color hairline: dark ? Qt.rgba(1, 1, 1, 0.07) : Qt.rgba(0, 0, 0, 0.075)
    readonly property color hover: dark ? Qt.rgba(1, 1, 1, 0.05) : Qt.rgba(0, 0, 0, 0.04)
    readonly property color pressed: dark ? Qt.rgba(1, 1, 1, 0.09) : Qt.rgba(0, 0, 0, 0.075)
    readonly property color connector: dark ? Qt.rgba(1, 1, 1, 0.16) : Qt.rgba(0, 0, 0, 0.15)
    readonly property color amber: dark ? "#FBBF24" : "#B45309"
    readonly property color danger: dark ? "#F87171" : "#D92D20"
    readonly property color good: dark ? "#4ADE80" : "#1F9D55"
    readonly property color accent: dark ? "#5B8CFF" : "#2F6BFF"
    readonly property color card: dark ? Qt.rgba(1, 1, 1, 0.035) : "#FFFFFF"
    readonly property color sidebar: dark ? "#13151A" : "#F2F3F7"
    readonly property color field: dark ? Qt.rgba(1, 1, 1, 0.06) : Qt.rgba(0, 0, 0, 0.045)

    // Task score (DESIGN 12): the score ink, its track and the state chips.
    readonly property color scoreInk: dark ? "#93A4D6" : "#4B5FA6"
    readonly property color track: dark ? Qt.rgba(1, 1, 1, 0.09) : Qt.rgba(0, 0, 0, 0.075)
    readonly property color passInk: dark ? "#6EE7B7" : "#047857"
    readonly property color passFill: dark ? Qt.rgba(0.204, 0.827, 0.6, 0.14) : Qt.rgba(0.063, 0.725, 0.506, 0.12)
    readonly property color failInk: dark ? "#FBBF24" : "#B45309"
    readonly property color failFill: dark ? Qt.rgba(0.961, 0.647, 0.141, 0.14) : Qt.rgba(0.961, 0.62, 0.043, 0.14)
    readonly property color cardFill: dark ? "#26272B" : "#FFFFFF"
    readonly property color cardLine: dark ? Qt.rgba(1, 1, 1, 0.12) : Qt.rgba(0, 0, 0, 0.09)
    readonly property color caption: dark ? Qt.rgba(0.922, 0.922, 0.961, 0.56) : Qt.rgba(0.235, 0.235, 0.263, 0.72)
    readonly property color cellFill: dark ? Qt.rgba(1, 1, 1, 0.06) : Qt.rgba(0, 0, 0, 0.05)
    readonly property color bolt: dark ? "#F5A524" : "#B86E00"
    readonly property color capsuleFill: dark ? Qt.rgba(1, 1, 1, 0.07) : Qt.rgba(0, 0, 0, 0.055)
    readonly property color capsuleLine: dark ? Qt.rgba(1, 1, 1, 0.08) : Qt.rgba(0, 0, 0, 0.08)
    readonly property color meterOff: dark ? Qt.rgba(1, 1, 1, 0.18) : Qt.rgba(0, 0, 0, 0.14)

    readonly property var tabular: ({ "tnum": 1 })

    readonly property var sourceColors: ({
        codex: "#34D399", claudeCode: "#FF8A65", claudeDesktop: "#B39DFF", deepseek: "#4D6BFE",
        opencode: "#F4B13E", gemini: "#34B8F0", omp: "#84CC16", antigravity: "#EC4899",
        workbuddy: "#11A68B", workbuddyAi: "#007AB4", zcode: "#466BFF", kimi: "#E16F8A",
        grokBuild: "#8290A6", pi: "#A855F7", openclaw: "#DE2E1F", hermes: "#B8860B", droid: "#9E5F46"
    })

    function sourceColor(id) { return sourceColors[id] || "#7C8798" }

    // Heat colors are tuned for dark grounds; deepen them so text stays readable on a light panel.
    function heatText(c) { return dark ? c : Qt.darker(c, 1.5) }
}
