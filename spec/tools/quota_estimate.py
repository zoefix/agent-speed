#!/usr/bin/env python3
"""Reference implementation of quota estimates (spec/QUOTAS.md section 10).

Standard library only, deterministic. It implements the account keys and merge rule, the spend ledger and its sums,
observations, window instances and segments, the interval vote, the live percent, the report entries of the
`quotaEstimate` extension and their send cadence; it writes and checks spec/fixtures/quota-estimate-cases.json.

    python3 spec/tools/quota_estimate.py --check-fixtures
    python3 spec/tools/quota_estimate.py --write
"""

from __future__ import annotations

import argparse
import bisect
import hashlib
import hmac
import json
import math
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CASES = ROOT / "spec" / "fixtures" / "quota-estimate-cases.json"

MINUTE = 60_000
HOUR = 60 * MINUTE
DAY = 24 * HOUR
LAG_MS = 2 * MINUTE
KEEP_MS = 8 * DAY
HI_PENALTY = 0.25
FULL_STEP = 20.0
MIN_WEIGHT = 0.05
SEGMENTS_PER_INSTANCE = 64
INSTANCES_PER_CLASS = 64
INCREMENT_STEP = 10.0
INSTANCE_JOIN_MS = 2 * MINUTE
HOUR_ROUNDED_SLACK_MS = HOUR
PERSONAL_CLAUDE_PLANS = ("claude_free", "claude_pro", "claude_max", "claude_ultra")
PLAN_PATTERN = re.compile(r"^[a-z0-9_.-]{1,64}$")
SEND_EVERY_MS = 60_000
PRECISION_FLOOR = 0.02
HIGH_WIDTH = 0.08


# 10.1 Accounts

def account_key(salt: bytes, vendor: str, ident: str) -> str:
    return hmac.new(salt, ("%s:%s" % (vendor, ident)).encode("utf-8"), hashlib.sha256).hexdigest()[:16]


def desktop_shares_cli(desktop_org, cli_org, cli_org_type) -> bool:
    return bool(desktop_org) and desktop_org == cli_org and cli_org_type in PERSONAL_CLAUDE_PLANS


def clean_plan(value):
    return value if isinstance(value, str) and PLAN_PATTERN.match(value) else None


class Chains:
    """Codex history accounts: observations that share a window instance belong to one account (union-find)."""

    def __init__(self):
        self.parent = {}

    def find(self, x):
        self.parent.setdefault(x, x)
        while self.parent[x] != x:
            self.parent[x] = self.parent[self.parent[x]]
            x = self.parent[x]
        return x

    def link(self, instances):
        roots = [self.find(i) for i in instances]
        for r in roots[1:]:
            self.parent[self.find(r)] = self.find(roots[0])
        return self.find(roots[0])


def instance_tag(window_id, resets_ms):
    return "%s@%d" % (window_id, round(resets_ms / (2 * MINUTE)))


# 10.3 Spend ledger

def minute_of(ms):
    return ms // MINUTE * MINUTE


def model_matches(model, name):
    if not name:
        return True
    tokens = set(str(model).lower().split("-"))
    return all(part in tokens for part in name.lower().split())


