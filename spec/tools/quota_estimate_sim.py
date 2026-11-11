#!/usr/bin/env python3
"""Simulation study of the quota estimate (QUOTAS 10.9): synthetic accounts with known caps.

Each account works in sessions of one to three parallel agents. The vendor counts a response's cost when it ends and
reports the percent of the window, quantized, as of the response's start plus a small lag; the observation is
written when the response ends (the Codex pattern). Contaminated windows add usage from another device that the
ledger never sees. The estimator sees only the ledger and the observations.

    python3 spec/tools/quota_estimate_sim.py [--accounts N] [--seed S]
"""

from __future__ import annotations

import argparse
import bisect
import math
import random
import statistics
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import quota_estimate as qe  # noqa: E402

MIN = qe.MINUTE
CHECKPOINTS = (5, 10, 20, 40, 60)
MODELS = (("model-a", 1.0), ("model-b", 0.25))


def quantize(x, step, mode):
    v = x / step
    v = math.floor(v) if mode == "floor" else math.floor(v + 0.5)
    return round(v * step, 6)


class Account:
    def __init__(self, rng, cap, minutes, step, mode, contaminated, unseen, vendor_weight_b=1.0, lag_s=20, offset=0.0):
        self.rng, self.cap, self.minutes, self.step, self.mode = rng, cap, minutes, step, mode
        self.contaminated, self.unseen, self.vendor_weight_b, self.lag_s = contaminated, unseen, vendor_weight_b, lag_s
        self.offset = offset
        self.ledger = qe.Ledger()
        self.windows = []

    def simulate_window(self, start, target):
        """One window: local responses until the vendor's total reaches `target` of the cap (or the window ends)."""
        rng, period = self.rng, self.minutes * MIN
        end = start + period
        events = []  # (endMs, startMs, vendorCost, ledgerCost, model, local)
        budget = target * self.cap
        mix_b = rng.uniform(0.1, 0.7)
        total = 0.0
        session_gap = period / rng.uniform(4, 14)
        t = start + rng.uniform(0, session_gap * 0.5)
        mean_cost = self.cap / rng.uniform(400, 3000)
        while total < budget and t < end - 10 * MIN:
            agents = rng.choice((1, 1, 2, 3))
            length = rng.uniform(20, 180) * MIN
            for _ in range(agents):
                a = t + rng.uniform(0, 3 * MIN)
                while a < t + length and a < end - MIN and total < budget * 1.02:
                    dur = min(8 * MIN, rng.lognormvariate(math.log(40_000), 0.9))
                    model = "model-b" if rng.random() < mix_b else "model-a"
                    price = 0.25 if model == "model-b" else 1.0
                    cost = mean_cost * price * rng.lognormvariate(0, 0.8) / 0.8
                    vcost = cost * (self.vendor_weight_b if model == "model-b" else 1.0)
                    events.append((a + dur, a, vcost, int(cost), model, True))
                    total += vcost
                    a += dur + rng.uniform(5_000, 90_000)
            t += length + rng.expovariate(1 / session_gap)
        if self.contaminated:
            local = sum(e[2] for e in events)
            other = local * self.unseen / (1 - self.unseen)
            n = max(1, int(rng.uniform(3, 30)))
            bursts = sorted(rng.uniform(start, min(end, max(e[0] for e in events) if events else end)) for _ in range(n))
            for b in bursts:
                events.append((b, b - 30_000, other / n, 0, "model-a", False))
        events.sort()
        for e in events:
            if e[5]:
                self.ledger.add("acc", "codex", e[4], int(e[0]), e[3], tokens=int(e[3] / 2))
        ends = [e[0] for e in events]
        cum = []
        c = 0.0
        for e in events:
            c += e[2]
            cum.append(c)

        skipped = self.cap * rng.uniform(0.03, 0.10) * (self.offset > 0)

        def used(at):
            i = bisect.bisect_right(ends, at)
            return max(0.0, (cum[i - 1] if i else 0.0) - skipped)

        observations = []
        for e in events:
            if not e[5]:
                continue
            seen_at = e[1] + rng.uniform(0, self.lag_s * 1000)
            p = min(100.0, quantize(100.0 * used(seen_at) / self.cap, self.step, self.mode))
            observations.append({"source": "codex", "kind": "sevenDay" if self.minutes >= 10_000 else "fiveHour",
                                 "name": None, "minutes": self.minutes, "p": p, "t": int(e[0]), "tLo": int(e[1]),
                                 "R": int(end)})
        self.windows.append({"S": start, "R": end, "obs": observations, "cap": self.cap})


def evaluate(acc, rows, label):
    """Estimate at each checkpoint of each window, with the closed windows before it as history."""
    closed = []
    for wi, w in enumerate(acc.windows):
        hl = qe.half_life_ms(acc.minutes)
        done = set()
        for o in w["obs"]:
            for cp in CHECKPOINTS:
                if cp in done or o["p"] < cp:
                    continue
                done.add(cp)
                now = o["t"]
                upto = [x for x in w["obs"] if x["t"] <= now]
                segs = list(closed)
                for inst in qe.instances(upto):
                    b = qe.instance_bounds(qe.segments(inst, acc.ledger, {"acc"}, None, now_ms=now), inst["R"])
                    if b:
                        segs.append(b)
                e = qe.estimate(segs, now, hl)
                rows.append({"label": label, "window": wi, "cp": cp, "est": e, "truth": w["cap"]})
        for inst in qe.instances(w["obs"]):
            b = qe.instance_bounds(qe.segments(inst, acc.ledger, {"acc"}, None), inst["R"])
            if b:
                closed.append(b)
        closed = qe.prune_closed(closed)


