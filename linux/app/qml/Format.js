.pragma library

// Mirrors format::speed (src/format.cpp).
function speed(v) {
    if (!(v >= 0.05)) return "0"
    if (v < 10) return v.toFixed(1)
    if (v < 10000) return String(Math.round(v))
    return (v / 1000).toFixed(1) + "k"
}

function axis(v) {
    if (v < 1000) return Math.round(v) === v ? String(v) : speed(v)
    var k = Math.round(v / 100) / 10
    return (Math.round(k) === k ? String(k) : k.toFixed(1)) + "k"
}

// Fills %d and %@ placeholders in order, as the string tables use them.
function fill(text, args) {
    var i = 0
    return text.replace(/%[d@]/g, function() { return String(args[i++]) })
}

// DESIGN 12 Number formats of the performance score, from the rounded integer: a comma every three digits.
function grouped(v) {
    var digits = String(Math.abs(Math.round(v)))
    var out = ""
    for (var i = 0; i < digits.length; ++i) {
        if (i > 0 && (digits.length - i) % 3 === 0) out += ","
        out += digits[i]
    }
    return (v < 0 ? "-" : "") + out
}

function perfCompact(v) {
    if (v < 10000) return grouped(v)
    var by = function(d) { return Math.floor((v + d / 2) / d) }
    if (by(100) < 1000) return (by(100) / 10).toFixed(1) + "k"
    if (by(1000) < 1000) return by(1000) + "k"
    if (by(10000) < 1000) return (by(10000) / 100).toFixed(2) + "M"
    if (by(100000) < 1000) return (by(100000) / 10).toFixed(1) + "M"
    return by(1000000) + "M"
}

function perfFull(v) { return v >= 100000 ? perfCompact(v) : grouped(v) }

// DESIGN 12 Log scale: 250 empty, 1000 half, 4000 full.
function perfScale(v) {
    if (!(v > 0) || !isFinite(v)) return 0
    return Math.min(1, Math.max(0, Math.log(v / 250) / Math.LN2 / 4))
}

// DESIGN 12 Motion: a count lasts clamp(0.3 + 0.35 |log2(new / old)|, 0.3, 0.7) s; it runs when the change is at
// least 1 % and 3.
function perfCounts(from, to) { return from >= 0 && to >= 0 && Math.abs(to - from) >= Math.max(3, Math.ceil(from / 100)) }
function perfCountMs(from, to) {
    var ratio = Math.abs(Math.log(Math.max(to, 1) / Math.max(from, 1)) / Math.LN2)
    return Math.round(1000 * Math.min(0.7, Math.max(0.3, 0.3 + 0.35 * ratio)))
}
