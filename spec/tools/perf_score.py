#!/usr/bin/env python3
"""Reference implementation of the performance score (METRICS 6.9) and its history (METRICS 6.11).

  python3 spec/tools/perf_score.py              check spec/fixtures/perf-score-cases.json

`evaluate` scores one agent at `nowMs` from its responses (3.3) and its stored actions (6.10); `highest` picks the
value of a set or a board group; `day_records` turns agents' completions into the per-day records of the history
analysis; `pooled` scores any set of records with the same formula; `baseline` and `range_values` are what the score
card and the History window show. The fixture is written by spec/tools/perf_score_cases.py --write, which holds every
expectation written by hand. The live thinking cases (6.9 Live thinking) are checked here too, through
live_thinking.py.
"""
from __future__ import annotations

import json
import math
import os
import sys
from datetime import date, timedelta

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import live_thinking as thinking  # noqa: E402
from live_thinking import seq_sum  # noqa: E402

engine = thinking.engine
FIXTURE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "fixtures", "perf-score-cases.json")

SOURCE_ORDER = ("codex", "claudeCode", "claudeDesktop", "deepseek", "opencode", "gemini", "omp", "antigravity",
                "workbuddy", "workbuddyAi", "zcode", "kimi", "grokBuild", "pi", "openclaw", "hermes", "droid")
LATENCY_SOURCES = ("codex", "claudeCode", "claudeDesktop")          # 4.1, 4.2: firstMs
ACTION_SOURCES = ("codex", "claudeCode", "claudeDesktop", "omp", "pi", "openclaw", "droid")  # 6.10
NO_REASONING = thinking.NO_SEPARATE_REASONING                         # 4.3: output does not split out reasoning
NO_CACHE = ("hermes",)                                                # 4.15: the cache read is optional on its line

IDS = ("T", "L", "N", "R", "H", "U", "S")
WEIGHTS = {"T": 0.30, "L": 0.20, "N": 0.15, "R": 0.10, "H": 0.10, "U": 0.10, "S": 0.05}
RATIO = {"T": lambda m: m / 50, "L": lambda m: 5000 / max(m, 250), "N": lambda m: m / 3, "R": lambda m: m / 20,
         "H": lambda m: m / 0.95, "U": lambda m: 400 / m, "S": lambda m: m / 0.95}
T_FLOOR, LOW, HIGH = 0.01, 0.05, 20.0
SAMPLE = 20
MIN_SAMPLES = 3
PROVISIONAL_BELOW = 10
WINDOW_MS = 30 * 60_000
STALE_MS = 300_000
TOOL_CAP = 64
TOOL_MAX_MS = 15 * 60_000
TOOL_FLOOR_MS = 50
BINS = 64
POOLED_MIN_N = 20
BASELINE_DAYS = 14


# ----------------------------------------------------------------------------- latency bins (6.11)

def _edge(j):
    """E_j = ⌈100 · 2^(j/4)⌉ in integer arithmetic: the ceiling of the 4th root of 10^8 · 2^j."""
    n = 10 ** 8 * 2 ** j
    root = math.isqrt(math.isqrt(n))
    return root if root ** 4 == n else root + 1


EDGES = [_edge(j) for j in range(BINS + 1)]


def latency_bin(ms):
    """Bucket k of an integer latency: the number of edges E_1 … E_63 it reaches."""
    return sum(1 for j in range(1, BINS) if ms >= EDGES[j])


def bin_centre(k):
    """The geometric centre 100 · 2^((k + 0.5) / 4) of bucket k."""
    return 100 * 2 ** ((2 * k + 1) / 8)


# ----------------------------------------------------------------------------- shared formula

def clamp(cid, ratio):
    return max(T_FLOOR, ratio) if cid == "T" else min(HIGH, max(LOW, ratio))


def not_recorded(cid, source):
    """Whether the source never records what the component measures (6.9 *Presence*)."""
    return ((cid == "L" and source not in LATENCY_SOURCES) or (cid in ("N", "U", "S") and source not in ACTION_SOURCES)
            or (cid == "R" and source in NO_REASONING) or (cid == "H" and source in NO_CACHE))