class Ledger:
    """Rows per (account, source, model, minute): [costMicroUsd, unpricedRequests, pricedTokens, unpricedTokens]."""

    def __init__(self):
        self.rows = {}
        self.cache = {}

    def add(self, account, source, model, end_ms, cost, requests=1, tokens=0):
        row = self.rows.setdefault((account, source, model, minute_of(end_ms)), [0, 0, 0, 0])
        if cost is None:
            row[1] += requests
            row[3] += tokens
        else:
            row[0] += cost
            row[2] += tokens
        self.cache.clear()

    def prune(self, now_ms):
        for k in [k for k in self.rows if k[3] < now_ms - KEEP_MS]:
            del self.rows[k]
        self.cache.clear()

    def _series(self, accounts, name):
        key = (tuple(sorted(accounts)), name or "")
        if key not in self.cache:
            per, rates = {}, {}
            for (acc, _src, model, minute), row in self.rows.items():
                if acc in accounts and model_matches(model, name):
                    v = per.setdefault(minute, [0, 0, 0])
                    v[0] += row[0]
                    v[1] += row[1]
                    v[2] += row[3]
                    r = rates.setdefault(model, [0, 0])
                    r[0] += row[0]
                    r[1] += row[2]
            minutes = sorted(per)
            sums = [[0, 0, 0]]
            for m in minutes:
                sums.append([a + b for a, b in zip(sums[-1], per[m])])
            rate = max((c / t for c, t in rates.values() if t > 0), default=None)
            self.cache[key] = (minutes, sums, rate)
        return self.cache[key]

    def _range(self, accounts, name, first_minute, last_minute):
        minutes, sums, rate = self._series(accounts, name)
        if last_minute < first_minute:
            return 0, 0, 0, rate
        i = bisect.bisect_left(minutes, first_minute)
        j = bisect.bisect_right(minutes, last_minute)
        return sums[j][0] - sums[i][0], sums[j][1] - sums[i][1], sums[j][2] - sums[i][2], rate

    def inner(self, accounts, name, start, end):
        """Spend of minutes wholly inside [start, end]: a lower bound of what ended in that span (unpriced as 0)."""
        cost, unpriced, _, _ = self._range(accounts, name, -(-start // MINUTE) * MINUTE, minute_of(end) - MINUTE)
        return cost, unpriced

    def outer(self, accounts, name, start, end):
        """Spend of minutes touching [start, end] (unpriced as 0) and its unpriced requests."""
        cost, unpriced, _, _ = self._range(accounts, name, minute_of(start), minute_of(end))
        return cost, unpriced

    def outer_high(self, accounts, name, start, end):
        """outer() with unpriced tokens at the highest cost per token of the accounts' priced models; inf without one."""
        cost, unpriced, tokens, rate = self._range(accounts, name, minute_of(start), minute_of(end))
        if not unpriced:
            return cost
        return math.inf if rate is None else cost + tokens * rate

    def mix(self, accounts, name, start, end, top=3):
        totals = {}
        for (acc, _src, model, minute), (cost, *_rest) in self.rows.items():
            if cost > 0 and acc in accounts and model_matches(model, name) and minute_of(start) <= minute <= minute_of(end):
                totals[model] = totals.get(model, 0) + cost
        whole = sum(totals.values())
        if whole <= 0:
            return []
        ranked = sorted(totals.items(), key=lambda kv: (-kv[1], kv[0]))[:top]
        return [{"model": m, "share": round_half_away(c / whole, 3)} for m, c in ranked]


# 10.4 Observations

def resolution(p):
    for digits in (0, 1):
        scaled = p * 10 ** digits
        if abs(scaled - round(scaled)) < 1e-9:
            return 10.0 ** -digits
    return 0.01


def window_id(kind, name, minutes):
    return "%s:%s:%s" % (kind, name or "", minutes)


def half_life_ms(minutes):
    return 7 * DAY if minutes and minutes >= 10_000 else 2 * DAY


def instances(observations):
    """Groups observations of one account-window into instances: resets within 2 minutes of the first are one."""
    out = []
    for o in sorted(observations, key=lambda o: (o["R"], o["t"])):
        if out and o["R"] - out[-1]["R"] <= INSTANCE_JOIN_MS:
            out[-1]["obs"].append(o)
        else:
            out.append({"R": o["R"], "obs": [o]})
    for inst in out:
        rs = [o["R"] for o in inst["obs"]]
        minutes = inst["obs"][0]["minutes"]
        inst["R"] = max(rs)
        inst["S"] = max(rs) - minutes * MINUTE
        slack = HOUR_ROUNDED_SLACK_MS if (inst["obs"][0]["kind"] == "fiveHour" and min(rs) % HOUR == 0) else 0
        inst["Shi"] = min(rs) - minutes * MINUTE - slack
        inst["obs"] = sorted([o for o in inst["obs"] if inst["Shi"] - MINUTE <= o["t"] <= inst["R"] + MINUTE],
                             key=lambda o: (o["t"], o["tLo"]))
    return [i for i in out if i["obs"]]


def window_q(observations):
    """Finest resolution per source among the account-window's observations."""
    q = {}
    for o in observations:
        q[o["source"]] = min(q.get(o["source"], 1.0), resolution(o["p"]))
    return q


# 10.5 Segments

def runs(obs, q_of):
    out = []
    for o in obs:
        q = q_of[o["source"]]
        if out and abs(out[-1]["p"] - o["p"]) < 1e-9:
            r = out[-1]
            r["last"] = o
            r["tLoMax"] = max(r["tLoMax"], o["tLo"])
            r["q"] = max(r["q"], q)
        else:
            out.append({"p": o["p"], "first": o, "last": o, "tLoMax": o["tLo"], "q": q})
    return out


def segments(inst, ledger, accounts, name, lag=LAG_MS, now_ms=None):
    """Cumulative and increment segments of one window instance (QUOTAS 10.5). Spend after `now_ms` does not exist yet."""
    S, Shi = inst["S"], inst["Shi"]
    upto = (lambda t: t) if now_ms is None else (lambda t: min(t, now_ms))
    q_of = window_q(inst["obs"])
    rs = runs(inst["obs"], q_of)
    segs = []
    for r in rs:
        p, q = r["p"], r["q"]
        if p <= 0:
            continue
        c_lo, _ = ledger.inner(accounts, name, S, r["tLoMax"] - lag)
        c_hi = ledger.outer_high(accounts, name, Shi, upto(r["first"]["t"] + lag))
        lo = 0.0 if p >= 100 else 100.0 * c_lo / (p + q)
        p_top = min(p, 100.0)
        hi = math.inf if p_top - q <= 0 else 100.0 * c_hi / (p_top - q)
        segs.append({"type": "cumulative", "lo": lo, "hi": hi, "dp": p, "t": r["last"]["t"]})
    anchors = []
    for r in rs:
        if r["p"] <= 0:
            continue
        if not anchors or r["p"] >= anchors[-1]["p"] + max(3 * max(r["q"], anchors[-1]["q"]), INCREMENT_STEP) - 1e-9:
            anchors.append(r)
    pairs = sorted(((a, b) for i, a in enumerate(anchors) for b in anchors[i + 1:]),
                   key=lambda ab: (-(ab[1]["p"] - ab[0]["p"]), ab[0]["p"]))[:SEGMENTS_PER_INSTANCE // 2]
    for a, b in sorted(pairs, key=lambda ab: (ab[0]["p"], ab[1]["p"])):
        q2 = a["q"] + b["q"]
        dp = b["p"] - a["p"]
        c_lo = max(0, ledger.inner(accounts, name, S, b["tLoMax"] - lag)[0]
                   - ledger.outer(accounts, name, S, upto(a["first"]["t"] + lag))[0])
        hi_end = ledger.outer_high(accounts, name, Shi, upto(b["first"]["t"] + lag))
        hi_start = ledger.inner(accounts, name, Shi, a["tLoMax"] - lag)[0]
        lo = 0.0 if b["p"] >= 100 else 100.0 * c_lo / (dp + q2)
        dp_top = min(b["p"], 100.0) - a["p"]
        hi = math.inf if dp_top - q2 <= 0 else 100.0 * max(0, hi_end - hi_start) / (dp_top - q2)
        segs.append({"type": "increment", "lo": lo, "hi": hi, "dp": dp, "t": b["last"]["t"]})
    segs = [s for s in segs if s["lo"] > 0 or (math.isfinite(s["hi"]) and s["hi"] > 0)]
    if len(segs) > SEGMENTS_PER_INSTANCE:
        cum = [s for s in segs if s["type"] == "cumulative"]
        keep_inc = [s for s in segs if s["type"] == "increment"]
        room = SEGMENTS_PER_INSTANCE - len(keep_inc)
        stride = len(cum) / room
        keep_cum = [cum[min(len(cum) - 1, int(len(cum) - 1 - i * stride))] for i in range(room)][::-1]
        segs = keep_cum + keep_inc
    return segs


# 10.6 Estimate

def instance_bounds(segs, R):
    """One interval per instance: the largest lower bound and the smallest upper bound not below it. When a
    cumulative lower bound lies above an increment's upper bound, the vendor left part of the window's first spend
    uncounted (or the increment saw unseen usage): the instance then keeps its increments alone, which an uncounted
    start does not bias."""
    def bounds(group):
        los = [x["lo"] for x in group if x["lo"] > 0]
        if not los:
            return None
        low = max(los)
        his = [x["hi"] for x in group if math.isfinite(x["hi"]) and x["hi"] >= low]
        return low, (min(his) if his else math.inf)

    incs = [x for x in segs if x["type"] == "increment"]
    cum_lo = max([x["lo"] for x in segs if x["type"] == "cumulative"] or [0])
    inc_hi = min([x["hi"] for x in incs] or [math.inf])
    offset = cum_lo > inc_hi
    b = bounds(incs if offset and bounds(incs) else segs)
    if b is None:
        return None
    return {"R": R, "lo": b[0], "hi": b[1], "dp": max(x["dp"] for x in segs), "t": max(x["t"] for x in segs),
            "offset": offset}


def weight(item, now_ms, half_life):
    age = max(0, now_ms - item["t"])
    return min(1.0, item["dp"] / FULL_STEP) * 0.5 ** (age / half_life)


def vote(weighted):
    """weighted: [(lo, hi, w, instance)]. Returns (Klo, Khi, inside weight, narrow instances inside, loss) or None:
    the narrow instances are those whose own interval holds the region and is at most 8 % wide."""
    items = [(lo, hi, w, i) for lo, hi, w, i in weighted
             if (lo > 0 or (math.isfinite(hi) and hi > 0)) and w >= MIN_WEIGHT]
    if not any(lo > 0 for lo, _, _, _ in items):
        return None
    floor = min(lo for lo, _, _, _ in items if lo > 0)
    points = sorted({x for lo, hi, _, _ in items for x in (lo, hi) if math.isfinite(x) and x >= floor})
    cells = []
    for i, x in enumerate(points):
        cells += [(x, x), (x, points[i + 1] if i + 1 < len(points) else math.inf)]
    los = sorted((lo, w) for lo, _, w, _ in items)
    his = sorted((hi, w) for _, hi, w, _ in items if math.isfinite(hi))
    lo_keys, hi_keys = [x for x, _ in los], [x for x, _ in his]
    lo_suffix = [0.0] * (len(los) + 1)
    for i in range(len(los) - 1, -1, -1):
        lo_suffix[i] = lo_suffix[i + 1] + los[i][1]
    hi_prefix = [0.0]
    for _, w in his:
        hi_prefix.append(hi_prefix[-1] + w)
    scored = []
    for a, b in cells:
        probe = a if a == b else (math.sqrt(a * b) if math.isfinite(b) else a * 2)
        loss = lo_suffix[bisect.bisect_right(lo_keys, probe)] + HI_PENALTY * hi_prefix[bisect.bisect_left(hi_keys, probe)]
        scored.append((a, b, round(loss, 9)))
    least = min(c[2] for c in scored)
    regions = []
    for a, b, loss in scored:
        if loss != least:
            continue
        if regions and regions[-1][1] >= a:
            regions[-1][1] = max(regions[-1][1], b)
        else:
            regions.append([a, b])

    def holders(region):
        return [(lo, hi, w, i) for lo, hi, w, i in items
                if lo <= region[0] * (1 + 1e-12) and hi >= region[1] * (1 - 1e-12)]

    ranked = sorted(regions, key=lambda r: (-round(sum(h[2] for h in holders(r)), 9), r[0]))
    klo, khi = ranked[0]
    held = holders(ranked[0])
    narrow_ids = {i for lo, hi, _, i in held if lo > 0 and math.isfinite(hi) and hi / lo - 1 <= HIGH_WIDTH}
    return klo, khi, sum(h[2] for h in held), len(narrow_ids), least


def confidence(klo, khi, inside_w, narrow_n, above):
    """`above`: weight of lower bounds above the region (a region below a lower bound is never high)."""
    if not math.isfinite(khi):
        return "low"
    width = khi / klo - 1
    if width <= HIGH_WIDTH and inside_w >= 1.5 and narrow_n >= 2 and above == 0:
        return "high"
    if width <= 0.25 and inside_w >= 0.5:
        return "medium"
    return "low"


def narrow(x):
    return x["lo"] > 0 and math.isfinite(x["hi"]) and x["hi"] / x["lo"] - 1 <= HIGH_WIDTH


def after_cut(items):
    """A lowered cap: the two newest narrow instances agree and lie wholly below older lower bounds. The older
    instances whose lower bound is above both are dropped (contamination never raises a lower bound)."""
    ns = sorted((x for x in items if narrow(x)), key=lambda x: x["t"])
    if len(ns) < 2:
        return items
    a, b = ns[-2], ns[-1]
    if max(a["lo"], b["lo"]) > min(a["hi"], b["hi"]):
        return items
    top = max(a["hi"], b["hi"])
    return [x for x in items if not (x["t"] < a["t"] and x["lo"] > top)]


def estimate(items, now_ms, half_life):
    """items: one interval per window instance (instance_bounds). Returns the estimate of 10.6."""
    items = after_cut(items)
    weighted = [(s["lo"], s["hi"], weight(s, now_ms, half_life), s.get("R")) for s in items]
    used = [x for x in weighted if x[2] >= MIN_WEIGHT]
    result = vote(weighted)
    if result is None:
        return {"confidence": "none", "cap": None, "low": None, "high": None, "windows": 0}
    klo, khi, inside_w, narrow, loss = result
    above = sum(w for lo, _, w, _ in used if lo > klo * (1 + 1e-12))
    cap = math.sqrt(klo * khi) if math.isfinite(khi) else None
    low, high = klo, (khi if math.isfinite(khi) else None)
    if cap is not None:
        low, high = min(low, cap / (1 + PRECISION_FLOOR)), max(high, cap * (1 + PRECISION_FLOOR))
    return {"confidence": confidence(klo, khi, inside_w, narrow, above), "cap": cap, "low": low, "high": high,
            "region": [klo, khi if math.isfinite(khi) else None], "loss": loss, "inside": inside_w,
            "windows": len({x[3] for x in used})}


def class_of(account, plan, tier, wid):
    return ("plan", plan, tier, wid) if plan else ("account", account, wid)


def prune_closed(items, limit=INSTANCES_PER_CLASS):
    return sorted(items, key=lambda s: s["t"])[-limit:]


# 10.7 Live percent

def live_percent(p_obs, t_obs, R, S, minutes, kind, cap, ledger, accounts, name, now_ms, first_after_reset=None):
    """Returns (percent, estimated, resets_ms) for the current instance."""
    if now_ms < R:
        if cap is None:
            return p_obs, False, R
        live = p_obs + 100.0 * (ledger.outer(accounts, name, S, now_ms)[0] - ledger.outer(accounts, name, S, t_obs)[0]) / cap
        live = min(max(live, p_obs), max(100.0, p_obs))
        return live, live - p_obs >= 0.5, R
    if kind == "fiveHour":
        if first_after_reset is None:
            return 0.0, False, None
        s2 = minute_of(first_after_reset)
    else:
        periods = (now_ms - R) // (minutes * MINUTE)
        s2 = R + periods * minutes * MINUTE
    r2 = s2 + minutes * MINUTE
    if cap is None:
        return 0.0, False, (r2 if kind != "fiveHour" else r2)
    live = min(100.0, 100.0 * ledger.outer(accounts, name, s2, now_ms)[0] / cap)
    return live, True, r2


# Reports (REPORTING-EXTENSIONS 11)

def round_half_away(x, digits):
    if x is None:
        return None
    m = 10 ** digits
    v = math.floor(abs(x) * m + 0.5 + 1e-9) / m
    v = math.copysign(v, x)
    return int(v) if digits == 0 else v


def micro(x):
    return None if x is None else int(math.floor(x + 0.5))


def report_entry(acc, now_ms, ledger, price_version):
    """acc: {source, sources, plan, tier, kind, name, minutes, p, t, R, S, accounts, estimate, firstAfterReset}."""
    est = acc["estimate"]
    cap = est.get("cap")
    name = acc["name"] if acc["kind"] == "modelWeek" else None
    live, estimated, resets = live_percent(acc["p"], acc["t"], acc["R"], acc["S"], acc["minutes"], acc["kind"], cap,
                                           ledger, acc["accounts"], name, now_ms, acc.get("firstAfterReset"))
    passed = now_ms >= acc["R"]
    start = acc["S"] if not passed else (resets - acc["minutes"] * MINUTE if resets else now_ms)
    used, unpriced = ledger.outer(acc["accounts"], name, start, now_ms)
    observed = ledger.outer(acc["accounts"], name, acc["S"], acc["t"])[0]
    coverage = None
    if cap and acc["p"] >= 5:
        coverage = round_half_away(min(1.0, max(0.0, observed / (cap * acc["p"] / 100))), 3)
    return {
        "source": acc["source"],
        "sources": acc["sources"],
        "plan": acc["plan"],
        "tier": acc["tier"],
        "window": {"kind": acc["kind"], "name": acc["name"], "minutes": acc["minutes"]},
        "usedPercent": None if passed else round_half_away(acc["p"], 2),
        "percentAgeMs": None if passed else max(0, now_ms - acc["t"]),
        "resetsInMs": None if resets is None else max(0, resets - now_ms),
        "livePercent": round_half_away(live, 1),
        "liveEstimated": estimated,
        "usedMicroUsd": used,
        "observedUsedMicroUsd": None if passed else observed,
        "unpricedRequests": unpriced,
        "estimate": {
            "capMicroUsd": micro(cap),
            "lowMicroUsd": micro(est.get("low")),
            "highMicroUsd": micro(est.get("high")),
            "confidence": est["confidence"],
            "coverage": coverage,
            "accountUsedMicroUsd": micro(cap * live / 100) if cap else None,
            "windows": est.get("windows", 0),
        },
        "mix": ledger.mix(acc["accounts"], name, start, now_ms),
        "priceVersion": price_version,
    }


def entry_identity(e):
    w = e["window"]
    return (tuple(e["sources"]), w["kind"], w["name"], w["minutes"])


def should_send(entries, last_sent, last_sent_ms, now_ms, leaving=False):
    """Whether this report carries `quotas`: a changed set, a new vendor percent or instance, 60 s, or leaving."""
    if last_sent is None or leaving:
        return True
    if now_ms - last_sent_ms >= SEND_EVERY_MS:
        return True
    before = {entry_identity(e): e for e in last_sent}
    now = {entry_identity(e): e for e in entries}
    if set(before) != set(now):
        return True
    for k, e in now.items():
        b = before[k]
        if e["usedPercent"] != b["usedPercent"] or e["estimate"]["confidence"] != b["estimate"]["confidence"]:
            return True
        if (e["resetsInMs"] is None) != (b["resetsInMs"] is None):
            return True
    return False


# Fixtures

def obs(p, t, R, kind="sevenDay", minutes=10080, source="codex", tLo=None, name=None):
    return {"source": source, "kind": kind, "name": name, "minutes": minutes, "p": p, "t": t,
            "tLo": t if tLo is None else tLo, "R": R}


def fmt_seg(s):
    return {"type": s["type"], "lo": round(s["lo"], 3), "hi": None if not math.isfinite(s["hi"]) else round(s["hi"], 3),
            "dp": s["dp"]}


def ledger_from(rows):
    led = Ledger()
    for r in rows:
        led.add(r.get("account", "a"), r.get("source", "codex"), r.get("model", "gpt-5.5"), r["at"], r.get("cost"),
                r.get("requests", 1))
    return led


T0 = 1_794_000_000_000 // DAY * DAY


def segment_cases():
    R = T0 + 7 * DAY
    S = R - 10080 * MINUTE
    cases = []
    rows = [{"at": S + i * 10 * MINUTE + 30_000, "cost": 1_000_000} for i in range(1, 61)]
    observations = [obs(p, S + m * MINUTE, R) for p, m in ((2, 100), (2, 130), (5, 300), (12, 610))]
    cases.append({"name": "cumulative runs, lag and integer resolution", "rows": rows, "observations": observations})
    observations = [obs(p, S + m * MINUTE, R) for p, m in ((2.5, 100), (12.5, 300), (24.7, 600))]
    cases.append({"name": "0.1 resolution and increments; a cumulative lower bound above an increment keeps the "
                          "increments",
                  "rows": rows, "observations": observations})
    observations = [obs(10, S + 300 * MINUTE, R, tLo=S + 250 * MINUTE)]
    cases.append({"name": "tLo before the observation (in-band value)", "rows": rows, "observations": observations})
    rows_u = rows + [{"at": S + 200 * MINUTE + 5_000, "cost": None}]
    observations = [obs(p, S + m * MINUTE, R) for p, m in ((5, 300), (12, 610))]
    cases.append({"name": "unpriced request: unbounded above", "rows": rows_u, "observations": observations})
    observations = [obs(0.4, S + 20 * MINUTE, R)]
    cases.append({"name": "p - q <= 0: lower bound only", "rows": rows, "observations": observations})
    R5 = T0 + 15 * HOUR
    observations = [obs(30, R5 - 2 * HOUR, R5, kind="fiveHour", minutes=300)]
    rows5 = [{"at": R5 - 5 * HOUR - 30 * MINUTE, "cost": 2_000_000}, {"at": R5 - 4 * HOUR, "cost": 3_000_000}]
    cases.append({"name": "five-hour window on a whole hour: start slack for the upper bound", "rows": rows5,
                  "observations": observations})
    rows_m = [{"at": S + 30 * MINUTE, "cost": 4_000_000, "model": "claude-fable-5-2"},
              {"at": S + 40 * MINUTE, "cost": 6_000_000, "model": "claude-opus-5-5"}]
    observations = [obs(8, S + 60 * MINUTE, R, kind="modelWeek", name="Fable", source="claudeDesktop")]
    cases.append({"name": "modelWeek counts only its model family", "rows": rows_m, "observations": observations})
    out = []
    for c in cases:
        led = ledger_from(c["rows"])
        inst = instances(c["observations"])[0]
        name = c["observations"][0]["name"]
        segs = segments(inst, led, {"a"}, name)
        b = instance_bounds(segs, inst["R"])
        out.append(dict(c, expected={"S": inst["S"], "Shi": inst["Shi"], "segments": [fmt_seg(s) for s in segs],
                                     "instance": None if b is None else fmt_seg(dict(b, type="instance"))}))
    return out


def vote_cases():
    cases = [
        {"name": "clean intersection over two windows", "items": [[90, 110, 1, 1], [95, 120, 1, 2], [80, 105, 1, 2],
                                                                  [96, 103, 0.5, 1]]},
        {"name": "one window only: never high", "items": [[95, 103, 1, 1], [96, 104, 1, 1]]},
        {"name": "contaminated upper bound loses at 0.25", "items": [[95, 105, 1, 1], [96, 104, 1, 2], [60, 70, 1, 3]]},
        {"name": "a lower bound outweighs three contaminated uppers", "items": [[95, 105, 1, 1], [50, 70, 0.9, 2],
                                                                                [50, 70, 0.9, 2], [50, 70, 0.9, 3]]},
        {"name": "five contaminated uppers outweigh a lower bound", "items": [[95, 105, 1, 1]] + [[50, 70, 1, 2]] * 5},
        {"name": "unbounded: at least", "items": [[40, None, 1, 1], [55, None, 0.5, 2]]},
        {"name": "degenerate region", "items": [[100, 120, 1, 1], [80, 100, 1, 2]]},
        {"name": "tie in loss: most weight inside wins", "items": [[10, 20, 1, 1], [30, 40, 0.25, 2]]},
        {"name": "fewer weighted violations win", "items": [[10, 20, 1, 1], [30, 40, 1, 2]]},
        {"name": "weights below 0.05 are dropped", "items": [[100, 110, 1, 1], [500, 600, 0.04, 2]]},
    ]
    for c in cases:
        r = vote([(lo, math.inf if hi is None else hi, w, i) for lo, hi, w, i in c["items"]])
        klo, khi, inside_w, inside_n, loss = r
        c["expected"] = {"low": klo, "high": None if not math.isfinite(khi) else khi, "inside": round(inside_w, 6),
                         "insideWindows": inside_n, "loss": round(loss, 6),
                         "confidence": confidence(klo, khi, inside_w, inside_n,
                                                  sum(w for lo, _, w, _ in c["items"] if lo > klo and w >= MIN_WEIGHT))}
    return cases


def estimate_cases():
    """Instance intervals pooled by the vote, with the published range and the cut rule."""
    day = DAY
    def item(lo, hi, dp, days_ago, R):
        return {"lo": lo, "hi": math.inf if hi is None else hi, "dp": dp, "t": T0 - int(days_ago * day), "R": R}
    cases = [
        {"name": "two narrow windows agree: high, range widened to 2 %",
         "items": [item(99.6, 100.4, 60, 7, 1), item(99.0, 100.9, 40, 0, 2)]},
        {"name": "a contaminated window below a clean one", "items": [item(99.0, 101.0, 60, 7, 1),
                                                                       item(70.0, 72.0, 60, 0, 2)]},
        {"name": "region below a lower bound is never high", "items": [item(90.0, 92.0, 60, 5, 1),
                                                                        item(89.5, 93.0, 60, 3, 2),
                                                                        item(95.0, None, 4, 0, 3)]},
        {"name": "a lowered cap: two agreeing windows drop older ones above them",
         "items": [item(99.0, 101.0, 60, 14, 1), item(99.5, 100.5, 60, 7, 2), item(66.0, 67.5, 40, 3, 3),
                   item(66.3, 67.2, 30, 0, 4)]},
        {"name": "lower bound only", "items": [item(50.0, None, 30, 0, 1)]},
        {"name": "nothing", "items": []},
    ]
    for c in cases:
        e = estimate(c["items"], T0, 7 * DAY)
        c["items"] = [{"lo": x["lo"], "hi": None if not math.isfinite(x["hi"]) else x["hi"], "dp": x["dp"],
                       "ageDays": (T0 - x["t"]) / DAY, "window": x["R"]} for x in c["items"]]
        c["expected"] = {k: (round(v, 6) if isinstance(v, float) else v) for k, v in e.items()
                         if k in ("confidence", "cap", "low", "high", "windows")}
    return cases


def weight_cases():
    out = []
    for dp, age_days, minutes in ((20, 0, 10080), (10, 0, 10080), (40, 7, 10080), (20, 2, 300), (5, 8, 300)):
        seg = {"dp": dp, "t": T0}
        w = weight(seg, T0 + int(age_days * DAY), half_life_ms(minutes))
        out.append({"dp": dp, "ageDays": age_days, "minutes": minutes, "expected": round(w, 6),
                    "used": w >= MIN_WEIGHT})
    return out


def account_cases():
    salt = bytes(range(32))
    keys = [{"vendor": v, "id": i, "expected": account_key(salt, v, i)}
            for v, i in (("codex", "acct-1"), ("claude", "org-1:user-1"), ("claudeDesktop", "org-1"))]
    merge = []
    for d, c, t in (("org-1", "org-1", "claude_max"), ("org-1", "org-1", "claude_team"), ("org-1", "org-2", "claude_pro"),
                    ("org-1", "org-1", None), ("org-1", "org-1", "claude_enterprise"), (None, None, "claude_max")):
        merge.append({"desktopOrg": d, "cliOrg": c, "cliOrgType": t, "expected": desktop_shares_cli(d, c, t)})
    plans = [{"value": v, "expected": clean_plan(v)}
             for v in ("promax", "claude_max", "default_claude_max_20x", "Pro Max", "x" * 65, None, 3)]
    ch = Chains()
    lines = [["5h@1", "7d@9"], ["5h@2", "7d@9"], ["5h@3", "7d@10"], ["5h@3", "7d@11"]]
    roots = [ch.link(l) for l in lines]
    names = {}
    chains = [names.setdefault(r, "chain%d" % len(names)) for r in [ch.find(r) for r in roots]]
    return {"salt": salt.hex(), "keys": keys, "desktopMerge": merge, "plans": plans,
            "chains": {"lines": lines, "expected": chains}}


def plan_cases():
    """A plan change moves the account to another cap class: evidence stays with the plan it was gathered under."""
    rows = [{"at": T0 + i * HOUR, "cost": 1_000_000} for i in range(1, 100)]
    led = ledger_from(rows)
    R1 = T0 + 7 * DAY
    old = [obs(p, T0 + h * HOUR, R1) for p, h in ((10, 21), (20, 41), (30, 61))]
    for o in old:
        o["plan"] = "plus"
    new = [obs(p, T0 + h * HOUR, R1 + 3 * DAY) for p, h in ((5, 80), (8, 96))]
    for o in new:
        o["plan"] = "pro"
    classes = {}
    for group in (old, new):
        for inst in instances(group):
            b = instance_bounds(segments(inst, led, {"a"}, None), inst["R"])
            classes.setdefault(class_of("a", group[0]["plan"], None, "sevenDay::10080"), []).append(b)
    out = {}
    for k, segs in classes.items():
        e = estimate(segs, T0 + 100 * HOUR, 7 * DAY)
        out[k[1]] = {"cap": micro(e["cap"]), "low": micro(e["low"]), "high": micro(e["high"]),
                     "confidence": e["confidence"]}
    return {"rows": rows, "old": old, "new": new, "expected": out}


def live_cases():
    R = T0 + 7 * DAY
    S = R - 10080 * MINUTE
    rows = [{"at": S + i * HOUR, "cost": 2_000_000} for i in range(1, 200)]
    led = ledger_from(rows)
    cases = []
    for label, p, t, now, cap, kind, minutes, first in (
            ("extrapolated", 20, S + 50 * HOUR, S + 60 * HOUR, 400_000_000, "sevenDay", 10080, None),
            ("below 0.5 points: not marked", 20, S + 50 * HOUR, S + 50 * HOUR + 30 * MINUTE, 400_000_000, "sevenDay",
             10080, None),
            ("no cap: vendor value", 20, S + 50 * HOUR, S + 60 * HOUR, None, "sevenDay", 10080, None),
            ("weekly after reset", 90, S + 160 * HOUR, R + 5 * HOUR, 400_000_000, "sevenDay", 10080, None),
            ("five-hour after reset, no use yet", 50, R - HOUR, R + HOUR, 10_000_000, "fiveHour", 300, None),
            ("five-hour after reset, first use", 50, R - HOUR, R + 2 * HOUR, 10_000_000, "fiveHour", 300,
             R + 30 * MINUTE + 1234)):
        s = R - minutes * MINUTE
        live, est, resets = live_percent(p, t, R, s, minutes, kind, cap, led, {"a"}, None, now, first)
        cases.append({"name": label, "p": p, "t": t, "R": R, "now": now, "cap": cap, "kind": kind, "minutes": minutes,
                      "firstAfterReset": first, "expected": {"live": round(live, 6), "estimated": est,
                                                            "resets": resets}})
    return {"rows": rows, "cases": cases}


def report_cases():
    R = T0 + 7 * DAY
    S = R - 10080 * MINUTE
    rows = [{"at": S + i * HOUR + 7, "cost": 1_234_567 + i, "model": m}
            for i, m in zip(range(1, 60), ["gpt-5.5", "gpt-5.5", "gpt-5.4-mini"] * 20)]
    rows.append({"at": S + 30 * HOUR, "cost": None, "model": "gpt-x"})
    led = ledger_from(rows)
    est = {"confidence": "medium", "cap": 512_345_678.4, "low": 480_000_000.5, "high": 546_000_000.49, "windows": 2}
    base = {"source": "codex", "sources": ["codex"], "plan": "promax", "tier": None, "kind": "sevenDay", "name": None,
            "minutes": 10080, "p": 23.456, "t": S + 50 * HOUR, "R": R, "S": S, "accounts": {"a"}, "estimate": est}
    entries = []
    for label, now in (("live", S + 55 * HOUR + 12_345), ("reset passed", R + HOUR)):
        entries.append({"name": label, "now": now, "expected": report_entry(base, now, led, 8)})
    none_est = dict(base, estimate={"confidence": "none", "windows": 0})
    entries.append({"name": "no estimate", "now": S + 55 * HOUR,
                    "expected": report_entry(none_est, S + 55 * HOUR, led, 8)})
    a = entries[0]["expected"]
    b = json.loads(json.dumps(a))
    b["usedMicroUsd"] += 5
    c = json.loads(json.dumps(a))
    c["usedPercent"] = 24.0
    cadence = [
        {"name": "first report", "lastSentMsAgo": None, "change": "none", "expected": should_send([a], None, 0, 0)},
        {"name": "spend only, 10 s", "lastSentMsAgo": 10_000, "change": "spend",
         "expected": should_send([b], [a], 0, 10_000)},
        {"name": "spend only, 60 s", "lastSentMsAgo": 60_000, "change": "spend",
         "expected": should_send([b], [a], 0, 60_000)},
        {"name": "vendor percent changed", "lastSentMsAgo": 5_000, "change": "usedPercent",
         "expected": should_send([c], [a], 0, 5_000)},
        {"name": "entry added", "lastSentMsAgo": 5_000, "change": "set", "expected": should_send([a, dict(
            b, sources=["claudeCode"])], [a], 0, 5_000)},
        {"name": "leaving", "lastSentMsAgo": 1_000, "change": "none", "expected": should_send([a], [a], 0, 1_000, True)},
    ]
    return {"rows": rows, "input": dict(base, accounts=["a"]), "entries": entries, "cadence": cadence}


def build_cases():
    return {
        "_comment": "QUOTAS 10 and REPORTING-EXTENSIONS 11 cases, written by spec/tools/quota_estimate.py --write.",
        "constants": {"lagMs": LAG_MS, "hiPenalty": HI_PENALTY, "fullStep": FULL_STEP, "minWeight": MIN_WEIGHT,
                      "incrementStep": INCREMENT_STEP, "segmentsPerInstance": SEGMENTS_PER_INSTANCE,
                      "instancesPerClass": INSTANCES_PER_CLASS, "highWidth": HIGH_WIDTH,
                      "precisionFloor": PRECISION_FLOOR, "sendEveryMs": SEND_EVERY_MS},
        "resolution": [{"p": p, "expected": resolution(p)} for p in (23.0, 23, 23.5, 41.25, 41.257, 0.0)],
        "accounts": account_cases(),
        "segments": segment_cases(),
        "vote": vote_cases(),
        "weights": weight_cases(),
        "estimate": estimate_cases(),
        "plan": plan_cases(),
        "live": live_cases(),
        "report": report_cases(),
        "pipeline": pipeline_cases(),
    }


def pipeline_cases():
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import quota_estimate_pipeline as qp  # noqa: E402
    return qp.cases()


def canonical(obj):
    return json.dumps(obj, ensure_ascii=False, indent=1, sort_keys=False) + "\n"


def schema_check(entries):
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import report_reference as rr  # noqa: E402
    schema = rr.load_schema()
    errors = []
    for e in entries:
        errors += rr.schema_errors(e, schema["$defs"]["quotaEntry"], schema, closed=True)
    return errors


HAND = {
    # Worked by hand in QUOTAS 10.5: $1 at S + 10 i min + 30 s; observations 2 % (100, 130 min), 5 % (300), 12 % (610).
    "segments/0": [(400e6, 1000e6), (2900e6 / 6, 750e6), (6000e6 / 13, 6000e6 / 11), (5000e6 / 12, 4800e6 / 8)],
    "live": [25.0, 20.0, 20, 3.0, 0.0, 40.0],
    "cadence": [True, False, True, True, True, True],
    "weights": [1.0, 0.5, 0.5, 0.5, 0.015625],
    # QUOTAS 10.3: Opus 9 000 000 for 1000 tokens sets the rate; 500 unpriced tokens add 4 500 000.
    "pipeline/ledger": [9_000_000, [12_000_000, 1], 16_500_000],
    # QUOTAS 10.7: first response starts at R + 38 min 0 s (minute R + 37 min 47 s + 13 s), now R + 2 h.
    "pipeline/live": [(None, 0.0, False, None), (None, 3.0, True, 13_067_000)],
    "pipeline/store": {"observations": 256, "instances": 64, "keys": 1},
    "pipeline/cadence": ["entries", "absent", "entries", "absent", "entries", "entries", "entries", "[]", "absent",
                         "entries", "entries", "entries", "entries", "absent", "entries"],
}


def hand_errors(cases):
    errors = []
    got = [(s["lo"], s["hi"]) for s in cases["segments"][0]["expected"]["segments"]]
    for (lo, hi), (elo, ehi) in zip(got, HAND["segments/0"]):
        if abs(lo - elo) > 0.01 or abs(hi - ehi) > 0.01:
            errors.append("segments/0: %r != %r" % ((lo, hi), (elo, ehi)))
    if [c["expected"]["live"] for c in cases["live"]["cases"]] != HAND["live"]:
        errors.append("live percent differs from the worked values")
    if [c["expected"] for c in cases["report"]["cadence"]] != HAND["cadence"]:
        errors.append("send cadence differs from the worked values")
    if [c["expected"] for c in cases["weights"]] != HAND["weights"]:
        errors.append("weights differ from the worked values")
    pl = cases["pipeline"]
    s = pl["ledger"][0]["expected"]["sums"][0]
    if [s["inner"], s["outer"], s["outerHigh"]] != HAND["pipeline/ledger"]:
        errors.append("pipeline ledger sums differ from the worked values")
    live = [tuple(e["expected"]["entries"][0][f] for f in ("usedPercent", "livePercent", "liveEstimated", "resetsInMs"))
            for e in pl["live"][:2]]
    if live != HAND["pipeline/live"]:
        errors.append("pipeline live percent differs from the worked values")
    if pl["store"][2]["expected"]["storeCounts"] != HAND["pipeline/store"]:
        errors.append("store limits differ")
    if pl["store"][0]["expected"]["estimates"] != pl["store"][1]["expected"]["estimates"]:
        errors.append("the stored file does not give the same estimate back")
    cut = list(pl["loweredCap"][0]["expected"]["estimates"].values())[0]
    if not 59e6 < cut["cap"] < 61e6:
        errors.append("lowered cap not followed")
    if pl["cadence"]["expected"] != HAND["pipeline/cadence"]:
        errors.append("pipeline cadence differs from the worked values")
    return errors


def check():
    expected = canonical(build_cases())
    actual = CASES.read_text(encoding="utf-8") if CASES.exists() else ""
    if actual != expected:
        print("quota-estimate-cases.json differs from the reference (run --write after reviewing)", file=sys.stderr)
        return 1
    cases = json.loads(expected)
    piped = [e for group in cases["pipeline"].values() if isinstance(group, list)
             for c in group for e in c["expected"].get("entries", [])]
    errors = hand_errors(cases) + schema_check([e["expected"] for e in cases["report"]["entries"]] + piped)
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    pipeline = ", ".join("%s %d" % (k, len(v) if isinstance(v, list) else len(v["steps"]))
                         for k, v in cases["pipeline"].items())
    print("quota estimate fixtures: ok (%d segment, %d vote, %d live, %d report cases; pipeline: %s)" % (
        len(cases["segments"]), len(cases["vote"]), len(cases["live"]["cases"]), len(cases["report"]["entries"]),
        pipeline))
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check-fixtures", action="store_true")
    parser.add_argument("--write", action="store_true")
    args = parser.parse_args()
    if args.write:
        CASES.write_text(canonical(build_cases()), encoding="utf-8")
        print("wrote", CASES.relative_to(ROOT))
        return 0
    return check()


if __name__ == "__main__":
    sys.exit(main())
