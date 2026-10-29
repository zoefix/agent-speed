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
