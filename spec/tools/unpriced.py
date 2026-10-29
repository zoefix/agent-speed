#!/usr/bin/env python3
"""Unpriced diagnostics (METRICS 9.10): why responses have no price, grouped into rows, and the copied text.

  python3 spec/tools/unpriced.py            # checks spec/fixtures/unpriced-cases.json

reference.py uses this module for `--dump-history`, `--dump-unpriced` and `--check-fixtures`;
spec/tools/unpriced_cases.py writes the cases. Only the Python standard library is used.
"""
import os
import platform
import sys
from datetime import date

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import prices as pricing  # noqa: E402

SPEC = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CASES = os.path.join(SPEC, "fixtures", "unpriced-cases.json")
FORMAT = 1
SOURCES = ("codex", "claudeCode", "claudeDesktop", "deepseek", "opencode", "gemini", "omp", "antigravity", "workbuddy",
           "workbuddyAi", "zcode", "kimi", "grokBuild", "pi", "openclaw", "hermes", "droid", "trae")
REASONS = ("noModel", "notInTable", "noFastPrice", "unpriceable", "noDate")
MAX_SCALARS = 120
ELLIPSIS = "…"
SHORT_ESCAPES = {"\b": "\\b", "\f": "\\f", "\n": "\\n", "\r": "\\r", "\t": "\\t", '"': '\\"', "\\": "\\\\"}
TOTALS = ("requests", "unpriced", "pricedNow", "noCostData", "derived")
DAY_FIRST_MS = -62_135_596_800_000 + 2 * 86_400_000  # METRICS 8: responses outside have no date
DAY_LAST_MS = 253_402_300_799_999 - 2 * 86_400_000


# ---------------------------------------------------------------- facts


def trimmed(text):
    """*Trimmed* of METRICS 4.9: Python `str.isspace()` whitespace removed at both ends; null is empty."""
    return text.strip() if isinstance(text, str) else ""


def fact_key(model, provider, fast, no_date=False):
    return trimmed(model), provider or None, fast is True, no_date is True


def utf8(text):
    return text.encode("utf-8", "surrogatepass")


def fact_order(key):
    model, provider, fast, no_date = key
    return utf8(model), provider is not None, utf8(provider or ""), fast, no_date


def fact_list(tally):
    return [dict(model=k[0], provider=k[1], fast=k[2], requests=n) if not k[3] else
            dict(model=k[0], provider=k[1], fast=k[2], noDate=True, requests=n)
            for k, n in sorted(tally.items(), key=lambda kv: fact_order(kv[0])) if n > 0]


def read_part(stored):
    """METRICS 9.10 *Reading*: one stored contribution (file or session, day, source) → its facts.
    `stored` = {fileVersion, requests, unpriced (null: no cost data), models {raw model: unpriced requests},
    side [{model, provider, fast, requests, version}]}. Returns {requests, noCostData, facts, derived}."""
    unpriced = stored.get("unpriced")
    if unpriced is None:
        return dict(requests=stored["requests"], noCostData=stored["requests"], facts=[], derived=0)
    side = stored.get("side") or []
    if unpriced <= 0:
        return dict(requests=stored["requests"], noCostData=0, facts=[], derived=0)
    if side and all(r["version"] == stored["fileVersion"] and r["requests"] >= 1 for r in side) and sum(
            r["requests"] for r in side) == unpriced:
        tally = {}
        for r in side:
            key = fact_key(r["model"], r["provider"], r["fast"])
            tally[key] = tally.get(key, 0) + r["requests"]
        return dict(requests=stored["requests"], noCostData=0, facts=fact_list(tally), derived=0)
    tally, left = {}, unpriced
    for model, n in sorted((stored.get("models") or {}).items(), key=lambda kv: utf8(kv[0])):
        take = min(max(n, 0), left)
        if take > 0:
            key = fact_key(model, None, False)
            tally[key] = tally.get(key, 0) + take
            left -= take
    if left > 0:
        key = fact_key("", None, False)
        tally[key] = tally.get(key, 0) + left
    return dict(requests=stored["requests"], noCostData=0, facts=fact_list(tally), derived=unpriced)