def run_scenario(label, n_accounts, seed, step, unseen_share, mode="round", minutes=10080, vendor_weight_b=1.0,
                 windows=4, lag_s=20, cap_change=1.0, offset=0.0):
    rng = random.Random(seed)
    rows = []
    for _ in range(n_accounts):
        cap = rng.uniform(50, 2000) * 1_000_000 * (minutes / 10080) ** 0.5
        acc = Account(rng, cap, minutes, step, mode, False, 0.0, vendor_weight_b, lag_s, offset)
        start = 1_794_000_000_000
        for _w in range(windows):
            if _w == 2:
                acc.cap *= cap_change
            acc.contaminated = rng.random() < unseen_share
            acc.unseen = rng.uniform(0.2, 0.6)
            acc.simulate_window(start, rng.uniform(0.65, 1.0))
            start += minutes * MIN + int(rng.uniform(0, 2) * 3600_000)
        evaluate(acc, rows, label)
    return rows


def summarize(rows):
    def stats(sel):
        errs = sorted(abs(r["est"]["cap"] / r["truth"] - 1) for r in sel if r["est"]["cap"])
        if not errs:
            return None
        p90 = errs[min(len(errs) - 1, int(math.ceil(0.9 * len(errs))) - 1)]
        return statistics.median(errs), p90, len(errs)

    def covered(sel):
        hs = [r for r in sel if r["est"]["confidence"] == "high"]
        if not hs:
            return None, 0
        ok = sum(1 for r in hs if r["est"]["low"] <= r["truth"] <= (r["est"]["high"] or math.inf))
        return ok / len(hs), len(hs)

    out = []
    for w in (0, 1, 2, 3):
        for cp in CHECKPOINTS:
            sel = [r for r in rows if r["window"] == w and r["cp"] == cp]
            st = stats(sel)
            cov, nh = covered(sel)
            out.append((w + 1, cp, st, cov, nh, len(sel)))
    return out


def cell(rows, window, cp):
    errs = sorted(abs(r["est"]["cap"] / r["truth"] - 1) for r in rows
                  if r["window"] == window and r["cp"] == cp and r["est"]["cap"])
    if not errs:
        return "-"
    p90 = errs[min(len(errs) - 1, int(math.ceil(0.9 * len(errs))) - 1)]
    return "%.1f / %.1f" % (100 * statistics.median(errs), 100 * p90)


def coverage(rows):
    """Share of high estimates whose published range holds the truth, and how often windows 3-4 are high."""
    hs = [r for r in rows if r["est"]["confidence"] == "high"]
    if not hs:
        return "-", "0%"
    ok = sum(1 for r in hs if r["est"]["low"] <= r["truth"] <= (r["est"]["high"] or math.inf))
    later = [r for r in rows if r["window"] >= 2]
    share = sum(1 for r in later if r["est"]["confidence"] == "high") / max(1, len(later))
    return "%.1f%% (%d)" % (100 * ok / len(hs), len(hs)), "%.0f%%" % (100 * share)


def print_table(results, detail=False):
    print("Relative error of the cap, median / 90th percentile in %, when the window's vendor percent first reaches "
          "the given value; window n has n - 1 closed windows before it.\n")
    print("| scenario | w1 at 5 % | w1 at 20 % | w1 at 60 % | w2 at 20 % | w3 at 20 % | w4 at 20 % "
          "| high covers truth | high share, w3-w4 |")
    print("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
    for label, rows in results:
        cov, share = coverage(rows)
        print("| %s | %s | %s | %s | %s | %s | %s | %s | %s |" % (
            label, cell(rows, 0, 5), cell(rows, 0, 20), cell(rows, 0, 60), cell(rows, 1, 20), cell(rows, 2, 20),
            cell(rows, 3, 20), cov, share))
    if detail:
        for label, rows in results:
            print("\n## %s" % label)
            for w, cp, st, cov, nh, n in summarize(rows):
                if st:
                    print("w%d p%d: %.2f%% / %.2f%%, high %d/%d, covers %s" % (
                        w, cp, 100 * st[0], 100 * st[1], nh, n, "-" if cov is None else "%.1f%%" % (100 * cov)))


SCENARIOS = (
    ("clean, integer percent", dict(step=1.0, unseen_share=0.0)),
    ("clean, 0.1 % resolution", dict(step=0.1, unseen_share=0.0)),
    ("clean, integer percent floored by the vendor", dict(step=1.0, unseen_share=0.0, mode="floor")),
    ("20 % of windows with unseen usage", dict(step=1.0, unseen_share=0.2)),
    ("50 % of windows with unseen usage", dict(step=1.0, unseen_share=0.5)),
    ("vendor lag up to 3 minutes", dict(step=1.0, unseen_share=0.0, lag_s=180)),
    ("vendor leaves 3-10 % of each window's first spend uncounted", dict(step=1.0, unseen_share=0.0, offset=1.0)),
    ("vendor weights model-b 20 % above its API price", dict(step=1.0, unseen_share=0.0, vendor_weight_b=1.2)),
    ("five-hour windows, integer percent", dict(step=1.0, unseen_share=0.0, minutes=300)),
    ("cap raised 50 % from window 3, same plan", dict(step=1.0, unseen_share=0.0, cap_change=1.5)),
    ("cap cut by a third from window 3, same plan", dict(step=1.0, unseen_share=0.0, cap_change=2 / 3)),
)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--accounts", type=int, default=60)
    parser.add_argument("--seed", type=int, default=7)
    parser.add_argument("--only", default=None)
    parser.add_argument("--detail", action="store_true")
    args = parser.parse_args()
    results = [(label, run_scenario(label, args.accounts, args.seed + i, **kw)) for i, (label, kw) in
               enumerate(SCENARIOS) if not args.only or args.only in label]
    print_table(results, args.detail)
    return 0


if __name__ == "__main__":
    sys.exit(main())
