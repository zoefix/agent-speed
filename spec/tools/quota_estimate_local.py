#!/usr/bin/env python3
"""Runs the quota estimate (QUOTAS 10) over this computer's own logs, read-only, and prints aggregates only.

Codex: every rollout of the last 8 days (observations from token counts, spend from the responses, accounts from
shared window instances, 10.1). Claude: the CLI bridge's quota file and Claude Desktop's cached usage entries, with the
Claude Code and Desktop transcripts. Only plan names, window kinds and amounts are printed: never ids, keys or paths.

    python3 -I spec/tools/quota_estimate_local.py
"""

from __future__ import annotations

import bisect
import json
import os
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import prices as pricing  # noqa: E402
import quota_estimate as qe  # noqa: E402
import quota_reference as qr  # noqa: E402
import reference as ref  # noqa: E402

HOME = Path.home()
NOW = int(time.time() * 1000)
SINCE = NOW - qe.KEEP_MS


def price_of(index, r):
    res = index.resolve(r.get("model"), r.get("provider"))
    model = res.entry["id"] if res.kind == "priced" else (res.kind if res.kind == "free" else "unknown")
    return model, index.price(r)


def codex_lines(path):
    lines = ref.zst_stream_lines(path) if path.endswith(".zst") else ref.read_lines(path)
    for raw in lines:
        if isinstance(raw, bytes):
            yield raw.decode("utf-8", "replace")


def codex_observations(path):
    out = []
    for line in codex_lines(path):
        if '"rate_limits"' not in line:
            continue
        try:
            obj = json.loads(line)
        except ValueError:
            continue
        payload = obj.get("payload") or {}
        rl = payload.get("rate_limits")
        if payload.get("type") != "token_count" or not isinstance(rl, dict) or rl.get("limit_id") not in (None, "codex"):
            continue
        try:
            ts = qr.parse_ts(obj.get("timestamp"))
        except (AttributeError, TypeError, ValueError):
            continue
        windows = qr.codex_windows(rl, ts)
        if windows:
            out.append({"t": ts, "plan": qe.clean_plan(rl.get("plan_type")), "windows": windows})
    return out


def codex(index):
    files = [p for _, p, src, _ in ref.startup_files(str(HOME / ".codex" / "sessions"), "/nonexistent", "/nonexistent",
                                                     limit=10 ** 9, include_archived=True)
             if src == "codex" and os.path.getmtime(p) * 1000 >= SINCE - qe.DAY]
    dedupe = ref.Deduper(limit=10 ** 9)
    chains = qe.Chains()
    observations, responses = [], []
    for path in files:
        state = ref.FileState(path, "codex")
        ref.process_file(state, dedupe)
        rs = sorted(state.responses.values(), key=lambda r: r["endMs"])
        ends = [r["endMs"] for r in rs]
        for o in codex_observations(path):
            i = max(0, min(len(rs) - 1, bisect.bisect_left(ends, o["t"] - 5_000)))
            match = rs[i] if rs and abs(rs[i]["endMs"] - o["t"]) <= 5_000 else None
            o["tLo"] = min(o["t"], match["startMs"]) if match and match.get("startMs") else o["t"]
            tags = [qe.instance_tag(qe.window_id(w["kind"], w.get("name"), w["minutes"]), w["resetsAtMs"])
                    for w in o["windows"] if w.get("resetsAtMs")]
            if not tags:
                continue
            o["chain"] = chains.link(tags)
            observations.append(o)
            if match is not None:
                match["_chain"] = o["chain"]
        responses += rs
    ledger = qe.Ledger()
    unattributed = 0
    for r in responses:
        if r["endMs"] < SINCE:
            continue
        if "_chain" not in r:
            unattributed += 1
            continue
        model, cost = price_of(index, r)
        ledger.add(chains.find(r["_chain"]), "codex", model, r["endMs"], cost, tokens=r["input"] + r["output"])
    known = {}
    for o in sorted(observations, key=lambda o: o["t"]):
        if o["plan"]:
            known[chains.find(o["chain"])] = o["plan"]
    obs = []
    for o in observations:
        o["plan"] = o["plan"] or known.get(chains.find(o["chain"]))
        for w in o["windows"]:
            if w.get("resetsAtMs") and o["t"] >= SINCE:
                obs.append({"account": chains.find(o["chain"]), "source": "codex", "kind": w["kind"],
                            "name": w.get("name"), "minutes": w["minutes"], "p": float(w["usedPercent"]),
                            "t": o["t"], "tLo": o["tLo"], "R": w["resetsAtMs"], "plan": o["plan"], "tier": None})
    return obs, ledger, {"files": len(files), "responses": len(responses), "unattributed": unattributed,
                         "accounts": len({o["account"] for o in obs})}