def combine(source, measures):
    """measures: {id: (measure or None, samples)} with None for an absent component -> components and score."""
    components, log_sum, weights = [], 0.0, 0.0
    for cid in IDS:
        measure, samples = measures[cid]
        if measure is None:
            reason = "notRecorded" if not_recorded(cid, source) else "fewSamples"
            components.append({"id": cid, "present": False, "reason": reason, "samples": samples, "measure": None,
                               "ratio": None, "subScore": None})
            continue
        ratio = clamp(cid, RATIO[cid](measure))
        components.append({"id": cid, "present": True, "reason": None, "samples": samples, "measure": measure,
                           "ratio": ratio, "subScore": 1000 * ratio})
        log_sum = log_sum + WEIGHTS[cid] * math.log(ratio)
        weights = weights + WEIGHTS[cid]
    score = 1000 * math.exp(log_sum / weights) if weights > 0 else None
    return components, score


def round_half_away(value):
    return int(math.floor(value + 0.5)) if value >= 0 else -int(math.floor(-value + 0.5))


def duration_ms(response):
    start, end = engine.interval(response)
    return end - start


def measured(response):
    return engine.interval(response) is not None and engine.is_final(response)


def union_length(intervals):
    total, current = 0, None
    for start, end in sorted(intervals):
        if end <= start:
            continue
        if current is None or start > current[1]:
            if current is not None:
                total += current[1] - current[0]
            current = [start, end]
        else:
            current[1] = max(current[1], end)
    return total + (current[1] - current[0] if current is not None else 0)


def median(values):
    ordered = sorted(values)
    mid = len(ordered) // 2
    return ordered[mid] if len(ordered) % 2 else (ordered[mid - 1] + ordered[mid]) / 2


def _ordinal(text):
    return text.encode("utf-16-be")


# ----------------------------------------------------------------------------- one agent (6.9)

def sample_of(responses, now):
    """The agent's measured responses with nowMs − 30 min ≤ endMs ≤ nowMs, the newest 20 (ties: key, ordinal)."""
    chosen = [r for r in responses if measured(r) and now - WINDOW_MS <= r["endMs"] <= now]
    chosen.sort(key=lambda r: _ordinal(r["key"]))
    chosen.sort(key=lambda r: r["endMs"], reverse=True)
    return chosen[:SAMPLE]


def calls_of(actions, span_start, now):
    """C: completed actions of the stored list (the last 64 in file order) inside the sample's span."""
    return [a for a in actions[-TOOL_CAP:] if a.get("resultMs") is not None and a["callMs"] >= span_start
            and a["resultMs"] <= now and 0 <= a["resultMs"] - a["callMs"] <= TOOL_MAX_MS]


def effective_phase(agent, now):
    """6.7 step 1: a phase older than 300 s without a new line is idle."""
    last = agent.get("lastLineMs")
    if agent["phase"] != "idle" and last is not None and now - last > STALE_MS:
        return "idle"
    return agent["phase"]


