"""The quota estimate pipeline (QUOTAS 10.1-10.8, REPORTING-EXTENSIONS 11.3): raw events to report entries.

An Engine takes responses, Codex token count lines, Claude profile and Codex sign-in changes, and quota snapshots,
resolves accounts (keys, Codex chains, the Desktop merge rule), fills the ledger, builds observations, instances and
cap classes, persists them (estimate.json) and produces report entries. A Reporter applies the send cadence.
Used by quota_estimate.py for the `pipeline` fixture cases.
"""

from __future__ import annotations

import bisect
import json
import math

import quota_estimate as qe
import quota_reference as qr
import report_reference as rr

SOURCE_ORDER = rr.SOURCES
KIND_ORDER = ("fiveHour", "other", "sevenDay", "modelWeek", "month", "credits")
VENDOR = {"codex": "openai", "claudeCode": "anthropic", "claudeDesktop": "anthropic"}
LINE_MATCH_MS = 5_000
OBSERVATIONS_PER_INSTANCE = 256
MAX_ENTRIES = 16
STORE_VERSION = 2
FIVE_HOUR = qe.window_id("fiveHour", None, 300)


def source_rank(source):
    return SOURCE_ORDER.index(source) if source in SOURCE_ORDER else len(SOURCE_ORDER)


def window_rank(w, name_order):
    if w["name"]:
        return (1, name_order.index(w["name"]), w["minutes"] or 1 << 30, KIND_ORDER.index(w["kind"]))
    return (0, 0, w["minutes"] or 1 << 30, KIND_ORDER.index(w["kind"]))


def in_force(timeline, at):
    """The entry in force at `at`: the last one at or before it, else the first one (10.1 key timeline)."""
    if not timeline:
        return None
    i = bisect.bisect_right([e[0] for e in timeline], at)
    return timeline[i - 1] if i else timeline[0]


def thin(observations):
    """Store compression (10.4): each run of equal values keeps its first and last observation; above 256, each run
    keeps its last; still above, 256 runs evenly spaced, the first and last included."""
    runs = []
    for o in sorted(observations, key=lambda o: (o["t"], o["tLo"])):
        if runs and runs[-1][-1]["p"] == o["p"]:
            runs[-1].append(o)
        else:
            runs.append([o])
    kept = [o for r in runs for o in ([r[0], r[-1]] if len(r) > 1 else r)]
    if len(kept) <= OBSERVATIONS_PER_INSTANCE:
        return kept
    lasts = [r[-1] for r in runs]
    if len(lasts) <= OBSERVATIONS_PER_INSTANCE:
        return lasts
    n = OBSERVATIONS_PER_INSTANCE
    return [lasts[round(i * (len(lasts) - 1) / (n - 1))] for i in range(n)]