def claude(index):
    try:
        cfg = json.loads((HOME / ".claude.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        cfg = {}
    oa = cfg.get("oauthAccount") if isinstance(cfg.get("oauthAccount"), dict) else {}
    cli_org, cli_type, cli_tier = oa.get("organizationUuid"), oa.get("organizationType"), oa.get(
        "organizationRateLimitTier")
    del cfg
    snaps = []
    qfile = HOME / "Library" / "Application Support" / "AgentSpeed" / "quota" / "claude-cli.json"
    if qfile.exists():
        snap = qr.cli_snapshot(json.loads(qfile.read_text(encoding="utf-8")))
        if snap:
            snaps.append(("claudeCode", snap, None))
    cache = HOME / "Library" / "Application Support" / "Claude" / "Cache" / "Cache_Data"
    desktop_org = None
    if cache.is_dir():
        found = []
        names = sorted((e for e in os.scandir(cache) if e.name.endswith("_0")), key=lambda e: -e.stat().st_mtime)
        for e in names[:4000]:
            if e.stat().st_mtime * 1000 < SINCE:
                break
            with open(e.path, "rb") as f:
                head = f.read(4096)
            if b"/usage" not in head:
                continue
            with open(e.path, "rb") as f:
                data = f.read()
            parsed = qr.parse_entry(data, int(e.stat().st_mtime * 1000))
            if parsed:
                found.append(parsed)
        for parsed in found:
            org = parsed["url"].split("/organizations/")[1].split("/")[0]
            snap = {"observedMs": parsed["observedMs"], "windows": qr.desktop_windows(parsed["body"])}
            snaps.append(("claudeDesktop", snap, org))
            desktop_org = org
    def points(source):
        return [(snap["observedMs"], qe.window_id(w["kind"], w.get("name"), w["minutes"]), w["resetsAtMs"], w["minutes"])
                for src, snap, _ in snaps if src == source for w in snap["windows"] if w.get("resetsAtMs")]

    fp = qe.fingerprint(points("claudeDesktop"), points("claudeCode"), NOW)
    shared = qe.desktop_shares_cli(desktop_org, cli_org, cli_type, fp)
    files = [p for _, p, src, _ in ref.startup_files("/nonexistent", str(HOME / ".claude" / "projects"), "/nonexistent",
                                                     limit=10 ** 9)
             if os.path.getmtime(p) * 1000 >= SINCE - qe.DAY]
    dedupe = ref.Deduper(limit=10 ** 9)
    ledger = qe.Ledger()
    starts = []
    count = {"claudeCode": 0, "claudeDesktop": 0}
    for path in files:
        state = ref.FileState(path, "claudeCode")
        ref.process_file(state, dedupe)
        for r in state.responses.values():
            if r["endMs"] < SINCE:
                continue
            src = state.source if state.source in count else "claudeCode"
            count[src] += 1
            account = "cli" if (src == "claudeCode" or shared) else "desktop"
            model, cost = price_of(index, r)
            ledger.add(account, src, model, r["endMs"], cost, tokens=r["input"] + r["output"])
            if src == "claudeCode" and r.get("startMs"):
                starts.append((r["startMs"], r["endMs"]))
    obs = []
    plan = qe.clean_plan(cli_type)
    tier = qe.clean_plan(cli_tier)
    for src, snap, org in snaps:
        account = "cli" if (src == "claudeCode" or shared) else "desktop"
        t = snap["observedMs"]
        t_lo = t
        if src == "claudeCode":
            live = [s for s, e in starts if e >= t - qe.LAG_MS and s <= t]
            t_lo = min([t] + live)
        for w in snap["windows"]:
            if w.get("resetsAtMs"):
                obs.append({"account": account, "source": src, "kind": w["kind"], "name": w.get("name"),
                            "minutes": w["minutes"], "p": float(w["usedPercent"]), "t": t, "tLo": t_lo,
                            "R": w["resetsAtMs"], "plan": plan if account == "cli" else None,
                            "tier": tier if account == "cli" else None})
    return obs, ledger, {"files": len(files), "responses": count, "desktopSharesCli": shared, "fingerprint": fp,
                         "snapshots": len(snaps)}


def report(label, obs, ledger):
    by_class = {}
    for o in obs:
        wid = qe.window_id(o["kind"], o["name"], o["minutes"])
        by_class.setdefault(qe.class_of(o["account"], o["plan"], o["tier"], wid), []).append(o)
    rows = []
    for cls, items in by_class.items():
        per_account = {}
        for o in items:
            per_account.setdefault(o["account"], []).append(o)
        bounds, current = [], None
        for account, os_ in per_account.items():
            name = os_[0]["name"] if os_[0]["kind"] == "modelWeek" else None
            for inst in qe.instances(os_):
                segs = qe.segments(inst, ledger, {account}, name, now_ms=NOW)
                b = qe.instance_bounds(segs, inst["R"])
                if b:
                    bounds.append(b)
                last = inst["obs"][-1]
                if current is None or last["t"] > current[1]["t"]:
                    current = (inst, last, account, name)
        e = qe.estimate(bounds, NOW, qe.half_life_ms(items[0]["minutes"]))
        inst, last, account, name = current
        observed = ledger.outer({account}, name, inst["S"], last["t"])[0]
        used = ledger.outer({account}, name, inst["S"], NOW)[0]
        coverage = None
        if e["cap"] and last["p"] >= 5:
            coverage = min(1.0, observed / (e["cap"] * last["p"] / 100))
        rows.append((label, items[0]["plan"] or "-", items[0]["tier"] or "-", qe.window_id(
            items[0]["kind"], items[0]["name"], items[0]["minutes"]), len(per_account), len(bounds), e, last["p"],
            round((NOW - last["t"]) / 3_600_000, 1), used, coverage, inst["R"] > NOW))
    return rows


def usd(m):
    return "-" if m is None else "$%.0f" % (m / 1e6)


def main():
    index = pricing.Index(pricing.load(pricing.TABLE))
    out = []
    obs, ledger, info = codex(index)
    print("codex:", info)
    out += report("codex", obs, ledger)
    obs, ledger, info = claude(index)
    print("claude:", info)
    out += report("claude", obs, ledger)
    print("| vendor | plan | tier | window | accounts | windows | cap | range | confidence | last p | age h "
          "| used now | coverage |")
    for label, plan, tier, wid, accs, n, e, p, age, used, cov, open_ in out:
        print("| %s | %s | %s | %s | %d | %d | %s | %s–%s | %s | %s%% | %s | %s | %s |" % (
            label, plan, tier, wid, accs, n, usd(e["cap"]), usd(e["low"]), usd(e["high"]), e["confidence"], p, age,
            usd(used) if open_ else "(reset)", "-" if cov is None else "%.2f" % cov))
    return 0


if __name__ == "__main__":
    sys.exit(main())