def evaluate(agent, responses, actions, now, history=None, today=None):
    """6.9 for one agent at `now`: form, score and the facts of the score card."""
    source = agent["source"]
    sample = sample_of(responses, now)
    n = len(sample)
    measures = {cid: (None, 0) for cid in IDS}
    span_start = min(engine.interval(r)[0] for r in sample) if sample else None
    calls = calls_of(actions, span_start, now) if n >= MIN_SAMPLES else []
    active = union_length([engine.interval(r) for r in sample] + [(a["callMs"], a["resultMs"]) for a in calls])
    if n >= MIN_SAMPLES:
        out = seq_sum((r["output"] for r in sample), 0)
        dur = seq_sum((duration_ms(r) for r in sample), 0)
        measures["T"] = (out / (dur / 1000), n)
        latencies = [r["firstMs"] - r["startMs"] for r in sample
                     if r.get("firstMs") is not None and r.get("startMs") is not None and r["firstMs"] >= r["startMs"]]
        measures["L"] = (median(latencies) if len(latencies) >= MIN_SAMPLES else None, len(latencies))
        if source in NO_REASONING:
            measures["R"] = (None, 0)
        else:
            thought = [r for r in sample if min(r.get("reasoning", 0), r["output"]) > 0]
            reason = seq_sum((min(r["reasoning"], r["output"]) for r in thought), 0)
            reason_ms = seq_sum((duration_ms(r) for r in thought), 0)
            measures["R"] = (reason / (reason_ms / 1000) if len(thought) >= MIN_SAMPLES else None, len(thought))
        if source in NO_CACHE:
            measures["H"] = (None, 0)
        else:
            cached = [r for r in sample if r["input"] > 0]
            hit = seq_sum((r["cachedInput"] for r in cached), 0) / seq_sum((r["input"] for r in cached), 0) \
                if cached else None
            measures["H"] = (hit if len(cached) >= MIN_SAMPLES else None, len(cached))
        k = len(calls)
        if source in ACTION_SOURCES and k >= MIN_SAMPLES:
            measures["N"] = (k / (active / 60_000) if active > 0 else None, k)
            logs = seq_sum(math.log(max(a["resultMs"] - a["callMs"], TOOL_FLOOR_MS)) for a in calls)
            measures["U"] = (math.exp(logs / k), k)
            failed = sum(1 for a in calls if a.get("failed") is True)
            measures["S"] = (1 - failed / k, k)
        else:
            measures.update({cid: (None, k) for cid in ("N", "U", "S")})
    else:
        measures["T"] = (None, n)
    components, score = combine(source, measures)
    phase = effective_phase(agent, now)
    form = "none" if score is None else ("resting" if phase == "idle" else "live")
    model = sample[0].get("model") if sample else None
    out = {"form": form, "score": score, "shown": None if score is None else round_half_away(score),
           "provisional": score is not None and n < PROVISIONAL_BELOW, "n": n, "tools": len(calls),
           "activeMs": active, "spanStartMs": span_start, "model": model, "components": components,
           "baseline": None, "vsUsual": None, "vsUsualPercent": None}
    if history is not None and today is not None and score is not None:
        base = baseline(history, source, model or "", today)
        if base is not None:
            out["baseline"] = base
            out["vsUsual"] = score / base - 1
            out["vsUsualPercent"] = round_half_away(100 * (score / base - 1))
    return out


def listed(agents, now):
    """6.7 steps 1–4: the agents list (ids) and each listed agent's tree parent."""
    by_id = {a["id"]: a for a in agents}
    chosen = {a["id"] for a in agents if effective_phase(a, now) == "generating" or
              (a.get("lastLineMs") is not None and now - a["lastLineMs"] <= 600_000)}
    for aid in list(chosen):
        seen, cur = {aid}, by_id[aid].get("parentId")
        while cur in by_id and cur not in seen:
            chosen.add(cur)
            seen.add(cur)
            cur = by_id[cur].get("parentId")
    return {aid: (by_id[aid].get("parentId") if by_id[aid].get("parentId") in chosen else None) for aid in chosen}


def highest(agents, values):
    """6.9 *Sets and groups*: the highest live value, else the highest resting value, else none; `agent` names the
    supplier (latest lastLineMs, then id, ordinal)."""
    for form in ("live", "resting"):
        candidates = [a for a in agents if values[a["id"]]["form"] == form]
        if candidates:
            best = max(values[a["id"]]["score"] for a in candidates)
            ties = [a for a in candidates if values[a["id"]]["score"] == best]
            ties.sort(key=lambda a: (-(a.get("lastLineMs") or 0), _ordinal(a["id"])))
            return {"form": form, "score": best, "shown": round_half_away(best), "agent": ties[0]["id"]}
    return {"form": "none", "score": None, "shown": None, "agent": None}


# ----------------------------------------------------------------------------- history (6.11)

FIELDS = ("n", "outputSum", "durationMsSum", "latencyBins", "reasonN", "reasonOutput", "reasonDurationMsSum",
          "cacheN", "cacheInput", "cacheCached", "toolN", "toolLnSum", "toolFailed", "activeMs")


def empty_record():
    return {"n": 0, "outputSum": 0, "durationMsSum": 0, "latencyBins": [0] * BINS, "reasonN": 0, "reasonOutput": 0,
            "reasonDurationMsSum": 0, "cacheN": 0, "cacheInput": 0, "cacheCached": 0, "toolN": 0, "toolLnSum": 0.0,
            "toolFailed": 0, "activeMs": 0}


