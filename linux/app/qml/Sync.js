.pragma library

// Brings a ListModel in line with rows keyed by `key`, so delegates (and their animations) survive updates.
function sync(model, rows, key) {
    var wanted = {}
    for (var r = 0; r < rows.length; ++r) wanted[rows[r][key]] = true
    for (var i = model.count - 1; i >= 0; --i) {
        if (!wanted[model.get(i)[key]]) model.remove(i)
    }
    for (var j = 0; j < rows.length; ++j) {
        var at = -1
        for (var k = j; k < model.count; ++k) {
            if (model.get(k)[key] === rows[j][key]) { at = k; break }
        }
        if (at < 0) { model.insert(j, rows[j]); continue }
        if (at !== j) model.move(at, j, 1)
        var item = model.get(j)
        for (var p in rows[j]) {
            if (item[p] !== rows[j][p]) model.setProperty(j, p, rows[j][p])
        }
    }
}

// Connector facts for depth-first agent rows: whether a row's branch continues to a later sibling, whether it has
// children, and a bit mask of the ancestor levels whose rails pass through it.
function tree(rows) {
    var n = rows.length, out = [], open = []
    for (var i = 0; i < n; ++i) {
        var d = rows[i].depth
        var j = i + 1
        while (j < n && rows[j].depth > d) ++j
        var continues = d > 0 && j < n && rows[j].depth === d
        var mask = 0
        for (var level = 1; level < d; ++level) {
            if (open[level]) mask |= (1 << level)
        }
        open[d] = continues
        out.push({ continues: continues, hasChildren: i + 1 < n && rows[i + 1].depth > d, rails: mask })
    }
    return out
}