class Engine:
    def __init__(self, salt: bytes, price_version=8, install="", origin=""):
        self.salt = salt
        self.install = install
        self.origin = origin
        self.price_version = price_version
        self.responses = []
        self.lines = []
        self.codex_auth = []
        self.claude_profiles = []
        self.snapshots = []
        self.stored = {"observations": [], "instances": [], "keys": []}

    # input

    def add(self, event):
        kind = event["type"]
        if kind == "response":
            self.responses.append(event)
        elif kind == "tokenCount":
            self.lines.append(event)
        elif kind == "codexAuth":
            self.codex_auth.append((event["at"], qe.account_key(self.salt, "codex", event["accountId"])))
        elif kind == "claudeProfile":
            self.claude_profiles.append((event["at"], event))
        elif kind == "snapshot":
            self.snapshots.append(event)
        else:
            raise ValueError(kind)

    def load(self, store):
        if store.get("version") == STORE_VERSION:
            self.stored = {k: list(store.get(k) or []) for k in ("observations", "instances", "keys")}

    # accounts

    def timelines(self):
        """Per source: [(at, accountKey, plan, tier)] from the events, merged with the stored timeline. For Desktop the
        stored and fresh entries are its organization's own keys; the keys it spends on are derived on every build."""
        tl = {"codex": [(at, key, None, None) for at, key in sorted(self.codex_auth)]}
        org_keys = {}
        for at, e in sorted(self.claude_profiles, key=lambda x: x[0]):
            entry = (at, qe.account_key(self.salt, "claude", "%s:%s" % (e["org"], e["account"])),
                     qe.clean_plan(e.get("orgType")), qe.clean_plan(e.get("tier")))
            tl.setdefault("claudeCode", []).append(entry)
            org_keys[entry] = qe.account_key(self.salt, "claudeDesktop", e["org"])
        for k in self.stored["keys"]:
            entry = (k["at"], k["key"], k.get("plan"), k.get("tier"))
            tl.setdefault(k["source"], []).append(entry)
            if k["source"] == "claudeCode" and k.get("orgKey"):
                org_keys.setdefault(entry, k["orgKey"])
        for s in self.snapshots:
            if s["source"] == "claudeDesktop":
                tl.setdefault("claudeDesktop", []).append(
                    (s["observedMs"], qe.account_key(self.salt, "claudeDesktop", s["org"]), None, None))
        tl = {s: sorted(set(v), key=lambda e: (e[0], e[1], str(e[2]), str(e[3]))) for s, v in tl.items()}
        tl.setdefault("claudeCode", [])
        self.org_keys = org_keys
        self.desktop_orgs = tl.pop("claudeDesktop", [])
        tl["claudeDesktop"] = self.desktop_timeline(tl["claudeCode"])
        return tl

    def window_points(self, source):
        """(t, windowId, R, minutes, account or None) of every fresh and stored observation of a Claude source."""
        out = []
        for s in self.snapshots:
            if s["source"] == source:
                out += [(s["observedMs"], qe.window_id(w["kind"], w.get("name"), w["minutes"]), w["resetsAtMs"],
                         w["minutes"], None) for w in s["windows"] if w.get("resetsAtMs") is not None]
        out += [(o["t"], qe.window_id(o["kind"], o["name"], o["minutes"]), o["R"], o["minutes"], o["account"])
                for o in self.stored["observations"] if o["source"] == source]
        return out

    def desktop_timeline(self, cli):
        """10.1: an entry at each entry of Desktop's organization timeline and of the CLI's key timeline: the CLI's key
        while the organization matches, the plan is personal and the fingerprint of the two sources' windows confirms
        it, else Desktop's own organization key. Organizations are compared by their Desktop keys."""
        orgs = self.desktop_orgs
        if not orgs:
            return []
        desk_w = [p[:4] for p in self.window_points("claudeDesktop")]
        cli_w = [(p[:4], p[4] if p[4] is not None else (in_force(cli, p[0]) or (None, None))[1])
                 for p in self.window_points("claudeCode")]
        out = []
        for at in sorted({o[0] for o in orgs} | {c[0] for c in cli}):
            own = in_force(orgs, at)[1]
            c = in_force(cli, at)
            if c:
                fp = qe.fingerprint(desk_w, [p for p, key in cli_w if key == c[1]], at)
                if qe.desktop_shares_cli(own, self.org_keys.get(c), c[2], fp):
                    out.append((at, c[1], c[2], c[3]))
                    continue
            out.append((at, own, None, None))
        return out

    def codex(self, tl):
        """Codex token count lines: windows, the response each follows, chains and their accounts (10.1)."""
        by_file = {}
        for r in self.responses:
            if r["source"] == "codex":
                by_file.setdefault(r.get("file"), []).append(r)
        lines = []
        for ln in sorted(self.lines, key=lambda x: (x["file"], x["ts"])):
            ws = [w for w in qr.codex_windows(ln["rateLimits"], ln["ts"]) if w["resetsAtMs"]]
            if not ws:
                continue
            lines.append(dict(ln, windows=ws))
        clusters = {}
        for ln in lines:
            for w in ln["windows"]:
                clusters.setdefault(qe.window_id(w["kind"], w["name"], w["minutes"]), set()).add(w["resetsAtMs"])
        tag_of = {}
        for wid, rs in clusters.items():
            first = None
            for r in sorted(rs):
                if first is None or r - first > qe.INSTANCE_JOIN_MS:
                    first = r
                tag_of[(wid, r)] = "%s@%d" % (wid, first)
        chains = qe.Chains()
        for ln in lines:
            ln["tags"] = [tag_of[(qe.window_id(w["kind"], w["name"], w["minutes"]), w["resetsAtMs"])]
                          for w in ln["windows"]]
            chains.link(ln["tags"])
        used = set()
        for ln in lines:
            ln["tLo"] = ln["ts"]
            for r in sorted(by_file.get(ln["file"], []), key=lambda r: r["endMs"]):
                if id(r) not in used and r["endMs"] <= ln["ts"] <= r["endMs"] + LINE_MATCH_MS:
                    used.add(id(r))
                    r["_line"] = ln
                    ln["tLo"] = min(ln["ts"], r.get("startMs") or ln["ts"])
                    break
        keyed = {}
        for ln in sorted(lines, key=lambda x: x["ts"]):
            e = tl["codex"] and in_force(tl["codex"], ln["ts"])
            if e and ln["ts"] >= tl["codex"][0][0]:
                keyed[chains.find(ln["tags"][0])] = e[1]
        account = {}
        for ln in lines:
            root = chains.find(ln["tags"][0])
            account[id(ln)] = keyed.get(root) or qe.account_key(
                self.salt, "codex-chain", min(t for t in set(tag_of.values()) if chains.find(t) == root))
        plans = {}
        for ln in sorted(lines, key=lambda x: x["ts"]):
            p = qe.clean_plan(ln["rateLimits"].get("plan_type"))
            ln["plan"] = p
            if p:
                plans.setdefault(account[id(ln)], []).append((ln["ts"], p))
        for ln in lines:
            if ln["plan"] is None:
                known = plans.get(account[id(ln)], [])
                before = [p for t, p in known if t <= ln["ts"]]
                ln["plan"] = before[-1] if before else (known[0][1] if known else None)
        return lines, account

    # build

    def build(self):
        tl = self.timelines()
        lines, line_account = self.codex(tl)
        ledger = qe.Ledger()
        excluded = 0
        source_accounts = {}
        for r in self.responses:
            src = r["source"]
            if src == "codex":
                if r.get("provider") not in (None, "openai") or "_line" not in r:
                    excluded += 1
                    continue
                acc = line_account[id(r["_line"])]
            elif src in ("claudeCode", "claudeDesktop"):
                if r.get("cloudRoute"):
                    excluded += 1
                    continue
                e = in_force(tl.get(src, []), r["endMs"])
                if e is None:
                    excluded += 1
                    continue
                acc = e[1]
            else:
                acc = qe.pair_key(self.salt, self.install, r.get("machine", "local"), src)
            r["_account"] = acc
            ledger.add(acc, src, r["model"], r["endMs"], r.get("costMicroUsd"), tokens=r.get("tokens", 0))
            source_accounts.setdefault(acc, set()).add(src)
        obs = []
        for ln in lines:
            for w in ln["windows"]:
                obs.append({"account": line_account[id(ln)], "source": "codex", "kind": w["kind"], "name": w["name"],
                            "minutes": w["minutes"], "p": w["usedPercent"], "t": ln["ts"], "tLo": ln["tLo"],
                            "R": w["resetsAtMs"], "plan": ln["plan"], "tier": None})
        for s in self.snapshots:
            src, t = s["source"], s["observedMs"]
            e = in_force(tl.get(src, []), t)
            if e is None:
                continue
            t_lo = t
            if src == "claudeCode":
                live = [r["startMs"] for r in self.responses if r.get("_account") == e[1] and r["source"] == src
                        and r.get("startMs") is not None and r["startMs"] < t and r["endMs"] >= t - qe.LAG_MS]
                t_lo = min([t] + live)
            for w in s["windows"]:
                if w.get("resetsAtMs") is None:
                    continue
                obs.append({"account": e[1], "source": src, "kind": w["kind"], "name": w.get("name"),
                            "minutes": w["minutes"], "p": float(w["usedPercent"]), "t": t, "tLo": t_lo,
                            "R": w["resetsAtMs"], "plan": e[2], "tier": e[3]})
            source_accounts.setdefault(e[1], set()).add(src)
        seen = {(o["account"], o["kind"], o["name"], o["minutes"], o["t"], o["p"], o["R"]) for o in obs}
        for o in self.stored["observations"]:
            o = dict(o)
            if o["source"] == "claudeDesktop" and tl["claudeDesktop"]:
                e = in_force(tl["claudeDesktop"], o["t"])
                o["account"], o["plan"], o["tier"] = e[1], e[2], e[3]
            k = (o["account"], o["kind"], o["name"], o["minutes"], o["t"], o["p"], o["R"])
            if k not in seen:
                seen.add(k)
                obs.append(o)
                source_accounts.setdefault(o["account"], set()).add(o["source"])
        self.tl, self.ledger, self.obs, self.excluded, self.source_accounts = tl, ledger, obs, excluded, source_accounts
        return self

    def vendor_of(self, source):
        return VENDOR.get(source, source)

    def instances_at(self, now_ms):
        """Instances per (account, window, plan, tier) with their intervals and cap classes."""
        groups = {}
        for o in self.obs:
            if o["t"] < now_ms - qe.KEEP_MS:
                continue
            key = (o["account"], qe.window_id(o["kind"], o["name"], o["minutes"]), o["plan"], o["tier"])
            groups.setdefault(key, []).append(o)
        out = []
        for (account, wid, plan, tier), os_ in groups.items():
            name = os_[0]["name"] if os_[0]["kind"] == "modelWeek" else None
            vendor = self.vendor_of(os_[0]["source"])
            cls = qe.class_of(account, plan and "%s/%s" % (vendor, plan), tier, wid)
            for inst in qe.instances(os_):
                b = qe.instance_bounds(qe.segments(inst, self.ledger, {account}, name, now_ms=now_ms), inst["R"])
                out.append({"account": account, "wid": wid, "plan": plan, "tier": tier, "class": cls, "inst": inst,
                            "bounds": b, "name": name, "closed": now_ms >= inst["R"]})
        return out

    def class_items(self, now_ms, insts):
        items = {}
        have = set()
        for x in insts:
            if x["bounds"]:
                items.setdefault(x["class"], []).append(x["bounds"])
                have.add((x["class"], x["account"], x["inst"]["R"]))
        for s in self.stored["instances"]:
            cls = tuple(s["class"])
            if (cls, s["account"], s["R"]) in have:
                continue
            items.setdefault(cls, []).append({"R": s["R"], "lo": s["lo"], "hi": math.inf if s["hi"] is None else s["hi"],
                                              "dp": s["pMax"], "t": s["tLast"]})
        return {c: qe.prune_closed(v) for c, v in items.items()}

    def estimates(self, now_ms):
        insts = self.instances_at(now_ms)
        out = {}
        for cls, items in self.class_items(now_ms, insts).items():
            minutes = cls[-1].rsplit(":", 1)[1]
            out[cls] = qe.estimate(items, now_ms, qe.half_life_ms(int(minutes) if minutes.isdigit() else 0))
        return insts, out

    def inferred_plans(self, ests):
        """10.2: the inferred plan of each account Claude Desktop spends on as its own (not the CLI's), unless the CLI
        names that organization a team or enterprise one: {key: (plan, tier, rule)}."""
        own = {e[1] for e in self.desktop_orgs}
        team = {org for c, org in self.org_keys.items() if c[2] is not None and c[2] not in qe.PERSONAL_CLAUDE_PLANS}
        anchors = sorted(((cls[1].split("/", 1)[1], cls[2], e) for cls, e in ests.items()
                          if cls[0] == "plan" and cls[1].startswith("anthropic/") and cls[3] == FIVE_HOUR),
                         key=lambda a: (a[0], a[1] or ""))
        newest = {}
        for s in self.snapshots:
            if s["source"] == "claudeDesktop":
                k = qe.account_key(self.salt, "claudeDesktop", s["org"])
                if k not in newest or s["observedMs"] >= newest[k]["observedMs"]:
                    newest[k] = s
        out = {}
        for k in sorted(own - team):
            kinds = [w["kind"] for w in newest[k]["windows"]] if k in newest else []
            plan, tier, rule = qe.infer_claude_plan(kinds, ests.get(qe.class_of(k, None, None, FIVE_HOUR)), anchors)
            if plan:
                out[k] = (plan, tier, rule)
        return out

    def entries(self, now_ms):
        """Report entries (REPORTING-EXTENSIONS 11.2): one per account-window, its newest instance."""
        insts, ests = self.estimates(now_ms)
        inferred = self.inferred_plans(ests)
        current = {}
        for x in insts:
            k = (x["account"], x["wid"])
            last = x["inst"]["obs"][-1]
            if k not in current or (last["t"], last["tLo"]) > (current[k][1]["t"], current[k][1]["tLo"]):
                current[k] = (x, last)
        rows = []
        for (account, wid), (x, last) in current.items():
            inst = x["inst"]
            first_after = None
            if inst["obs"][0]["kind"] == "fiveHour":
                starts = [r["startMs"] for r in self.responses if r.get("_account") == account
                          and r.get("startMs") is not None and r["startMs"] >= inst["R"] and r["endMs"] <= now_ms]
                first_after = min(starts) if starts else None
            sources = sorted({r["source"] for r in self.responses if r.get("_account") == account
                              and r["endMs"] >= inst["S"]} | {last["source"]}, key=source_rank)
            plan, tier = (x["plan"], x["tier"]) if x["plan"] or account not in inferred else inferred[account][:2]
            acc = {"account": account, "source": last["source"], "sources": sources, "plan": plan, "tier": tier,
                   "kind": last["kind"], "name": last["name"], "minutes": last["minutes"], "p": last["p"],
                   "t": last["t"], "R": inst["R"], "S": inst["S"], "accounts": {account},
                   "estimate": ests.get(x["class"]) or {"confidence": "none", "windows": 0},
                   "firstAfterReset": first_after}
            names = []
            for o in sorted(self.obs, key=lambda o: o["t"]):
                if o["account"] == account and o["name"] and o["name"] not in names:
                    names.append(o["name"])
            e = qe.report_entry(acc, now_ms, self.ledger, self.price_version, self.origin)
            rows.append(((source_rank(sources[0]), window_rank(e["window"], names), last["source"], e["account"]), e))
        return [e for _, e in sorted(rows, key=lambda r: r[0])][:MAX_ENTRIES]

    # persistence (10.4)

    def store(self, now_ms):
        insts = self.instances_at(now_ms)
        observations = []
        groups = {}
        for o in self.obs:
            if o["t"] >= now_ms - qe.KEEP_MS:
                groups.setdefault((o["account"], o["kind"], o["name"], o["minutes"], round(o["R"] / qe.INSTANCE_JOIN_MS),
                                   o["plan"], o["tier"]), []).append(o)
        for k in sorted(groups, key=str):
            for o in thin(groups[k]):
                observations.append({f: o[f] for f in ("account", "source", "kind", "name", "minutes", "t", "tLo", "p",
                                                       "R", "plan", "tier")})
        closed = {}
        for x in insts:
            if x["closed"] and x["bounds"]:
                b = x["bounds"]
                closed.setdefault(x["class"], []).append(
                    {"class": list(x["class"]), "account": x["account"], "R": x["inst"]["R"], "lo": b["lo"],
                     "hi": None if not math.isfinite(b["hi"]) else b["hi"], "pMax": b["dp"], "tLast": b["t"]})
        have = {(tuple(i["class"]), i["account"], i["R"]) for v in closed.values() for i in v}
        for s in self.stored["instances"]:
            if (tuple(s["class"]), s["account"], s["R"]) not in have:
                closed.setdefault(tuple(s["class"]), []).append(dict(s))
        instances = []
        for cls in sorted(closed, key=str):
            instances += sorted(closed[cls], key=lambda s: s["tLast"])[-qe.INSTANCES_PER_CLASS:]
        timelines = dict(self.tl, claudeDesktop=self.desktop_orgs)
        keys = [dict({"source": src, "at": e[0], "key": e[1], "plan": e[2], "tier": e[3]},
                     **({"orgKey": self.org_keys[e]} if src == "claudeCode" and e in self.org_keys else {}))
                for src in sorted(timelines) for e in timelines[src]]
        return {"version": STORE_VERSION, "observations": observations, "instances": instances, "keys": keys}