class Recorder:
    """Per (day, source, model) records from completions taken in the order they complete, with the online union
    rule for activeMs (one `coveredUntil` per agent and model)."""

    def __init__(self, day_of):
        self.day_of = day_of
        self.records = {}
        self.covered = {}

    def record(self, day, source, model):
        return self.records.setdefault((day, source, model), empty_record())

    def active(self, agent_id, source, model, start, end):
        key = (agent_id, model)
        covered = self.covered.get(key)
        added = max(0, end - (start if covered is None else max(start, covered)))
        self.covered[key] = end if covered is None else max(covered, end)
        if added:
            self.record(self.day_of(end), source, model)["activeMs"] += added

    def response(self, agent_id, source, r):
        """A measured response (3.3), with its values for the record."""
        if not measured(r):
            return
        model = r.get("model") or ""
        rec = self.record(self.day_of(r["endMs"]), source, model)
        dur = duration_ms(r)
        rec["n"] += 1
        rec["outputSum"] += r["output"]
        rec["durationMsSum"] += dur
        if r.get("firstMs") is not None and r.get("startMs") is not None and r["firstMs"] >= r["startMs"]:
            rec["latencyBins"][latency_bin(r["firstMs"] - r["startMs"])] += 1
        thought = min(r.get("reasoning", 0), r["output"])
        if source not in NO_REASONING and thought > 0:
            rec["reasonN"] += 1
            rec["reasonOutput"] += thought
            rec["reasonDurationMsSum"] += dur
        if source not in NO_CACHE and r["input"] > 0:
            rec["cacheN"] += 1
            rec["cacheInput"] += r["input"]
            rec["cacheCached"] += r["cachedInput"]
        start, end = engine.interval(r)
        self.active(agent_id, source, model, start, end)

    def action(self, agent_id, source, a):
        """A tool call whose result was read (6.10), within 15 minutes."""
        if a.get("resultMs") is None or not 0 <= a["resultMs"] - a["callMs"] <= TOOL_MAX_MS:
            return
        model = a.get("model") or ""
        rec = self.record(self.day_of(a["resultMs"]), source, model)
        rec["toolN"] += 1
        rec["toolLnSum"] = rec["toolLnSum"] + math.log(max(a["resultMs"] - a["callMs"], TOOL_FLOOR_MS))
        rec["toolFailed"] += 1 if a.get("failed") is True else 0
        self.active(agent_id, source, model, a["callMs"], a["resultMs"])

    def feed(self, agent_id, source, events):
        for event in events:
            if "response" in event:
                self.response(agent_id, source, event["response"])
            else:
                self.action(agent_id, source, event["action"])

    def rows(self):
        """[{day, source, model, …fields}] by day, source order, then model (UTF-8 bytes)."""
        keys = sorted(self.records, key=lambda k: (k[0], SOURCE_ORDER.index(k[1]), k[2].encode("utf-8")))
        return [dict(day=d, source=s, model=m, **self.records[(d, s, m)]) for d, s, m in keys]


def day_records(agents, day_of):
    """agents: [{id, source, events}] with each agent's completions in the order they complete."""
    recorder = Recorder(day_of)
    for agent in agents:
        recorder.feed(agent["id"], agent["source"], agent["events"])
    return recorder.rows()


def add_records(records):
    total = empty_record()
    for rec in records:
        for field in FIELDS:
            if field == "latencyBins":
                total[field] = [a + b for a, b in zip(total[field], rec[field])]
            elif field == "toolLnSum":
                total[field] = total[field] + rec[field]
            else:
                total[field] += rec[field]
    return total


def pooled_latency(bins):
    count = sum(bins)
    if count < MIN_SAMPLES:
        return None, count
    target, seen = (count + 1) // 2, 0
    for k, c in enumerate(bins):
        seen += c
        if seen >= target:
            return bin_centre(k), count
    raise AssertionError("unreachable")


def pooled(records, source):
    """6.11 Pooled score of records of one source: the 6.9 formula over the pooled sums; none below Σ n = 20."""
    s = add_records(records)
    measures = {cid: (None, 0) for cid in IDS}
    measures["T"] = (s["outputSum"] / (s["durationMsSum"] / 1000) if s["n"] >= MIN_SAMPLES else None, s["n"])
    measures["L"] = pooled_latency(s["latencyBins"])
    measures["R"] = (s["reasonOutput"] / (s["reasonDurationMsSum"] / 1000) if s["reasonN"] >= MIN_SAMPLES else None,
                     s["reasonN"])
    measures["H"] = (s["cacheCached"] / s["cacheInput"] if s["cacheN"] >= MIN_SAMPLES else None, s["cacheN"])
    k = s["toolN"]
    if k >= MIN_SAMPLES:
        measures["N"] = (k / (s["activeMs"] / 60_000) if s["activeMs"] > 0 else None, k)
        measures["U"] = (math.exp(s["toolLnSum"] / k), k)
        measures["S"] = (1 - s["toolFailed"] / k, k)
    else:
        measures.update({cid: (None, k) for cid in ("N", "U", "S")})
    components, score = combine(source, measures)
    if s["n"] < POOLED_MIN_N:
        score = None
    return {"n": s["n"], "tools": k, "activeMs": s["activeMs"], "score": score,
            "shown": None if score is None else round_half_away(score), "components": components}