def merge_parts(parts):
    """Sums parts of the same (day, source): facts by key, the counters by addition (METRICS 9.10 *Rows*)."""
    merged = {}
    for p in parts:
        m = merged.setdefault((p["day"], p["source"]), dict(day=p["day"], source=p["source"], requests=0,
                                                             noCostData=0, derived=0, tally={}))
        for k in ("requests", "noCostData", "derived"):
            m[k] += p[k]
        for f in p["facts"]:
            key = fact_key(f["model"], f["provider"], f["fast"], f.get("noDate"))
            m["tally"][key] = m["tally"].get(key, 0) + f["requests"]
    return [dict(day=m["day"], source=m["source"], requests=m["requests"], noCostData=m["noCostData"],
                 derived=m["derived"], facts=fact_list(m["tally"]))
            for _, m in sorted(merged.items(), key=lambda kv: (kv[0][0], SOURCES.index(kv[0][1])))]


def has_day(ms):
    return DAY_FIRST_MS <= ms <= DAY_LAST_MS


def live_parts(records, today):
    """Parts of the engine's today responses `(source, response)` with their `costMicroUsd` (METRICS 9.10 *Live*):
    every response without a known cost is a fact; one whose `endMs` has no date (8) is a `noDate` fact."""
    parts = []
    for source in SOURCES:
        mine = [r for s, r in records if s == source]
        if not mine:
            continue
        tally = {}
        for r in mine:
            if r.get("costMicroUsd") is None:
                key = fact_key(r.get("model"), r.get("provider"), r.get("fast"), not has_day(r["endMs"]))
                tally[key] = tally.get(key, 0) + r.get("requestCount", 1)
        parts.append(dict(day=today, source=source, requests=sum(r.get("requestCount", 1) for r in mine),
                          noCostData=0, derived=0, facts=fact_list(tally)))
    return parts


# ---------------------------------------------------------------- rows


def aggregate(parts, index, first, last, source=None, excluded=()):
    """Source groups with their totals and rows over the parts whose day lies in [first, last], for one source or
    all, without `excluded` sources (METRICS 9.10 *Rows*). `index` is the current price table."""
    groups, rows = {}, {}
    for part in parts:
        src = part["source"]
        if not first <= part["day"] <= last or src in excluded or (source is not None and src != source):
            continue
        g = groups.setdefault(src, dict(source=src, **{k: 0 for k in TOTALS}))
        for k in ("requests", "noCostData", "derived"):
            g[k] += part[k]
        for fact in part["facts"]:
            n = fact["requests"]
            g["unpriced"] += n
            key = fact_key(fact["model"], fact["provider"], fact["fast"], fact.get("noDate"))
            if key[3]:
                diag = dict(reason="noDate", canonical=None, rule=None)
            else:
                diag = index.diagnose(key[0], key[1], key[2])
                if diag is None:
                    g["pricedNow"] += n
                    continue
            row = rows.get((src,) + key)
            if row is None:
                row = rows[(src,) + key] = dict(
                    source=src, reason=diag["reason"], model=key[0], provider=key[1], fast=key[2],
                    canonical=diag["canonical"], rule=diag["rule"]["reason"] if diag["rule"] else None, requests=0,
                    first=part["day"], last=part["day"], order=fact_order(key))
            row["requests"] += n
            row["first"] = min(row["first"], part["day"])
            row["last"] = max(row["last"], part["day"])
    out = []
    for g in sorted(groups.values(), key=lambda g: (-g["unpriced"], SOURCES.index(g["source"]))):
        mine = sorted((r for k, r in rows.items() if k[0] == g["source"]), key=lambda r: (-r["requests"], r["order"]))
        out.append(dict(g, rows=[{k: v for k, v in r.items() if k != "order"} for r in mine]))
    return out


