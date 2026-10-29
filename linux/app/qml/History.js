.pragma library

// Day rows of the history window, aggregated for a source filter and a date range (DESIGN 4b).

function pad(n) { return n < 10 ? "0" + n : String(n) }
function iso(d) { return d.getFullYear() + "-" + pad(d.getMonth() + 1) + "-" + pad(d.getDate()) }
function addDays(d, n) { var c = new Date(d.getFullYear(), d.getMonth(), d.getDate()); c.setDate(c.getDate() + n); return c }
function parse(text) { var p = text.split("-"); return new Date(+p[0], +p[1] - 1, +p[2]) }

// [from, to] ISO dates for a range id; `first` is the oldest date with data.
function bounds(range, first) {
    var today = new Date()
    var t = iso(today)
    switch (range) {
    case "today": return [t, t]
    case "yesterday": var y = iso(addDays(today, -1)); return [y, y]
    case "7d": return [iso(addDays(today, -6)), t]
    case "30d": return [iso(addDays(today, -29)), t]
    case "90d": return [iso(addDays(today, -89)), t]
    case "year": return [today.getFullYear() + "-01-01", t]
    default: return [first || t, t]
    }
}

function dayCount(from, to) { return Math.round((parse(to) - parse(from)) / 86400000) + 1 }

function emptyDay(date) {
    return { date: date, requests: 0, input: 0, cachedInput: 0, cacheWriteInput: 0, output: 0, reasoning: 0, measuredOutput: 0,
             measuredMs: 0, unfinalized: 0, agents: 0, cost: null, unpriced: 0, hasCost: false, peak: null, models: {}, bySource: {} }
}

// Per-day totals between `from` and `to` (inclusive), for one source or all. A day with agent traffic of the selection
// is a day with data even without token usage (DESIGN 10).
function days(rows, source, from, to, traffic, disabled, includeTools) {
    var byDate = {}
    var off = {}
    for (var o = 0; o < (disabled || []).length; ++o) off[disabled[o]] = true
    for (var k = 0; k < (traffic || []).length; ++k) {
        var t = traffic[k]
        if (t.date < from || t.date > to || t.key === "machine" || off[t.key]) continue
        if (source !== "all" && t.key !== source) continue
        if (t.rx + t.tx + (includeTools ? t.rxTools + t.txTools : 0) > 0 && !byDate[t.date]) byDate[t.date] = emptyDay(t.date)
    }
    for (var i = 0; i < rows.length; ++i) {
        var r = rows[i]
        if (r.date < from || r.date > to) continue
        if (source !== "all" && r.source !== source) continue
        var d = byDate[r.date] || (byDate[r.date] = emptyDay(r.date))
        d.requests += r.requests; d.input += r.input; d.cachedInput += r.cachedInput; d.cacheWriteInput += r.cacheWriteInput
        d.output += r.output; d.reasoning += r.reasoning; d.measuredOutput += r.measuredOutput; d.measuredMs += r.measuredMs
        d.unfinalized += r.unfinalized; d.agents += r.agents
        if (r.cost !== undefined && r.cost !== null) { d.cost = (d.cost || 0) + r.cost; d.hasCost = true }
        if (r.unpriced) d.unpriced += r.unpriced
        if (source === "all" && r.peak !== undefined && r.peak !== null) d.peak = Math.max(d.peak || 0, r.peak)
        d.bySource[r.source] = (d.bySource[r.source] || 0) + r.output
        if (r.cost !== undefined && r.cost !== null) {
            if (!d.costBySource) d.costBySource = {}
            d.costBySource[r.source] = (d.costBySource[r.source] || 0) + r.cost
        }
        for (var m = 0; m < r.models.length; ++m) {
            var model = r.models[m]
            var key = model.name || "?"
            var t = d.models[key] || (d.models[key] = { name: key, requests: 0, output: 0, measuredOutput: 0, measuredMs: 0 })
            t.requests += model.requests; t.output += model.output; t.measuredOutput += model.measuredOutput; t.measuredMs += model.measuredMs
        }
    }
    var out = []
    for (var date in byDate) out.push(byDate[date])
    out.sort(function(a, b) { return a.date < b.date ? -1 : a.date > b.date ? 1 : 0 })
    return out
}

function totals(list) {
    var t = emptyDay("")
    var best = null
    for (var i = 0; i < list.length; ++i) {
        var d = list[i]
        t.requests += d.requests; t.input += d.input; t.cachedInput += d.cachedInput; t.output += d.output
        t.measuredOutput += d.measuredOutput; t.measuredMs += d.measuredMs; t.unpriced += d.unpriced; t.agents += d.agents
        if (d.hasCost) { t.cost = (t.cost || 0) + d.cost; t.hasCost = true }
        for (var s in d.bySource) t.bySource[s] = (t.bySource[s] || 0) + d.bySource[s]
        for (var m in d.models) {
            var x = d.models[m]
            var y = t.models[m] || (t.models[m] = { name: m, requests: 0, output: 0, measuredOutput: 0, measuredMs: 0 })
            y.requests += x.requests; y.output += x.output; y.measuredOutput += x.measuredOutput; y.measuredMs += x.measuredMs
        }
        if (!best || d.output > best.output) best = d
    }
    // 活躍 N 天 counts days with token usage only; a day with traffic alone is listed but not active (DESIGN 10).
    t.activeDays = list.filter(function(d) { return d.requests > 0 || d.output > 0 }).length
    t.best = best
    return t
}

function speed(output, ms) { return ms > 0 ? output * 1000 / ms : -1 }

function compact(n) {
    if (n < 1000) return String(Math.round(n))
    if (n < 1e6) return (n / 1e3).toFixed(n < 1e4 ? 2 : 1) + "k"
    if (n < 1e9) return (n / 1e6).toFixed(n < 1e7 ? 2 : 1) + "M"
    return (n / 1e9).toFixed(2) + "B"
}

function grouped(n) { return String(Math.round(n)).replace(/\B(?=(\d{3})+(?!\d))/g, ",") }

function delta(now, before) {
    if (!(before > 0)) return ""
    var p = (now - before) / before * 100
    return (p >= 0 ? "+" : "−") + Math.abs(p).toFixed(Math.abs(p) < 10 ? 1 : 0) + "%"
}

// DESIGN 10 Usage history: each day's traffic of the selection (tools per the setting), per source, and this computer's
// total, keyed by date.
function trafficByDate(traffic, source, from, to, disabled, includeTools) {
    var off = {}
    for (var o = 0; o < (disabled || []).length; ++o) off[disabled[o]] = true
    var out = {}
    for (var k = 0; k < (traffic || []).length; ++k) {
        var t = traffic[k]
        if (t.date < from || t.date > to) continue
        var d = out[t.date] || (out[t.date] = { rx: 0, tx: 0, rxTools: 0, txTools: 0, machineRx: 0, machineTx: 0, bySource: {} })
        if (t.key === "machine") { d.machineRx += t.rx; d.machineTx += t.tx; continue }
        if (off[t.key] || (source !== "all" && t.key !== source)) continue
        var rx = t.rx + (includeTools ? t.rxTools : 0), tx = t.tx + (includeTools ? t.txTools : 0)
        d.rx += rx; d.tx += tx; d.rxTools += t.rxTools; d.txTools += t.txTools
        var s = d.bySource[t.key] || (d.bySource[t.key] = { rx: 0, tx: 0 })
        s.rx += rx; s.tx += tx
    }
    return out
}