def day_minus(day, count):
    return (date.fromisoformat(day) - timedelta(days=count)).isoformat()


def baseline(records, source, model, today):
    """比平常: the pooled score of (source, raw model) over the 14 local days before today; None below Σ n = 20."""
    first, last = day_minus(today, BASELINE_DAYS), day_minus(today, 1)
    chosen = [r for r in records if r["source"] == source and r["model"] == model and first <= r["day"] <= last]
    return pooled(chosen, source)["score"]


def range_values(records, first, last, source=None):
    """The History window's 性能表現 over [first, last]: per (source, model) the pooled values and the daily trend."""
    chosen = [r for r in records if first <= r["day"] <= last and (source is None or r["source"] == source)]
    groups = {}
    for r in chosen:
        groups.setdefault((r["source"], r["model"]), []).append(r)
    days, d = [], first
    while d <= last:
        days.append(d)
        d = day_minus(d, -1)
    rows = []
    for (src, model), part in groups.items():
        trend = []
        for day in days:
            value = pooled([r for r in part if r["day"] == day], src)
            trend.append({"day": day, "score": value["score"]})
        rows.append(dict(source=src, model=model, **pooled(part, src), trend=trend))
    rows.sort(key=lambda row: (SOURCE_ORDER.index(row["source"]), -row["n"], row["model"].encode("utf-8")))
    return rows


# ----------------------------------------------------------------------------- fixtures

def compute(cases):
    out = {"edges": EDGES}
    out["latencyBins"] = [dict(c, expect=latency_bin(c["ms"])) for c in cases["latencyBins"]]
    out["agents"] = []
    for case in cases["agents"]:
        queries = []
        for q in case["queries"]:
            expect = {}
            for agent in case["agents"]:
                own = [r for r in case["responses"] if r["agentId"] == agent["id"]]
                expect[agent["id"]] = evaluate(agent, own, case.get("actions", {}).get(agent["id"], []), q["nowMs"],
                                               case.get("history"), case.get("today"))
            queries.append(dict(q, expect=expect))
        out["agents"].append(dict(case, queries=queries))
    out["sets"] = []
    for case in cases["sets"]:
        values = case["values"]
        groups = thinking.board_groups(case["agents"])
        by_id = {a["id"]: a for a in case["agents"]}
        expect = {"all": highest(case["agents"], values),
                  "groups": {root: dict(members=members, **highest([by_id[m] for m in members], values))
                             for root, members in groups.items()}}
        out["sets"].append(dict(case, expect=expect))
    out["days"] = [dict(c, expect=day_records(c["agents"], lambda ms: utc_day(ms))) for c in cases["days"]]
    out["pooled"] = [dict(c, expect=pooled(c["records"], c["source"])) for c in cases["pooled"]]
    out["baseline"] = [dict(c, expect=baseline(c["records"], c["source"], c["model"], c["today"]))
                       for c in cases["baseline"]]
    out["range"] = [dict(c, expect=range_values(c["records"], c["first"], c["last"], c.get("source")))
                    for c in cases["range"]]
    out["thinking"] = compute_thinking(cases["thinking"])
    return out