# ---------------------------------------------------------------- text (METRICS 9.10 *Text*)


def ascii_lower(text):
    return "".join(chr(ord(c) + 32) if "A" <= c <= "Z" else c for c in text)


def path_like(text):
    low = ascii_lower(text)
    return (low.startswith(("/", "\\", "~", "file:"))
            or (len(low) >= 3 and "a" <= low[0] <= "z" and low[1] == ":" and low[2] in "/\\")
            or any(p in low for p in ("/users/", "\\users\\", "/home/")))


def escape(text):
    """`"` and `\\` escaped; U+0000-U+001F, U+007F-U+009F, U+2028 and U+2029 as a short escape or `\\u` with four
    lower-case hex digits; everything else as is."""
    out = []
    for ch in text:
        c = ord(ch)
        if ch in SHORT_ESCAPES:
            out.append(SHORT_ESCAPES[ch])
        elif c < 0x20 or 0x7F <= c <= 0x9F or c in (0x2028, 0x2029):
            out.append("\\u%04x" % c)
        else:
            out.append(ch)
    return "".join(out)


def quoted(text, redact=True):
    """A text value of METRICS 9.10 *Text*: null; else lone surrogates become U+FFFD, a path-like value `<path>`,
    more than 120 Unicode scalars the first 120 and `…`, then escaped and in double quotes."""
    if text is None:
        return "null"
    text = "".join("�" if 0xD800 <= ord(ch) <= 0xDFFF else ch for ch in text)
    if redact and path_like(text):
        text = "<path>"
    if len(text) > MAX_SCALARS:
        text = text[:MAX_SCALARS] + ELLIPSIS
    return '"' + escape(text) + '"'


def days_between(first, last):
    return (date.fromisoformat(last) - date.fromisoformat(first)).days + 1


def render(groups, app, table, view, first, last, source=None, excluded=()):
    lines = ["Agent Speed unpriced diagnostics %d" % FORMAT,
             "app version=%s platform=%s os=%s" % (app["version"], app["platform"], app["os"]),
             "prices version=%d updated=%s" % (table["version"], table["updated"]),
             "range view=%s first=%s last=%s days=%d source=%s excluded=%s" % (
                 view, first, last, days_between(first, last), source or "all",
                 ",".join(s for s in SOURCES if s in excluded) or "none")]
    total = dict({k: 0 for k in TOTALS}, rows=0)
    for g in groups:
        lines.append("source id=%s requests=%d unpriced=%d pricedNow=%d noCostData=%d derived=%d rows=%d" % (
            tuple(g[k] for k in ("source",) + TOTALS) + (len(g["rows"]),)))
        for r in g["rows"]:
            lines.append("row source=%s reason=%s requests=%d first=%s last=%s fast=%s model=%s provider=%s "
                         "canonical=%s rule=%s" % (
                             r["source"], r["reason"], r["requests"], r["first"], r["last"],
                             "true" if r["fast"] else "false", quoted(r["model"]), quoted(r["provider"]),
                             quoted(r["canonical"]), quoted(r["rule"], redact=False)))
        for k in TOTALS:
            total[k] += g[k]
        total["rows"] += len(g["rows"])
    lines.append("total requests=%d unpriced=%d pricedNow=%d noCostData=%d derived=%d rows=%d" % (
        tuple(total[k] for k in TOTALS) + (total["rows"],)))
    return "".join(line + "\n" for line in lines)


def report(parts, index, app, view, first, last, source=None, excluded=()):
    groups = aggregate(parts, index, first, last, source, excluded)
    return groups, render(groups, app, index.table, view, first, last, source, excluded)


# ---------------------------------------------------------------- reference environment