class Reporter:
    """The send cadence of REPORTING-EXTENSIONS 11.3 across a run's reports."""

    def __init__(self):
        self.last = None
        self.last_ms = None
        self.cleared = True

    def new_run(self):
        self.__init__()

    def step(self, now_ms, entries, switch_on=True, leaving=False, fits=True):
        """Returns the `quotas` value of this report: None (absent) or a list."""
        if not switch_on:
            if self.cleared:
                return None
            self.cleared, self.last, self.last_ms = True, None, None
            return []
        due = qe.should_send(entries, self.last, self.last_ms or 0, now_ms, leaving)
        if not due or not fits:
            return None
        self.last, self.last_ms, self.cleared = entries, now_ms, False
        return entries


# Fixture cases (QUOTAS 11): small raw inputs, every rule a port implements

MINUTE, HOUR, DAY = qe.MINUTE, qe.HOUR, qe.DAY
BASE = qe.T0 + 17 * MINUTE + 13_000
SALT = qe.ACCOUNT_SALT
INSTALL = "00112233445566778899aabbccddeeff"
ORIGIN = "https://radar.example"
FABLE = "claude-fable-5-2"
OPUS = "claude-opus-5-5"


def sec(ms):
    return ms // 1000 * 1000


def resp(source, end, cost, model="gpt-5.5", start=None, file=None, tokens=1000, **extra):
    r = {"type": "response", "source": source, "file": file, "startMs": end - 20_000 if start is None else start,
         "endMs": end, "model": model, "costMicroUsd": cost, "tokens": tokens}
    r.update(extra)
    return r