def utc_day(ms):
    return (date(1970, 1, 1) + timedelta(days=ms // 86_400_000)).isoformat()


def compute_thinking(cases):
    out = {"windows": [], "agentWindows": [], "groupWindows": [], "modelWindows": []}
    for case in cases["windows"]:
        queries = []
        for q in case["queries"]:
            subset = set(q.get("sources") or [r["source"] for r in case["responses"]] +
                         [a["source"] for a in case["agents"]])
            responses = [r for r in case["responses"] if r["source"] in subset]
            agents = [a for a in case["agents"] if a["source"] in subset]
            queries.append(dict(q, expect=thinking.window_sums(responses, agents, q["nowMs"])[0].result()))
        out["windows"].append(dict(case, queries=queries))
    for case in cases["agentWindows"]:
        queries = []
        for q in case["queries"]:
            total, per_agent = thinking.window_sums(case["responses"], case["agents"], q["nowMs"])
            agents = {a["id"]: per_agent.get(a["id"], thinking.Window()).result() for a in case["agents"]}
            queries.append(dict(q, expect=total.result(), agents=agents))
        out["agentWindows"].append(dict(case, queries=queries))
    for case in cases["groupWindows"]:
        queries = []
        groups = thinking.board_groups(case["agents"])
        for q in case["queries"]:
            total, per_agent = thinking.window_sums(case["responses"], case["agents"], q["nowMs"])
            got = {root: dict(members=members, **thinking.group_window(per_agent, members).result())
                   for root, members in groups.items()}
            queries.append(dict(q, expect=total.result(), groups=got))
        out["groupWindows"].append(dict(case, queries=queries))
    for case in cases["modelWindows"]:
        queries = []
        for q in case["queries"]:
            total, _ = thinking.window_sums(case["responses"], case["agents"], q["nowMs"])
            models = {model: window.result()
                      for model, window in thinking.model_sums(case["responses"], case["agents"], q["nowMs"]).items()}
            queries.append(dict(q, expect=total.result(), models=models))
        out["modelWindows"].append(dict(case, queries=queries))
    return out


def close(a, b, tolerance=1e-9):
    """Structural equality; numbers within an absolute tolerance, or a relative one for large values."""
    if isinstance(a, bool) or isinstance(b, bool) or a is None or b is None or isinstance(a, str):
        return a == b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return abs(a - b) <= tolerance * max(1.0, abs(a), abs(b))
    if isinstance(a, dict) and isinstance(b, dict):
        return a.keys() == b.keys() and all(close(a[k], b[k], tolerance) for k in a)
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(close(x, y, tolerance) for x, y in zip(a, b))
    return a == b


EXPECT_KEYS = ("expect", "agents", "groups", "models")


def inputs_of(data):
    """The fixture's inputs: everything but the computed fields."""
    out = {}
    for section, items in data.items():
        if section in ("_comment", "edges"):
            continue
        if section == "thinking":
            out[section] = {k: [dict(c, queries=[{qk: qv for qk, qv in q.items() if qk not in EXPECT_KEYS}
                                                  for q in c["queries"]]) for c in v] for k, v in items.items()}
            continue
        cleaned = []
        for case in items:
            case = {k: v for k, v in case.items() if k != "expect"}
            if "queries" in case:
                case["queries"] = [{k: v for k, v in q.items() if k != "expect"} for q in case["queries"]]
            cleaned.append(case)
        out[section] = cleaned
    return out


def counts(data):
    queries = sum(len(c["queries"]) for c in data["agents"])
    return {"latencyBins": len(data["latencyBins"]), "agentQueries": queries, "sets": len(data["sets"]),
            "days": len(data["days"]), "pooled": len(data["pooled"]), "baseline": len(data["baseline"]),
            "range": len(data["range"]),
            "thinkingQueries": sum(len(c["queries"]) for k in ("windows", "agentWindows", "groupWindows",
                                                                "modelWindows") for c in data["thinking"][k])}


def diff(got, want, label):
    if isinstance(got, dict) and isinstance(want, dict):
        lines = []
        for k in sorted(set(got) | set(want), key=str):
            if k not in got or k not in want:
                lines.append("%s.%s: present on one side only" % (label, k))
            elif not close(got[k], want[k]):
                lines.extend(diff(got[k], want[k], "%s.%s" % (label, k)))
        return lines
    if isinstance(got, list) and isinstance(want, list) and len(got) == len(want):
        lines = []
        for i, (g, w) in enumerate(zip(got, want)):
            if not close(g, w):
                lines.extend(diff(g, w, "%s[%d]" % (label, i)))
        return lines
    return ["%s: fixture %r, reference %r" % (label, want, got)]


def main():
    with open(FIXTURE, encoding="utf-8") as f:
        data = json.load(f)
    computed = compute(inputs_of(data))
    failures = []
    for section in computed:
        if not close(computed[section], data[section]):
            failures.append("section %s differs from the reference" % section)
            failures.extend(diff(computed[section], data[section], section))
    if failures:
        print("\n".join(failures[:40]))
        return 1
    print("performance score reference matches %s" % ", ".join("%d %s" % (v, k) for k, v in counts(data).items()))
    return 0


if __name__ == "__main__":
    sys.exit(main())