def reference_app():
    with open(os.path.join(os.path.dirname(SPEC), "VERSION"), encoding="utf-8") as f:
        version = f.read().strip()
    if sys.platform == "darwin":
        os_name = "macos-" + (platform.mac_ver()[0].split(".")[0] or "0")
    elif sys.platform == "win32":
        os_name = "windows-11" if sys.getwindowsversion().build >= 22000 else "windows-10"
    else:
        os_name = sys.platform
    return dict(version=version, platform="reference", os=os_name)


def history_parts(rows):
    """Parts of `daily_history` rows (one per date and source, every fact read from valid side rows)."""
    return [dict(day=r["date"], source=r["source"], requests=r["requests"],
                 noCostData=r["requests"] if r["unpricedRequests"] is None else 0, derived=0,
                 facts=r.get("unpriced") or []) for r in rows]


# ---------------------------------------------------------------- checks


def stored_parts(entries):
    """Parts of stored contributions `{day, source, stored}` (METRICS 9.10 *Reading*), summed per day and source."""
    return merge_parts([dict(read_part(e["stored"]), day=e["day"], source=e["source"]) for e in entries])


def run_report(case, cases, index):
    v = case["view"]
    if v["view"] == "live":
        today = next(t for t in cases["live"] if t["name"] == case["input"])
        parts = live_parts([(r["source"], r) for r in today["responses"]], today["today"])
    else:
        parts = stored_parts(cases["stored"])
    groups, text = report(parts, index, case["app"], v["view"], v["first"], v["last"], v.get("source"),
                          tuple(v.get("excluded", ())))
    return dict(sources=[{k: g[k] for k in ("source",) + TOTALS} for g in groups],
                rows=[r for g in groups for r in g["rows"]], text=text)


def check_cases(path=CASES):
    cases = pricing.load(path)
    folder = os.path.dirname(path)
    index = pricing.Index(pricing.load(os.path.join(folder, cases["table"])))
    errors = []
    for i, case in enumerate(cases["diagnose"]):
        got = index.diagnose(case["model"], case.get("provider"), case.get("fast", False))
        priced = index.price(dict(model=case["model"], provider=case.get("provider"), fast=case.get("fast", False),
                                  input=1000, output=100))
        if (got is None) != (priced is not None):
            errors.append("diagnose[%d] %r: disagrees with Index.price (%r)" % (i, case["model"], priced))
        if got is not None:
            got = dict(reason=got["reason"], canonical=got["canonical"],
                       rule=got["rule"]["reason"] if got["rule"] else None)
        if got != case["expect"]:
            errors.append("diagnose[%d] %r: expected %r got %r" % (i, case["model"], case["expect"], got))
    for i, case in enumerate(cases["read"]):
        got = read_part(case["stored"])
        if got != case["expect"]:
            errors.append("read[%d] %s: expected %r got %r" % (i, case["name"], case["expect"], got))
    for i, case in enumerate(cases["quote"]):
        got = quoted(case["text"], redact=case.get("redact", True))
        if got != case["expect"]:
            errors.append("quote[%d] %r: expected %r got %r" % (i, case["text"], case["expect"], got))
    for case in cases["live"]:
        records = [(r["source"], r) for r in case["responses"]]
        got = live_parts(records, case["today"])
        if got != case["expect"]:
            errors.append("live %s: expected %r got %r" % (case["name"], case["expect"], got))
    for case in cases["reports"]:
        table = pricing.Index(pricing.load(os.path.join(folder, case["table"]))) if case.get("table") else index
        got = run_report(case, cases, table)["text"]
        if got != case["expect"]:
            errors.append("report %s: expected\n%sgot\n%s" % (case["name"], case["expect"], got))
    return errors


def summary():
    cases = pricing.load(CASES)
    return "%d diagnose, %d read, %d quote, %d live cases and %d reports" % tuple(
        len(cases[k]) for k in ("diagnose", "read", "quote", "live", "reports"))


def main():
    errors = check_cases()
    if errors:
        print("\n".join("error: " + e for e in errors), file=sys.stderr)
        return 1
    print("unpriced diagnostics match " + summary())
    return 0


if __name__ == "__main__":
    sys.exit(main())
