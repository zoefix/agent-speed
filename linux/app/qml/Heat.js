.pragma library

// DESIGN 1 heat palette, interpolated in linear RGB between stops.
var stops = [
    [0.00, "#7C8798"], [0.10, "#2DD4BF"], [0.30, "#38BDF8"], [0.48, "#818CF8"],
    [0.64, "#E879F9"], [0.78, "#FB7185"], [0.90, "#FB923C"], [1.00, "#FACC15"]
]

function hexToRgb(hex) {
    return [parseInt(hex.substr(1, 2), 16) / 255, parseInt(hex.substr(3, 2), 16) / 255, parseInt(hex.substr(5, 2), 16) / 255]
}

function toLinear(c) { return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4) }
function toSrgb(c) { return c <= 0.0031308 ? c * 12.92 : 1.055 * Math.pow(c, 1 / 2.4) - 0.055 }

function color(h) {
    h = Math.max(0, Math.min(1, h))
    for (var i = 1; i < stops.length; ++i) {
        if (h <= stops[i][0]) {
            var a = hexToRgb(stops[i - 1][1]), b = hexToRgb(stops[i][1])
            var t = (h - stops[i - 1][0]) / (stops[i][0] - stops[i - 1][0])
            var rgb = [0, 1, 2].map(function(k) { return toSrgb(toLinear(a[k]) * (1 - t) + toLinear(b[k]) * t) })
            return Qt.rgba(rgb[0], rgb[1], rgb[2], 1)
        }
    }
    var last = hexToRgb(stops[stops.length - 1][1])
    return Qt.rgba(last[0], last[1], last[2], 1)
}

function heatOf(v, redline) { return Math.min(1, Math.pow(Math.max(0, v) / redline, 0.6)) }