def line(file, ts, plan, five=None, week=None):
    rl = {"limit_id": "codex", "plan_type": plan}
    for slot, w, minutes in (("primary", five, 300), ("secondary", week, 10080)):
        if w:
            rl[slot] = {"used_percent": w[0], "window_minutes": minutes, "resets_at": w[1] // 1000}
    return {"type": "tokenCount", "file": file, "ts": ts, "rateLimits": rl}


def codex_turn(file, end, cost, plan, five=None, week=None, model="gpt-5.5"):
    return [resp("codex", end, cost, model=model, file=file), line(file, end + 1_500, plan, five, week)]


def claude_windows(five=None, week=None, fable=None):
    out = []
    for kind, name, minutes, w in (("fiveHour", None, 300, five), ("sevenDay", None, 10080, week),
                                   ("modelWeek", "Fable", 10080, fable)):
        if w:
            out.append({"kind": kind, "name": name, "minutes": minutes, "usedPercent": w[0], "resetsAtMs": w[1]})
    return out


def snap(source, at, org=None, **windows):
    s = {"type": "snapshot", "source": source, "observedMs": at, "windows": claude_windows(**windows)}
    if org:
        s["org"] = org
    return s


def profile(at, org="org-1", account="user-1", org_type="claude_max", tier="default_claude_max_20x"):
    return {"type": "claudeProfile", "at": at, "org": org, "account": account, "orgType": org_type, "tier": tier}


def five_hour_codex(file, S, cap, steps, plan, cost=5_000_000, week=None):
    """`steps` codex turns 20 min apart in the 5h window that starts at S, each line with the integer percent."""
    R = sec(S + 300 * MINUTE)
    events, spent = [], 0
    for i in range(1, steps + 1):
        spent += cost
        end = S + i * 20 * MINUTE
        events += codex_turn(file, end, cost, plan, five=(spent * 100 // cap, R), week=week)
    return events


def fnum(x):
    if x is None or (isinstance(x, float) and not math.isfinite(x)):
        return None
    return round(x, 3) if isinstance(x, float) else x


def generated_store(g):
    t0, acc = g["t0"], g["account"]
    obs = [{"account": acc, "source": "claudeCode", "kind": "sevenDay", "name": None, "minutes": 10080,
            "t": t0 + i * MINUTE, "tLo": t0 + i * MINUTE, "p": round(i / 10, 1), "R": t0 + 6 * DAY,
            "plan": "claude_max", "tier": None} for i in range(1, g["observations"] + 1)]
    insts = [{"class": ["plan", "anthropic/claude_max", None, "fiveHour::300"], "account": acc,
              "R": t0 - i * 5 * HOUR, "lo": 1e8, "hi": 2e8, "pMax": 40.0, "tLast": t0 - i * 5 * HOUR - HOUR}
             for i in range(1, g["instances"] + 1)]
    return {"version": g["version"], "observations": obs, "instances": insts,
            "keys": [{"source": "claudeCode", "at": t0, "key": acc, "plan": "claude_max", "tier": None}]}


def run(inp):
    eng = Engine(bytes.fromhex(inp["salt"]), install=inp["install"], origin=inp["origin"])
    for e in inp["events"]:
        eng.add(dict(e))
    if "store" in inp:
        eng.load(inp["store"])
    if "generatedStore" in inp:
        eng.load(generated_store(inp["generatedStore"]))
    eng.build()
    now = inp["now"]
    want = inp.get("outputs", ["responses", "instances", "estimates", "entries"])
    out = {}
    if "responses" in want:
        out["responses"] = [r.get("_account") for r in eng.responses]
    if "sums" in want:
        out["sums"] = []
        for q in inp["sums"]:
            acc = {eng.responses[i]["_account"] for i in q["accountsOf"]}
            name = q.get("name")
            out["sums"].append({"inner": eng.ledger.inner(acc, name, q["a"], q["b"])[0],
                                "outer": eng.ledger.outer(acc, name, q["a"], q["b"]),
                                "outerHigh": fnum(eng.ledger.outer_high(acc, name, q["a"], q["b"]))})
    insts, ests = eng.estimates(now)
    if "instances" in want:
        rows = []
        for x in insts:
            b = x["bounds"]
            rows.append({"account": x["account"], "window": x["wid"], "plan": x["plan"], "tier": x["tier"],
                         "class": list(x["class"]), "R": x["inst"]["R"], "S": x["inst"]["S"],
                         "Shi": x["inst"]["Shi"], "observations": len(x["inst"]["obs"]),
                         "bounds": b and {"lo": fnum(b["lo"]), "hi": fnum(b["hi"]), "offset": b["offset"]}})
        out["instances"] = sorted(rows, key=lambda r: (r["account"], r["window"], r["R"], str(r["plan"])))
    if "estimates" in want:
        out["estimates"] = {"|".join(str(c) for c in k): {f: fnum(e.get(f)) for f in
                                                          ("cap", "low", "high", "confidence", "windows")}
                            for k, e in sorted(ests.items(), key=lambda kv: str(kv[0]))}
    if "plans" in want:
        out["plans"] = {k: dict(zip(("plan", "tier", "rule"), v)) for k, v in eng.inferred_plans(ests).items()}
    if "entries" in want:
        out["entries"] = eng.entries(now)
    if "store" in want:
        out["store"] = eng.store(now)
    if "storeCounts" in want:
        st = eng.store(now)
        out["storeCounts"] = {"observations": len(st["observations"]), "instances": len(st["instances"]),
                              "keys": len(st["keys"])}
    return out


def case(name, why, events, now, **extra):
    inp = {"salt": SALT.hex(), "install": INSTALL, "origin": ORIGIN, "events": events, "now": now}
    inp.update(extra)
    return {"name": name, "why": why, "input": inp, "expected": run(inp)}


def ledger_cases():
    t = BASE + HOUR
    ev = [profile(BASE),
          resp("claudeCode", t + 10_000, 3_000_000, model=FABLE, tokens=2000),
          resp("claudeCode", t + 70_000, 9_000_000, model=OPUS, tokens=1000),
          resp("claudeCode", t + 130_000, None, model="claude-mystery-1", tokens=500),
          resp("claudeCode", t + 140_000, 4_000_000, model="anthropic.claude-opus-5-5", cloudRoute="bedrock")]
    sums = [{"accountsOf": [0], "a": t, "b": t + 3 * MINUTE},
            {"accountsOf": [0], "a": t + 30_000, "b": t + 100_000},
            {"accountsOf": [0], "a": t, "b": t + 3 * MINUTE, "name": "Fable"}]
    cases = [case("priced, unpriced and cloud responses", "C- counts whole minutes inside, C+ every minute touched; the "
                  "unpriced response's 500 tokens cost the highest priced rate (Opus, 9000 micro-USD per token); a "
                  "Bedrock route is left out; modelWeek Fable counts only claude-fable-*", ev, t + HOUR,
                  outputs=["responses", "sums"], sums=sums)]
    ev = [profile(BASE), resp("claudeCode", t, None, model="claude-mystery-1", tokens=500)]
    cases.append(case("unpriced only", "with no priced model the upper bound is unbounded (null)", ev, t + HOUR,
                      outputs=["sums"], sums=[{"accountsOf": [0], "a": t - MINUTE, "b": t + MINUTE}]))
    ev = [resp("codex", t, 2_000_000, file="a"), line("a", t + 1_000, "plus", five=(1, sec(t + 4 * HOUR))),
          resp("codex", t + 60_000, 2_000_000, file="a", provider="azure"),
          line("a", t + 61_000, "plus", five=(2, sec(t + 4 * HOUR)))]
    cases.append(case("other Codex provider", "a response whose model_provider is not openai is no subscription spend",
                      ev, t + HOUR, outputs=["responses"]))
    return cases


def chain_cases():
    S1 = BASE
    S2 = BASE + 5 * HOUR + 30 * MINUTE
    W1, W2 = sec(BASE + 5 * DAY), sec(BASE + 6 * DAY + 7 * HOUR)
    ev = five_hour_codex("a1", S1, 100_000_000, 3, "pro", week=(10, W1))
    ev += five_hour_codex("a2", S2, 100_000_000, 2, "pro", week=(11, W1))
    ev += five_hour_codex("b1", S2 + 2 * HOUR, 100_000_000, 2, "pro", week=(40, W2))
    ev += [{"type": "codexAuth", "at": S2 + 2 * HOUR, "accountId": "acct-b"}]
    ev += [resp("codex", S2 + 3 * HOUR, 1_000_000, file="b1")]
    late = S2 + 4 * HOUR
    ev += [resp("codex", late, 1_000_000, file="b1"), line("b1", late + 6_000, "pro", five=(9, sec(S2 + 7 * HOUR)),
                                                            week=(41, W2))]
    return [case("two accounts switching", "account A's two 5h instances share one weekly instance, so they are one "
                 "chain; account B's lines name other instances and start after codex login gives B's key; A never "
                 "seen live keeps a chain key of its own; a response with no token count line (API key) and one whose "
                 "line comes 6 s later are left out", ev, S2 + 8 * HOUR, outputs=["responses", "instances"])]


def claude_cases():
    t = BASE + HOUR
    R5, R7 = BASE + 5 * HOUR, BASE + 6 * DAY
    out = []
    for label, org_type, desk_org, why in (
            ("Desktop on a personal plan", "claude_max", "org-1", "same organization and a personal plan: Desktop "
             "spends on the CLI's account, sources list both, the two spends add up"),
            ("Desktop on a team plan", "claude_team", "org-1", "a team organization is shared by its members: Desktop "
             "keeps its own account (no plan, account class)"),
            ("Desktop in another organization", "claude_max", "org-2", "another organization uuid never merges")):
        ev = [profile(BASE, org_type=org_type),
              resp("claudeCode", t, 4_000_000, model=OPUS, start=t - 50_000),
              resp("claudeDesktop", t + 5 * MINUTE, 6_000_000, model=OPUS),
              snap("claudeCode", t + 30_000, five=(2, R5), week=(1, R7)),
              snap("claudeDesktop", t + 6 * MINUTE, org=desk_org, five=(3, R5), week=(1, R7))]
        out.append(case(label, why, ev, t + HOUR, outputs=["responses", "instances", "entries"]))
    base = [profile(BASE), resp("claudeCode", t, 4_000_000, model=OPUS, start=t - 50_000),
            resp("claudeDesktop", t + 5 * MINUTE, 6_000_000, model=OPUS)]
    for label, why, extra in (
            ("Desktop on another account of the organization", "same organization and a personal plan, but the 7-day "
             "resets lie 19 h apart while both instances are live: two accounts (seen 2026-11-11), Desktop keeps its "
             "own", [snap("claudeCode", t + 30_000, week=(100, R7)),
                     snap("claudeDesktop", t + 6 * MINUTE, org="org-1", week=(0, R7 + 19 * HOUR))]),
            ("Desktop without CLI values", "no CLI window to compare: the fingerprint is unknown and Desktop keeps its "
             "own account", [snap("claudeDesktop", t + 6 * MINUTE, org="org-1", week=(1, R7))]),
            ("Desktop's value before the CLI's", "Desktop's snapshot comes before any CLI value: the first comparable "
             "pair after it decides, so Desktop's earlier spend joins the CLI's account",
             [snap("claudeDesktop", t + 6 * MINUTE, org="org-1", week=(1, R7)),
              snap("claudeCode", t + 20 * MINUTE, week=(2, R7 + 20_000))])):
        out.append(case(label, why, base + extra, t + HOUR, outputs=["responses", "instances", "entries"]))
    return out


def five_hour_claude(source, S, cap, steps, org=None, cost=1_000_000):
    """`steps` responses 3 min apart in the 5h window that starts at S, each followed 30 s later by a snapshot with
    the integer percent of the cap spent so far."""
    R = sec(S + 300 * MINUTE)
    events, spent = [], 0
    for i in range(1, steps + 1):
        spent += cost
        end = S + i * 3 * MINUTE
        events += [resp(source, end, cost, model=OPUS), snap(source, end + 30_000, org=org, five=(spent * 100 // cap, R))]
    return events


def inference_cases():
    t = BASE + HOUR
    R7 = BASE + 6 * DAY
    out = []
    for label, why, org_type, desk_org in (
            ("Desktop without a 5-hour window", "Desktop's own account shows a weekly window and no 5-hour window: "
             "claude_ultra (some Ultra accounts lost the 5-hour window, user 2026-11-10); the Fable window of the account "
             "carries it too", "claude_max", "org-2"),
            ("Desktop of a team organization", "the CLI names Desktop's organization a team one: nothing is inferred",
             "claude_team", "org-1")):
        ev = [profile(BASE, org_type=org_type), resp("claudeDesktop", t, 2_000_000, model=OPUS),
              snap("claudeDesktop", t + MINUTE, org=desk_org, week=(5, R7), fable=(3, R7))]
        out.append(case(label, why, ev, t + HOUR, outputs=["plans", "entries"]))
    S = BASE + 10 * MINUTE
    cli = [profile(BASE)] + five_hour_claude("claudeCode", S, 100_000_000, 20, cost=2_000_000)
    for label, why, cap in (
            ("Desktop at a quarter of the CLI's 5-hour cap", "the CLI's Max 20x account caps 5 h at ≈ $95 and "
             "Desktop's own account at ≈ $23 (both medium): 20 · 23.4 / 94.9 ≈ 4.9, claude_max with "
             "default_claude_max_5x", 25_000_000),
            ("Desktop at half the CLI's 5-hour cap", "20 · 46.8 / 94.9 ≈ 9.9 lies more than 1.5 times away from 5 "
             "and from 20: nothing is inferred", 50_000_000)):
        ev = cli + five_hour_claude("claudeDesktop", S + MINUTE, cap, 8, org="org-2", cost=cap // 10)
        out.append(case(label, why, ev, S + 3 * HOUR, outputs=["plans", "estimates", "entries"]))
    return out


def key_cases():
    t = BASE + HOUR
    ev = [resp("claudeCode", t - 30 * MINUTE, 1_000_000, model=OPUS),
          profile(t, account="user-1"),
          resp("claudeCode", t + 10 * MINUTE, 2_000_000, model=OPUS),
          profile(t + 20 * MINUTE, account="user-2", org_type="claude_pro", tier=None),
          resp("claudeCode", t + 30 * MINUTE, 3_000_000, model=OPUS)]
    return [case("spend before the first key and a /login", "the response before the first key seen goes to that "
                 "key; after the switch spend goes to the new key", ev, t + HOUR, outputs=["responses"])]


def class_cases():
    out = []
    K = 200_000_000
    ev = five_hour_codex("a", BASE, K, 4, "pro", cost=25_000_000)
    ev += [{"type": "codexAuth", "at": BASE, "accountId": "acct-a"},
           {"type": "codexAuth", "at": BASE + 6 * HOUR, "accountId": "acct-b"}]
    ev += five_hour_codex("b", BASE + 6 * HOUR, K, 4, "pro", cost=25_000_000)
    out.append(case("two accounts, one plan", "both accounts' instances pool in the class (openai/pro, 5h): two windows",
                    ev, BASE + 12 * HOUR, outputs=["instances", "estimates"]))
    ev = five_hour_codex("a", BASE, K, 6, None, cost=10_000_000)
    for i, e in enumerate(ev):
        if e["type"] == "tokenCount" and i in (5, 7):
            e["rateLimits"]["plan_type"] = "plus"
    ev += five_hour_codex("z", BASE + 3 * DAY, K, 3, None, cost=10_000_000)
    out.append(case("null plan_type", "lines with a null plan take the account's nearest earlier plan, else its first "
                    "later one (account A is plus throughout); account Z never names a plan and keeps an account class",
                    ev, BASE + 4 * DAY, outputs=["instances", "estimates"]))
    return out


def cut_cases():
    ev = []
    for i in range(5):
        cap, cost = (100_000_000, 24_000_000) if i < 3 else (60_000_000, 12_000_000)
        S = BASE + i * 6 * HOUR
        ev += five_hour_codex("a", S, cap, 3, "plus", cost=cost)
        ev += codex_turn("a", S + 4 * HOUR + 30 * MINUTE, 0, "plus", five=(ev[-1]["rateLimits"]["primary"]["used_percent"],
                                                                         sec(S + 300 * MINUTE)))
    return [case("lowered cap", "three windows at $100 then two at $60: the instances after the cut fall below the "
                 "older ones' lower bounds, so the older evidence is dropped and the estimate is $60 (a last zero-cost turn with the "
                 "same percent lets the cumulative bound count the final response)",
                 ev, BASE + 30 * HOUR, outputs=["estimates"])]


def plan_change_cases():
    K = 100_000_000
    ev = five_hour_codex("a", BASE, K, 6, "plus")
    for e in ev[6:]:
        if e["type"] == "tokenCount":
            e["rateLimits"]["plan_type"] = "pro"
    return [case("plan change inside a window", "the instance splits at the change: the plus part stays in the plus "
                 "class, the pro part starts the pro class; the entry shows the newer part", ev, BASE + 4 * HOUR,
                 outputs=["instances", "estimates", "entries"])]


def live_cases():
    out = []
    R = BASE + 5 * HOUR
    ev = [profile(BASE)]
    for i in range(1, 6):
        end = BASE + i * 40 * MINUTE
        ev += [resp("claudeCode", end, 10_000_000, model=OPUS), snap("claudeCode", end + 3 * MINUTE, five=(i * 10, R))]
    ev.append(resp("claudeCode", R + 40 * MINUTE, 3_000_000, model=OPUS, start=R + 38 * MINUTE))
    for now, label, why in (
            (R + 30 * MINUTE, "no use yet", "after R with no local response yet: 0 %, not estimated, no reset"),
            (R + 2 * HOUR, "after the first response", "the new instance starts at the minute of the first local "
             "response's start after R (R + 38 min), resets 5 h later; the percent is spend since then over the cap")):
        out.append(case("five-hour after reset, " + label, why, ev, now, outputs=["entries"]))
    R7 = BASE + 7 * DAY
    ev = [profile(BASE)]
    for i in range(1, 6):
        end = R7 - (12 - 2 * i) * HOUR
        ev += [resp("claudeCode", end, 40_000_000, model=OPUS), snap("claudeCode", end + 3 * MINUTE, week=(i * 10, R7))]
    ev += [resp("claudeCode", R7 + 3 * DAY, 50_000_000, model=OPUS),
           resp("claudeCode", R7 + 7 * DAY + 2 * HOUR, 20_000_000, model=OPUS)]
    out.append(case("weekly after reset", "a weekly window restarts at R + n * minutes: 1.5 weeks after R that is R + 1 "
                    "week, so the response 3 days after R no longer counts", ev, R7 + 7 * DAY + 12 * HOUR,
                    outputs=["entries"]))
    ev = ev[:-2] + [resp("claudeCode", R7 - 20 * MINUTE, 30_000_000, model=OPUS)]
    out.append(case("before the reset", "spend after the newest vendor value raises the live percent: p + 100 * "
                    "(C+(S, now) - C+(S, t)) / K, estimated from 0.5 points up", ev, R7 - 10 * MINUTE,
                    outputs=["entries"]))
    return out


def store_cases():
    out = []
    t = BASE + HOUR
    R5, R7 = BASE + 5 * HOUR, BASE + 6 * DAY
    ev = [profile(BASE), resp("claudeCode", t, 4_000_000, model=OPUS),
          resp("claudeCode", t + 20 * MINUTE, 4_000_000, model=OPUS),
          snap("claudeCode", t + 30_000, five=(2, R5)), snap("claudeCode", t + 60_000, five=(2, R5)),
          snap("claudeCode", t + 90_000, five=(2, R5)),
          snap("claudeCode", t + 21 * MINUTE, five=(4, R5))]
    first = case("store written", "runs of equal values keep their first and last observation; key changes are kept "
                 "with their plan", ev, R5 + HOUR, outputs=["estimates", "store"])
    out.append(first)
    again = [e for e in ev if e["type"] == "response"]
    out.append(case("store read back", "a later launch with only the responses and the stored file gives the same "
                    "estimate: the stored keys attribute the spend, the stored observations rebuild the instance",
                    again, R5 + HOUR, outputs=["estimates"], store=first["expected"]["store"]))
    gen = {"version": STORE_VERSION, "t0": BASE, "account": "k1", "observations": 300, "instances": 70}
    why = ("generated store: observation i = 1..300 of k1 (claudeCode, sevenDay, plan claude_max) at t = tLo = t0 + i "
           "min with p = i / 10 and R = t0 + 6 d; closed instance i = 1..70 of class (plan, anthropic/claude_max, "
           "null, fiveHour::300) with R = t0 - 5 i h, lo 1e8, hi 2e8, pMax 40, tLast = R - 1 h; one key")
    out.append(case("store limits", "300 distinct values of one instance thin to 256 evenly spaced runs (first and "
                    "last kept); 70 closed instances of one class keep the newest 64; " + why, [], BASE + 6 * HOUR,
                    outputs=["storeCounts"], generatedStore=gen))
    out.append(case("store of another version", "a file of another version is ignored (and replaced); the same "
                    "generated store with version 1 (before the fixed account salt)", [], BASE + 6 * HOUR,
                    outputs=["storeCounts"], generatedStore=dict(gen, version=1)))
    dev = [profile(BASE), resp("claudeCode", t, 4_000_000, model=OPUS, start=t - 50_000),
           resp("claudeDesktop", t + 5 * MINUTE, 6_000_000, model=OPUS),
           snap("claudeCode", t + 30_000, week=(1, R7)), snap("claudeDesktop", t + 6 * MINUTE, org="org-1", week=(2, R7))]
    desk = case("store written with Desktop", "Desktop's stored timeline holds its organization's own key; the shared "
                "key is derived", dev, t + HOUR, outputs=["responses", "estimates", "store"])
    out.append(desk)
    out.append(case("store read back with Desktop", "only the responses and the stored file: the stored organization "
                    "timeline and the stored windows of both sources give the same merge and estimate",
                    [e for e in dev if e["type"] == "response"], t + HOUR, outputs=["responses", "estimates"],
                    store=desk["expected"]["store"]))
    return out


def report_cases():
    t = BASE + HOUR
    R5, R7 = BASE + 5 * HOUR, BASE + 6 * DAY
    ev = [profile(BASE), {"type": "codexAuth", "at": BASE, "accountId": "acct-a"},
          resp("claudeCode", t, 1_234_567, model=FABLE, start=t - 40_000),
          resp("claudeCode", t + 5 * MINUTE, 2_345_678, model=OPUS),
          resp("claudeDesktop", t + 7 * MINUTE, 345_679, model=OPUS),
          snap("claudeCode", t + 10_000, week=(1.25, R7), fable=(3.333, R7), five=(2.5, R5)),
          snap("claudeDesktop", t + 8 * MINUTE, org="org-team", five=(7, R5 + 60_000))]
    ev += codex_turn("a", t + 9 * MINUTE, 7_654_321, "pro", five=(4, sec(R5 + 3 * MINUTE)), week=(1, sec(R7 + HOUR)))
    out = [case("entries and their order", "Codex first, then the Claude account, then the team Desktop account; each "
                "account's windows unnamed by length, then named; percent at two decimals, micro-USD whole; a "
                "Desktop of a team organization lists itself only", ev, t + 30 * MINUTE, outputs=["entries"])]
    two = [{"type": "codexAuth", "at": BASE, "accountId": "acct-a"}]
    two += codex_turn("a", t, 3_000_000, "pro", week=(40, sec(R7)))
    two += [{"type": "codexAuth", "at": t + HOUR, "accountId": "acct-b"}]
    two += codex_turn("b", t + 2 * HOUR, 2_000_000, "pro", week=(10, sec(R7 - 20 * HOUR)))
    out.append(case("two Codex accounts", "two accounts of one source and window are two entries with the same sources "
                    "and window and different account tags (seen 2026-11-11: a service rejected the pair before the "
                    "tag existed)", two, t + 3 * HOUR, outputs=["entries"]))
    return out


def reporter_case():
    e = {"account": "0123456789abcdef", "sources": ["codex"], "window": {"kind": "sevenDay", "name": None, "minutes": 10080},
         "usedPercent": 20.0,
         "resetsInMs": 1_000_000, "usedMicroUsd": 1, "estimate": {"confidence": "medium"}}

    def variant(change, prev):
        x = json.loads(json.dumps(prev))
        if change == "spend":
            x["usedMicroUsd"] += 1
        elif change == "percent":
            x["usedPercent"] += 1
        elif change == "confidence":
            x["estimate"]["confidence"] = "high" if x["estimate"]["confidence"] != "high" else "medium"
        elif change == "reset":
            x["resetsInMs"] = None if x["resetsInMs"] is not None else 1_000_000
            x["usedPercent"] = None
        return x

    steps = [
        {"at": 0, "change": "none"},
        {"at": 10_000, "change": "spend"},
        {"at": 20_000, "change": "percent"},
        {"at": 30_000, "change": "percent", "fits": False},
        {"at": 35_000, "change": "none"},
        {"at": 50_000, "change": "confidence"},
        {"at": 110_000, "change": "spend"},
        {"at": 120_000, "change": "none", "switch": False},
        {"at": 130_000, "change": "none", "switch": False},
        {"at": 140_000, "change": "none"},
        {"at": 145_000, "change": "reset"},
        {"at": 150_000, "change": "set"},
        {"at": 155_000, "change": "none", "newRun": True},
        {"at": 156_000, "change": "none", "newRun": True, "switch": False},
        {"at": 157_000, "change": "none", "leaving": True},
    ]
    rep, cur, out = Reporter(), [e], []
    for s in steps:
        if s.get("newRun"):
            rep.new_run()
        if s["change"] == "set":
            cur = cur + [dict(cur[0], account="fedcba9876543210")]
        elif s["change"] != "none":
            cur = [variant(s["change"], cur[0])] + cur[1:]
        q = rep.step(s["at"], cur, s.get("switch", True), s.get("leaving", False), s.get("fits", True))
        out.append("absent" if q is None else ("[]" if q == [] else "entries"))
    return {"why": "a report carries `quotas` when it is the run's first, the set of entries, a vendor percent, a "
                   "confidence or the reset state changed, 60 s passed since the last sent one, or the app leaves; "
                   "spend alone waits; a section dropped for size counts as unsent; switching the section off sends "
                   "`[]` once; a new run starts empty", "entry": e, "steps": steps, "expected": out}


def cases():
    return {
        "ledger": ledger_cases(),
        "chains": chain_cases(),
        "claude": claude_cases(),
        "inference": inference_cases(),
        "keys": key_cases(),
        "classes": class_cases(),
        "loweredCap": cut_cases(),
        "planChange": plan_change_cases(),
        "live": live_cases(),
        "store": store_cases(),
        "report": report_cases(),
        "cadence": reporter_case(),
    }
