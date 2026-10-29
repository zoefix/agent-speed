#!/usr/bin/env python3
"""Reference implementation of the `perfScore` report extension (METRICS 11.10, REPORTING-EXTENSIONS 9).

Standard library only. It implements:

- when the extension is active for a report, and when a client sends the retired `taskScore` instead (thinking only);
- the live values (the highest performance score of a set, its form, the thinking speed) at the top level and per
  model, with the membership and `other` rules of the `models` section;
- the statistics ledger of METRICS 11.10 as a simulator: which completions count, the online union rule for
  activeMs, what a break or a build does, and the rows a build sends;
- the checks a service makes when it advertises the extension (and the old checks of `taskScore`);
- a receiver that pools the rows of every accepted report into a model leaderboard with the METRICS 6.9 formula
  (written out here, independently of perf_score.py);
- the `perfScore` part of spec/fixtures/report-vectors.json (written by report_reference.py --write, checked by it).
"""

from __future__ import annotations

import copy
import math

import report_reference as ref
from execution_modes_reference import supported

EXTENSION = "perfScore"
LEGACY = "taskScore"
FORMS = ("live", "resting", "none")
FORM_RANK = {"live": 2, "resting": 1, "none": 0}
MAX_SCORE = 10_000_000          # a reported score above this is sent as this (a broken log, never a real setup)
MAX_RESPONSES = 100_000         # Σ n of one report
MAX_CALLS = 100_000             # Σ toolN of one report
FIRST_SEEN_MS = 120_000         # METRICS 11.4: a completion older than this when read is history
TOOL_MAX_MS = 15 * 60_000
TOOL_FLOOR_MS = 50
MIN_MS, MAX_MS = 500, 15 * 60_000
TOKEN_CAP = 10 ** 12
LN_PLACES = 6
BINS = 64
POOLED_MIN_N = 20
COUNT_FIELDS = ("n", "outputSum", "durationMsSum", "reasonN", "reasonOutput", "reasonDurationMsSum", "cacheN",
                "cacheInput", "cacheCached", "toolN", "toolFailed", "activeMs")
ROW_FIELDS = ("source", "n", "outputSum", "durationMsSum", "latencyBins", "reasonN", "reasonOutput",
              "reasonDurationMsSum", "cacheN", "cacheInput", "cacheCached", "toolN", "toolLnSum", "toolFailed",
              "activeMs")
NO_REASONING = ("deepseek",)
NO_CACHE = ("hermes",)
UNRANKED = ("other", "unknown", "local")
WEIGHTS = (("T", 0.30), ("L", 0.20), ("N", 0.15), ("R", 0.10), ("H", 0.10), ("U", 0.10), ("S", 0.05))


def round_half_away(value: float, places: int = 0):
    """Half away from zero; every value here is >= 0."""
    scale = 10 ** places
    rounded = math.floor(value * scale + 0.5) / scale
    return int(rounded) if places == 0 else rounded


def _edge(j: int) -> int:
    n = 10 ** 8 * 2 ** j
    root = math.isqrt(math.isqrt(n))
    return root if root ** 4 == n else root + 1


EDGES = [_edge(j) for j in range(BINS + 1)]


def latency_bin(ms: int) -> int:
    """METRICS 6.11: the number of edges E_1 … E_63 the latency reaches."""
    return sum(1 for j in range(1, BINS) if ms >= EDGES[j])


# ------------------------------------------------------------------------------------------------- capability

def _models_shared(document: dict, models_switch: bool) -> bool:
    return models_switch and "models" in document.get("sections", []) and document["limits"]["maxModels"] >= 1


def active(document: dict, models_switch: bool = True) -> bool:
    """METRICS 11.10: the report carries `models` by consent and the service advertises perfScore."""
    return _models_shared(document, models_switch) and supported(document.get("extensions"), EXTENSION)


def legacy_active(document: dict, models_switch: bool = True) -> bool:
    """The retired taskScore is sent (thinking only) when the service advertises it and not perfScore."""
    return (_models_shared(document, models_switch) and supported(document.get("extensions"), LEGACY)
            and not supported(document.get("extensions"), EXTENSION))


def extension_sent(document: dict, models_switch: bool = True):
    if active(document, models_switch):
        return EXTENSION
    return LEGACY if legacy_active(document, models_switch) else None


# ------------------------------------------------------------------------------------------------- live values

def set_value(values) -> dict:
    """METRICS 6.9 Sets: (form, unrounded score) of each agent -> the highest live score, else the highest resting
    score, else none; the sent score is rounded half away from zero and at most MAX_SCORE."""
    for form in ("live", "resting"):
        scores = [score for f, score in values if f == form and score is not None]
        if scores:
            return {"score": min(MAX_SCORE, round_half_away(max(scores))), "form": form}
    return {"score": None, "form": "none"}


def live_object(values, thinking: float) -> dict:
    return {**set_value(values), "thinkingTokS": round_half_away(thinking, 1)}


def legacy_object(thinking: float) -> dict:
    return {"score": None, "form": "none", "thinkingTokS": round_half_away(thinking, 1)}


# ------------------------------------------------------------------------------------------------- rows

def empty_row(source: str) -> dict:
    return {"source": source, **{f: 0 for f in COUNT_FIELDS}, "bins": [0] * BINS, "toolLnSum": 0.0}


def add_row(target: dict, row: dict) -> dict:
    for field in COUNT_FIELDS:
        target[field] += row[field]
    target["bins"] = [a + b for a, b in zip(target["bins"], row["bins"])]
    target["toolLnSum"] = target["toolLnSum"] + row["toolLnSum"]
    return target


def from_sent(row: dict) -> dict:
    """A row as sent (sparse bins) -> the internal form (dense bins)."""
    out = {k: row[k] for k in ("source",) + COUNT_FIELDS}
    out["bins"] = [0] * BINS
    for k, count in row["latencyBins"]:
        out["bins"][k] += count
    out["toolLnSum"] = row["toolLnSum"]
    return out


def sent_row(row: dict) -> dict:
    """The row as a report carries it: sparse bins in ascending order, toolLnSum to 6 decimals."""
    out = {}
    for field in ROW_FIELDS:
        if field == "latencyBins":
            out[field] = [[k, c] for k, c in enumerate(row["bins"]) if c]
        elif field == "toolLnSum":
            out[field] = round_half_away(row["toolLnSum"], LN_PLACES)
        else:
            out[field] = row[field]
    return out


def merge_rows(rows) -> list:
    merged = {}
    for row in rows:
        add_row(merged.setdefault(row["source"], empty_row(row["source"])), row)
    return [sent_row(merged[source]) for source in sorted(merged, key=ref.source_order)]


def response_row(source: str, response: dict) -> dict:
    """What one measured response adds (METRICS 6.11 Records), activeMs aside."""
    row = empty_row(source)
    start = min(response["startMs"], response["endMs"] - MIN_MS)
    duration = response["endMs"] - start
    row["n"], row["outputSum"], row["durationMsSum"] = 1, response["output"], duration
    first = response.get("firstMs")
    if first is not None and first >= response["startMs"]:
        row["bins"][latency_bin(first - response["startMs"])] = 1
    thought = min(response.get("reasoning", 0), response["output"])
    if source not in NO_REASONING and thought > 0:
        row["reasonN"], row["reasonOutput"], row["reasonDurationMsSum"] = 1, thought, duration
    if source not in NO_CACHE and response["input"] > 0:
        row["cacheN"], row["cacheInput"], row["cacheCached"] = 1, response["input"], response["cachedInput"]
    return row


def call_row(source: str, call: dict) -> dict:
    row = empty_row(source)
    row["toolN"] = 1
    row["toolLnSum"] = math.log(max(call["resultMs"] - call["callMs"], TOOL_FLOOR_MS))
    row["toolFailed"] = 1 if call.get("failed") is True else 0
    return row


# ------------------------------------------------------------------------------------------------- assembly

def assemble(inputs: dict) -> dict:
    """METRICS 11.10 Live values, Membership and `other`: the top-level object and the final models[] list.

    inputs: extension (perfScore or taskScore); maxModels; agents (the 6.9 set of all enabled sources: id, source,
    model id resolved as 11.3, form, unrounded score); thinking (`total` and per-model unrounded liveThinking); models
    (the entries of 11.3 before the extension, with their usage fields); ledger (per model id, rows in the internal
    form of the stats ledger, latencyBins sparse)."""
    legacy = inputs.get("extension", EXTENSION) == LEGACY
    agents, ledger = inputs["agents"], {} if legacy else inputs.get("ledger", {})
    thinking = inputs["thinking"]
    entries = {}
    for model in inputs["models"]:
        entries[model["id"]] = {**copy.deepcopy(model), "sources": list(model["sources"])}

    def entry(model_id):
        return entries.setdefault(model_id, {"id": model_id, "sources": [], "requests": 0, "output": 0,
                                             "measuredOutput": 0, "measuredMs": 0, "generating": 0})

    if not legacy:
        for agent in agents:
            if agent["form"] == "live":
                target = entry(agent["model"])
                if agent["source"] not in target["sources"]:
                    target["sources"].append(agent["source"])
        for model_id, rows in ledger.items():
            target = entry(model_id)
            for row in rows:
                if row["source"] not in target["sources"]:
                    target["sources"].append(row["source"])
    built = []
    for model_id, item in entries.items():
        item["sources"] = sorted(item["sources"], key=ref.source_order)
        item["_values"] = [(a["form"], a["score"]) for a in agents if a["model"] == model_id]
        item["_thinking"] = thinking.get("models", {}).get(model_id, 0.0)
        item["_rows"] = [from_sent(row) for row in ledger.get(model_id, [])]
        built.append(item)
    built.sort(key=lambda m: (-m["output"], m["id"]))
    limit = inputs.get("maxModels", 16)
    if len(built) > limit:
        kept, rest = built[:limit - 1], built[limit - 1:]
        other = {"id": "other", "sources": sorted({s for m in rest for s in m["sources"]}, key=ref.source_order)}
        for field in ("requests", "output", "measuredOutput", "measuredMs", "generating"):
            other[field] = sum(m[field] for m in rest)
        if any("deltaMicroUsd" in m for m in rest):
            other["deltaMicroUsd"] = sum(m.get("deltaMicroUsd", 0) for m in rest)
        other["_values"] = [value for m in rest for value in m["_values"]]
        other["_thinking"] = _seq_sum(m["_thinking"] for m in rest)
        other["_rows"] = [row for m in rest for row in m["_rows"]]
        built = kept + [other]
    models = []
    for item in built:
        out = {key: value for key, value in item.items() if not key.startswith("_")}
        if legacy:
            out["taskScore"] = legacy_object(item["_thinking"])
            out["taskStats"] = []
        else:
            out["perfScore"] = live_object(item["_values"], item["_thinking"])
            out["perfStats"] = merge_rows(item["_rows"])
        models.append(out)
    if legacy:
        return {"taskScore": legacy_object(thinking["total"]), "models": models}
    top = live_object([(a["form"], a["score"]) for a in agents], thinking["total"])
    return {"perfScore": top, "models": models}


def _seq_sum(values) -> float:
    total = 0.0
    for value in values:
        total = total + value
    return total


# ------------------------------------------------------------------------------------------------- ledger

class StatsLedger:
    """METRICS 11.10 Watch, Counting and the statistics ledger, driven by events (the `ledger` vectors)."""

    def __init__(self):
        self.watch = False
        self.rows: dict = {}        # model id -> {source: row}
        self.covered: dict = {}     # (agent, model id) -> coveredUntil
        self.builds: list = []

    def totals(self, field: str) -> int:
        return sum(row[field] for rows in self.rows.values() for row in rows.values())

    def take(self) -> dict:
        taken = {model_id: [sent_row(rows[s]) for s in sorted(rows, key=ref.source_order)]
                 for model_id, rows in sorted(self.rows.items())}
        self.rows = {}
        return taken

    def _add(self, model_id: str, row: dict, agent: str, start: int, end: int) -> None:
        key = (agent, model_id)
        covered = self.covered.get(key)
        row["activeMs"] = max(0, end - (start if covered is None else max(start, covered)))
        self.covered[key] = end if covered is None else max(covered, end)
        target = self.rows.setdefault(model_id, {})
        add_row(target.setdefault(row["source"], empty_row(row["source"])), row)

    def apply(self, event: dict) -> None:
        if "watch" in event:
            if self.watch and not event["watch"]:
                self.rows = {}
            self.watch = event["watch"]
        elif "response" in event:
            r = event["response"]
            if not self.watch or not event.get("counted", True) or self.totals("n") >= MAX_RESPONSES:
                return
            start = min(r["startMs"], r["endMs"] - MIN_MS)
            self._add(r["model"], response_row(r["source"], r), r["agent"], start, r["endMs"])
        elif "call" in event:
            c = event["call"]
            if not self.watch or event.get("seeding") or event["at"] - c["resultMs"] > FIRST_SEEN_MS:
                return
            if not 0 <= c["resultMs"] - c["callMs"] <= TOOL_MAX_MS or self.totals("toolN") >= MAX_CALLS:
                return
            self._add(c["model"], call_row(c["source"], c), c["agent"], c["callMs"], c["resultMs"])
        elif "build" in event:
            taken = self.take()
            self.builds.append({"seq": event["build"], "perfStats": taken if event.get("models", True) else None})
        elif "relaunch" in event:
            self.watch = False
            self.rows = {}
            self.covered = {}
        # "discard" (quiet, a gap, not ready, saturated) and "newRun" leave the ledger as it is.


def simulate_ledger(case: dict) -> dict:
    ledger = StatsLedger()
    for event in case["events"]:
        ledger.apply(event)
    return {"builds": ledger.builds, "pending": ledger.take()}


# ------------------------------------------------------------------------------------------------- checks

def _live_errors(value: dict, pointer: str) -> list:
    if (value["score"] is None) != (value["form"] == "none"):
        return [(pointer + "/score", "score must be null exactly when form is none")]
    return []


def _row_errors(row: dict, pointer: str) -> list:
    errors = []
    n, tools = row["n"], row["toolN"]
    if n + tools < 1:
        errors.append((pointer + "/n", "a row counts at least one response or tool call"))
    keys = [k for k, _ in row["latencyBins"]]
    if keys != sorted(set(keys)) or any(not 0 <= k < BINS for k in keys) or any(c < 1 for _, c in row["latencyBins"]):
        errors.append((pointer + "/latencyBins", "buckets 0–63 ascending, each once, counts of at least 1"))
    elif sum(c for _, c in row["latencyBins"]) > n:
        errors.append((pointer + "/latencyBins", "more latencies than responses"))
    if not MIN_MS * n <= row["durationMsSum"] <= MAX_MS * n:
        errors.append((pointer + "/durationMsSum", "each response lasts 500 ms to 15 minutes"))
    if row["outputSum"] > TOKEN_CAP * n:
        errors.append((pointer + "/outputSum", "above 10^12 tokens per response"))
    if row["reasonN"] > n:
        errors.append((pointer + "/reasonN", "above n"))
    elif not row["reasonN"] <= row["reasonOutput"] <= row["outputSum"]:
        errors.append((pointer + "/reasonOutput", "outside [reasonN, outputSum]"))
    elif not MIN_MS * row["reasonN"] <= row["reasonDurationMsSum"] <= min(row["durationMsSum"],
                                                                          MAX_MS * row["reasonN"]):
        errors.append((pointer + "/reasonDurationMsSum", "outside the bounds of its responses"))
    if row["cacheN"] > n:
        errors.append((pointer + "/cacheN", "above n"))
    elif not row["cacheN"] <= row["cacheInput"] <= TOKEN_CAP * row["cacheN"]:
        errors.append((pointer + "/cacheInput", "outside [cacheN, 10^12 × cacheN]"))
    elif row["cacheCached"] > row["cacheInput"]:
        errors.append((pointer + "/cacheCached", "above cacheInput"))
    low, high = tools * math.log(TOOL_FLOOR_MS), tools * math.log(TOOL_MAX_MS)
    if not low - 1e-6 <= row["toolLnSum"] <= high + 1e-6:
        errors.append((pointer + "/toolLnSum", "outside [toolN · ln 50, toolN · ln 900000]"))
    if row["toolFailed"] > tools:
        errors.append((pointer + "/toolFailed", "above toolN"))
    if row["activeMs"] > row["durationMsSum"] + MAX_MS * tools:
        errors.append((pointer + "/activeMs", "above the sum of the intervals"))
    return errors


def errors(report: dict) -> list:
    """What a service that advertises perfScore rejects with 422 payload_invalid, as (JSON pointer, reason), after
    the schema check. Rows are checked in order; the service reports any one problem."""
    problems = []
    top = report.get("perfScore")
    if top is not None:
        problems += _live_errors(top, "/perfScore")
    responses = calls = 0
    for index, model in enumerate(report.get("models", [])):
        pointer = f"/models/{index}"
        if top is None:
            problems += [(f"{pointer}/{key}", "present without the top-level perfScore")
                         for key in ("perfScore", "perfStats") if key in model]
            continue
        for key in ("perfScore", "perfStats"):
            if key not in model:
                problems.append((f"{pointer}/{key}", "every model entry carries it with the top-level perfScore"))
        if "perfScore" in model:
            problems += _live_errors(model["perfScore"], pointer + "/perfScore")
        seen = set()
        for row_index, row in enumerate(model.get("perfStats", [])):
            row_pointer = f"{pointer}/perfStats/{row_index}"
            if row["source"] not in model["sources"] or row["source"] in seen:
                problems.append((row_pointer + "/source", "source must be unique and belong to the model"))
            seen.add(row["source"])
            problems += _row_errors(row, row_pointer)
            responses += row["n"]
            calls += row["toolN"]
    if responses > MAX_RESPONSES or calls > MAX_CALLS:
        problems.append(("/models", f"more than {MAX_RESPONSES} responses or tool calls in one report"))
    return problems


def legacy_errors(report: dict) -> list:
    """The checks of a service that advertises the retired taskScore (REPORTING-EXTENSIONS 10): the thinking-only
    bodies of current clients pass them."""
    problems = []
    top = report.get("taskScore")
    if top is not None:
        problems += _live_errors(top, "/taskScore")
    for index, model in enumerate(report.get("models", [])):
        pointer = f"/models/{index}"
        if top is None:
            problems += [(f"{pointer}/{key}", "present without the top-level taskScore")
                         for key in ("taskScore", "taskStats") if key in model]
            continue
        for key in ("taskScore", "taskStats"):
            if key not in model:
                problems.append((f"{pointer}/{key}", "every model entry carries it with the top-level taskScore"))
        if "taskScore" in model:
            problems += _live_errors(model["taskScore"], pointer + "/taskScore")
    return problems


def conformance(report: dict) -> list:
    """Client rules that services do not reject on (REPORTING 6.9): order, rounding and the set relations."""
    problems = []
    for name in (EXTENSION, LEGACY):
        top = report.get(name)
        if top is None:
            continue
        entries = [m for m in report.get("models", []) if name in m]
        for value, label in [(top, name)] + [(m[name], f"models.{m['id']}.{name}") for m in entries]:
            if not ref._decimals_ok(value["thinkingTokS"], 1):
                problems.append(f"{label}.thinkingTokS: more than one decimal")
            if (FORM_RANK[value["form"]], value["score"] or 0) > (FORM_RANK[top["form"]], top["score"] or 0):
                problems.append(f"{label}: above the top-level value, which is the highest of every agent")
        total = sum(m[name]["thinkingTokS"] for m in entries)
        if total > top["thinkingTokS"] + 0.05 * len(entries) + 1e-6:
            problems.append(f"models: {name}.thinkingTokS adds up to more than the top-level value")
        if name == LEGACY:
            if top["score"] is not None or any(m.get("taskStats") for m in entries):
                problems.append("taskScore: a current client sends no task score and no task statistics")
            continue
        for model in entries:
            rows = model.get("perfStats", [])
            listed = [row["source"] for row in rows]
            if listed != sorted(listed, key=ref.source_order):
                problems.append(f"models.{model['id']}.perfStats: not in source order")
            for row in rows:
                if not ref._decimals_ok(row["toolLnSum"], LN_PLACES):
                    problems.append(f"models.{model['id']}.perfStats.{row['source']}.toolLnSum: more than 6 decimals")
    return problems


def strip(report: dict, keep=()) -> dict:
    """The body a service keeps when it does not advertise an extension: its members removed (`keep` names the
    advertised ones)."""
    members = {EXTENSION: ("perfScore", "perfStats"), LEGACY: ("taskScore", "taskStats")}
    drop_top = [name for name in members if name not in keep]
    drop_model = [key for name in drop_top for key in members[name]]
    if not any(k in report for k in drop_top) and not any(
            isinstance(m, dict) and any(k in m for k in drop_model) for m in report.get("models", [])):
        return report
    copied = {key: value for key, value in report.items() if key not in drop_top}
    if isinstance(report.get("models"), list):
        copied["models"] = [{k: v for k, v in m.items() if k not in drop_model} if isinstance(m, dict) else m
                            for m in report["models"]]
    return copied


# ------------------------------------------------------------------------------------------------- pooled score

def pooled(rows: list) -> dict:
    """METRICS 6.11 Pooled score over sent rows (any devices, sources and reports): the 6.9 formula on the sums."""
    s = empty_row("")
    for row in rows:
        add_row(s, from_sent(row))
    measures = {}
    if s["n"] >= 3:
        measures["T"] = s["outputSum"] / (s["durationMsSum"] / 1000)
    count = sum(s["bins"])
    if count >= 3:
        target, seen = (count + 1) // 2, 0
        for k, c in enumerate(s["bins"]):
            seen += c
            if seen >= target:
                measures["L"] = 100 * 2 ** ((2 * k + 1) / 8)
                break
    if s["toolN"] >= 3:
        if s["activeMs"] > 0:
            measures["N"] = s["toolN"] / (s["activeMs"] / 60_000)
        measures["U"] = math.exp(s["toolLnSum"] / s["toolN"])
        measures["S"] = 1 - s["toolFailed"] / s["toolN"]
    if s["reasonN"] >= 3:
        measures["R"] = s["reasonOutput"] / (s["reasonDurationMsSum"] / 1000)
    if s["cacheN"] >= 3:
        measures["H"] = s["cacheCached"] / s["cacheInput"]
    ratio = {"T": lambda m: max(0.01, m / 50), "L": lambda m: 5000 / max(m, 250), "N": lambda m: m / 3,
             "R": lambda m: m / 20, "H": lambda m: m / 0.95, "U": lambda m: 400 / m, "S": lambda m: m / 0.95}
    components, log_sum, weight = {}, 0.0, 0.0
    for cid, w in WEIGHTS:
        if cid not in measures:
            components[cid] = None
            continue
        r = ratio[cid](measures[cid])
        r = r if cid == "T" else min(20.0, max(0.05, r))
        components[cid] = {"measure": measures[cid], "ratio": r}
        log_sum, weight = log_sum + w * math.log(r), weight + w
    score = 1000 * math.exp(log_sum / weight) if weight > 0 and s["n"] >= POOLED_MIN_N else None
    return {"n": s["n"], "tools": s["toolN"], "score": score,
            "shown": None if score is None else round_half_away(score), "components": components}


# ------------------------------------------------------------------------------------------------- receiver

class Board:
    """REPORTING-EXTENSIONS 9.6: live values from each online device's latest accepted report, statistics rows
    added once per accepted report and pooled per model. Not production storage."""

    def __init__(self):
        self.live: dict = {}           # device id -> (top-level perfScore, {model id: perfScore}) or None
        self.contributions: dict = {}  # device id -> {(model id, source): sent row}

    def record(self, device_id: str, report: dict) -> None:
        top = report.get("perfScore")
        models = report.get("models", [])
        self.live[device_id] = None if top is None else (
            top, {m["id"]: m["perfScore"] for m in models if "perfScore" in m})
        rows = self.contributions.setdefault(device_id, {})
        for model in models:
            for row in model.get("perfStats", []):
                key = (model["id"], row["source"])
                rows[key] = sent_row(add_row(from_sent(rows[key]) if key in rows else empty_row(row["source"]),
                                             from_sent(row)))

    def forget(self, device_id: str) -> None:
        self.live.pop(device_id, None)
        self.contributions.pop(device_id, None)

    def leaderboard(self, online: list) -> dict:
        """online: the device ids that are online (REPORTING 6.10 presence), in any order."""
        by_model: dict = {}
        for device_id in sorted(self.contributions):
            for (model_id, source), row in self.contributions[device_id].items():
                by_model.setdefault(model_id, {}).setdefault(source, []).append(row)
        live: dict = {}
        top_values, top_thinking = [], 0.0
        for device_id in sorted(online):
            state = self.live.get(device_id)
            if state is None:
                continue
            top, per_model = state
            top_values.append((top["form"], top["score"]))
            top_thinking += top["thinkingTokS"]
            for model_id, value in per_model.items():
                item = live.setdefault(model_id, {"values": [], "thinking": 0.0, "devices": 0})
                item["values"].append((value["form"], value["score"]))
                item["thinking"] += value["thinkingTokS"]
                item["devices"] += 1
        rows = []
        for model_id in sorted(set(by_model) | set(live)):
            sources = by_model.get(model_id, {})
            value = pooled([row for part in sources.values() for row in part])
            item = live.get(model_id)
            rows.append({
                "id": model_id,
                "ranked": model_id not in UNRANKED and value["score"] is not None,
                **value,
                "bySource": [{"source": s, **{k: v for k, v in pooled(sources[s]).items() if k != "components"}}
                             for s in sorted(sources, key=ref.source_order)],
                "live": None if item is None else {**set_value(item["values"]),
                                                   "thinkingTokS": round_half_away(item["thinking"], 1),
                                                   "devices": item["devices"]},
            })
        rows.sort(key=lambda r: (not r["ranked"], -(r["score"] or 0), r["id"]))
        return {"live": {**set_value(top_values), "thinkingTokS": round_half_away(top_thinking, 1)},
                "models": rows}


def simulate_receiver(case: dict) -> dict:
    board = Board()
    for step in case["reports"]:
        if step.get("forget"):
            board.forget(step["device"])
        elif step.get("accepted", True):
            board.record(step["device"], step["body"])
    return board.leaderboard(case["online"])


# ------------------------------------------------------------------------------------------------- vectors

OPUS, GPT, SONNET = "claude-opus-5-5", "gpt-5.5", "claude-sonnet-5-5"
T = 1_800_000_000_000


def _row(source, n=0, output=0, duration=0, bins=(), reason=(0, 0, 0), cache=(0, 0, 0), tools=(0, 0.0, 0),
         active=0):
    return {"source": source, "n": n, "outputSum": output, "durationMsSum": duration,
            "latencyBins": [list(b) for b in bins], "reasonN": reason[0], "reasonOutput": reason[1],
            "reasonDurationMsSum": reason[2], "cacheN": cache[0], "cacheInput": cache[1], "cacheCached": cache[2],
            "toolN": tools[0], "toolLnSum": tools[1], "toolFailed": tools[2], "activeMs": active}


def _doc(extensions, **extra):
    return {"extensions": extensions, **extra}


CAPABILITY_CASES = [
    {"name": "perfScore advertised with the models section and the switch on: perfScore",
     "document": _doc({"perfScore": 1}, sections=["sources", "models", "cost"]), "modelsSwitch": True,
     "expect": "perfScore"},
    {"name": "no sections list means every section", "document": _doc({"perfScore": 1}), "modelsSwitch": True,
     "expect": "perfScore"},
    {"name": "both advertised: only perfScore", "document": _doc({"perfScore": 1, "taskScore": 1}),
     "modelsSwitch": True, "expect": "perfScore"},
    {"name": "only the retired taskScore: taskScore with the thinking speed only",
     "document": _doc({"taskScore": 1}), "modelsSwitch": True, "expect": "taskScore"},
    {"name": "the models switch off: nothing of either is sent", "document": _doc({"perfScore": 1, "taskScore": 1}),
     "modelsSwitch": False, "expect": None},
    {"name": "the service does not list models", "document": _doc({"perfScore": 1}, sections=["sources", "cost"]),
     "modelsSwitch": True, "expect": None},
    {"name": "maxModels 0 sends no models", "document": _doc({"perfScore": 1}, limits={"maxModels": 0}),
     "modelsSwitch": True, "expect": None},
    {"name": "true is not the integer 1 (taskScore still counts)", "document": _doc({"perfScore": True,
                                                                                    "taskScore": 1}),
     "modelsSwitch": True, "expect": "taskScore"},
    {"name": "the string \"1\" is not the integer 1", "document": _doc({"perfScore": "1"}), "modelsSwitch": True,
     "expect": None},
    {"name": "another version is unknown", "document": _doc({"perfScore": 2}), "modelsSwitch": True,
     "expect": None},
    {"name": "null is absent", "document": _doc({"perfScore": None}), "modelsSwitch": True, "expect": None},
]

_LN = math.log
ASSEMBLY_CASES = [
    {"name": "top-level and per-model values; a live model without usage and a model with only statistics get "
             "entries; a resting-only model does not; rows sorted by source, bins sparse, toolLnSum to 6 decimals",
     "input": {"maxModels": 16,
               "agents": [
                   {"id": "a1", "source": "claudeCode", "model": OPUS, "form": "live", "score": 1285.5},
                   {"id": "a2", "source": "claudeCode", "model": OPUS, "form": "resting", "score": 2400.0},
                   {"id": "a3", "source": "codex", "model": GPT, "form": "live", "score": 801.75},
                   {"id": "a4", "source": "omp", "model": "glm-5", "form": "live", "score": 640.25},
                   {"id": "a5", "source": "pi", "model": "kimi-k3", "form": "resting", "score": 999.0},
                   {"id": "a6", "source": "opencode", "model": "deepseek-v4", "form": "none", "score": None}],
               "thinking": {"total": 38.64, "models": {GPT: 38.64}},
               "models": [{"id": OPUS, "sources": ["claudeCode"], "requests": 3, "output": 1500,
                           "measuredOutput": 1400, "measuredMs": 3300, "generating": 2},
                          {"id": GPT, "sources": ["codex"], "requests": 1, "output": 340, "measuredOutput": 302,
                           "measuredMs": 820, "generating": 1},
                          {"id": "deepseek-v4", "sources": ["opencode"], "requests": 0, "output": 0,
                           "measuredOutput": 0, "measuredMs": 0, "generating": 1}],
               "ledger": {SONNET: [_row("claudeDesktop", tools=(1, _LN(2300), 1), active=2300)],
                          OPUS: [_row("claudeDesktop", n=1, output=200, duration=4000, bins=[(20, 1)],
                                      cache=(1, 5000, 4900), active=4000),
                                 _row("claudeCode", n=3, output=1400, duration=3300, bins=[(22, 1), (24, 2)],
                                      cache=(3, 800000, 780000),
                                      tools=(4, _LN(1200) + _LN(300) + _LN(80) + _LN(2500), 0), active=7420)]}},
     "expect": {
         "perfScore": {"score": 1286, "form": "live", "thinkingTokS": 38.6},
         "models": [
             {"id": OPUS, "sources": ["claudeCode", "claudeDesktop"], "requests": 3, "output": 1500,
              "measuredOutput": 1400, "measuredMs": 3300, "generating": 2,
              "perfScore": {"score": 1286, "form": "live", "thinkingTokS": 0.0},
              "perfStats": [_row("claudeCode", n=3, output=1400, duration=3300, bins=[(22, 1), (24, 2)],
                                 cache=(3, 800000, 780000), tools=(4, 24.999932, 0), active=7420),
                            _row("claudeDesktop", n=1, output=200, duration=4000, bins=[(20, 1)],
                                 cache=(1, 5000, 4900), active=4000)]},
             {"id": GPT, "sources": ["codex"], "requests": 1, "output": 340, "measuredOutput": 302,
              "measuredMs": 820, "generating": 1, "perfScore": {"score": 802, "form": "live", "thinkingTokS": 38.6},
              "perfStats": []},
             {"id": SONNET, "sources": ["claudeDesktop"], "requests": 0, "output": 0, "measuredOutput": 0,
              "measuredMs": 0, "generating": 0, "perfScore": {"score": None, "form": "none", "thinkingTokS": 0.0},
              "perfStats": [_row("claudeDesktop", tools=(1, 7.740664, 1), active=2300)]},
             {"id": "deepseek-v4", "sources": ["opencode"], "requests": 0, "output": 0, "measuredOutput": 0,
              "measuredMs": 0, "generating": 1, "perfScore": {"score": None, "form": "none", "thinkingTokS": 0.0},
              "perfStats": []},
             {"id": "glm-5", "sources": ["omp"], "requests": 0, "output": 0, "measuredOutput": 0, "measuredMs": 0,
              "generating": 0, "perfScore": {"score": 640, "form": "live", "thinkingTokS": 0.0},
              "perfStats": []}]}},
    {"name": "beyond maxModels the rest merges into other: the highest value live before resting, thinking summed "
             "before rounding, rows summed per source (bins per bucket, toolLnSum rounded once); a score above "
             "10 000 000 is sent as 10 000 000",
     "input": {"maxModels": 2,
               "agents": [
                   {"id": "a1", "source": "claudeCode", "model": OPUS, "form": "live", "score": 25_000_000.0},
                   {"id": "a2", "source": "codex", "model": GPT, "form": "resting", "score": 880.0},
                   {"id": "a3", "source": "claudeCode", "model": SONNET, "form": "live", "score": 1212.5}],
               "thinking": {"total": 20.08, "models": {OPUS: 10.0, GPT: 10.04, SONNET: 0.04}},
               "models": [{"id": OPUS, "sources": ["claudeCode"], "requests": 2, "output": 900, "measuredOutput": 900,
                           "measuredMs": 3000, "generating": 1},
                          {"id": GPT, "sources": ["codex"], "requests": 1, "output": 300, "measuredOutput": 300,
                           "measuredMs": 1000, "generating": 0},
                          {"id": SONNET, "sources": ["claudeCode"], "requests": 1, "output": 100,
                           "measuredOutput": 100, "measuredMs": 500, "generating": 1}],
               "ledger": {GPT: [_row("codex", n=1, output=300, duration=1000, bins=[(6, 1)], reason=(1, 300, 1000),
                                     cache=(1, 9000, 8000), tools=(1, 0.0000004, 0), active=1000)],
                          SONNET: [_row("claudeCode", n=1, output=100, duration=500, bins=[(6, 1), (9, 0)],
                                        tools=(1, 0.0000004, 0), active=500)]}},
     "expect": {
         "perfScore": {"score": 10_000_000, "form": "live", "thinkingTokS": 20.1},
         "models": [
             {"id": OPUS, "sources": ["claudeCode"], "requests": 2, "output": 900, "measuredOutput": 900,
              "measuredMs": 3000, "generating": 1,
              "perfScore": {"score": 10_000_000, "form": "live", "thinkingTokS": 10.0}, "perfStats": []},
             {"id": "other", "sources": ["codex", "claudeCode"], "requests": 2, "output": 400,
              "measuredOutput": 400, "measuredMs": 1500, "generating": 1,
              "perfScore": {"score": 1213, "form": "live", "thinkingTokS": 10.1},
              "perfStats": [_row("codex", n=1, output=300, duration=1000, bins=[(6, 1)], reason=(1, 300, 1000),
                                 cache=(1, 9000, 8000), tools=(1, 0.0, 0), active=1000),
                            _row("claudeCode", n=1, output=100, duration=500, bins=[(6, 1)], tools=(1, 0.0, 0),
                                 active=500)]}]}},
    {"name": "no agent has a score: none everywhere, thinking still reported",
     "input": {"maxModels": 16,
               "agents": [{"id": "a1", "source": "gemini", "model": "gemini-3-pro", "form": "none", "score": None}],
               "thinking": {"total": 4.25, "models": {"gemini-3-pro": 4.25}},
               "models": [{"id": "gemini-3-pro", "sources": ["gemini"], "requests": 1, "output": 50,
                           "measuredOutput": 50, "measuredMs": 400, "generating": 1}]},
     "expect": {
         "perfScore": {"score": None, "form": "none", "thinkingTokS": 4.3},
         "models": [{"id": "gemini-3-pro", "sources": ["gemini"], "requests": 1, "output": 50, "measuredOutput": 50,
                     "measuredMs": 400, "generating": 1,
                     "perfScore": {"score": None, "form": "none", "thinkingTokS": 4.3}, "perfStats": []}]}},
    {"name": "legacy: a service with only taskScore gets the thinking speed, a null score, empty taskStats and no "
             "extra entries",
     "input": {"extension": "taskScore", "maxModels": 16,
               "agents": [{"id": "a1", "source": "claudeCode", "model": OPUS, "form": "live", "score": 1285.5},
                          {"id": "a2", "source": "omp", "model": "glm-5", "form": "live", "score": 640.25}],
               "thinking": {"total": 38.64, "models": {GPT: 38.64}},
               "models": [{"id": GPT, "sources": ["codex"], "requests": 1, "output": 340, "measuredOutput": 302,
                           "measuredMs": 820, "generating": 1}],
               "ledger": {OPUS: [_row("claudeCode", n=1, output=10, duration=500)]}},
     "expect": {
         "taskScore": {"score": None, "form": "none", "thinkingTokS": 38.6},
         "models": [{"id": GPT, "sources": ["codex"], "requests": 1, "output": 340, "measuredOutput": 302,
                     "measuredMs": 820, "generating": 1,
                     "taskScore": {"score": None, "form": "none", "thinkingTokS": 38.6}, "taskStats": []}]}},
]


def _resp(agent, source, model, start, end, output, first=None, reasoning=0, inp=1000, cached=900):
    return {"agent": agent, "source": source, "model": model, "startMs": T + start, "endMs": T + end,
            "firstMs": None if first is None else T + first, "output": output, "reasoning": reasoning, "input": inp,
            "cachedInput": cached}


def _call(agent, source, model, call, result, failed=False):
    return {"agent": agent, "source": source, "model": model, "callMs": T + call, "resultMs": T + result,
            "failed": failed}


LEDGER_CASES = [
    {"name": "completions while the watch is on count once, in the next build; activeMs by the online union rule",
     "events": [
         {"at": T, "watch": True},
         {"at": T + 4000, "response": _resp("a1", "claudeCode", OPUS, 0, 4000, 200, first=1000, reasoning=50)},
         {"at": T + 4600, "call": _call("a1", "claudeCode", OPUS, 4100, 4600)},
         {"at": T + 4700, "call": _call("a1", "claudeCode", OPUS, 4100, 4700, failed=True)},
         {"at": T + 5000, "build": 1},
         {"at": T + 9000, "response": _resp("a1", "claudeCode", OPUS, 3000, 9000, 300, inp=0, cached=0)},
         {"at": T + 9100, "response": _resp("a2", "codex", GPT, 8900, 9100, 10, first=8950)},
         {"at": T + 10_000, "build": 2}, {"at": T + 15_000, "build": 3}],
     # a1: [0,4000] adds 4000; [4100,4600] 500; [4100,4700] 100 (covered to 4600); [3000,9000] 9000 − 4700 = 4300.
     # a2: 200 ms widened to 500: [8600, 9100] adds 500, latency 50 ms -> bucket 0.
     "expect": {"builds": [
         {"seq": 1, "perfStats": {OPUS: [_row("claudeCode", n=1, output=200, duration=4000, bins=[(13, 1)],
                                              reason=(1, 50, 4000), cache=(1, 1000, 900),
                                              tools=(2, round_half_away(_LN(500) + _LN(600), LN_PLACES), 1),
                                              active=4600)]}},
         {"seq": 2, "perfStats": {GPT: [_row("codex", n=1, output=10, duration=500, bins=[(0, 1)],
                                             reason=(0, 0, 0), cache=(1, 1000, 900), active=500)],
                                  OPUS: [_row("claudeCode", n=1, output=300, duration=6000, active=4300)]}},
         {"seq": 3, "perfStats": {}}], "pending": {}}},
    {"name": "history is never counted: a response the report ledger did not count (seeding, older than 2 minutes, "
             "a disabled source), a call read while seeding or whose result is older than 2 minutes, a call over "
             "15 minutes; nothing counts before the watch",
     "events": [
         {"at": T, "response": _resp("a1", "codex", GPT, 0, 1000, 50)},
         {"at": T + 1000, "watch": True},
         {"at": T + 2000, "response": _resp("a1", "codex", GPT, 1000, 2000, 50), "counted": False},
         {"at": T + 3000, "call": _call("a1", "codex", GPT, 2000, 2500), "seeding": True},
         {"at": T + 200_000, "call": _call("a1", "codex", GPT, 70_000, 79_999)},
         {"at": T + 200_000, "call": _call("a1", "codex", GPT, 80_000, 80_000)},
         {"at": T + 1_000_100, "call": _call("a1", "codex", GPT, 100_000, 1_000_001)},
         {"at": T + 1_200_000, "build": 1}],
     "expect": {"builds": [{"seq": 1, "perfStats": {GPT: [_row("codex", tools=(1, 3.912023, 0), active=0)]}}],
                "pending": {}}},
    {"name": "a break (pause, models switch off, the extension dropped, network off) empties the ledger; a leaving "
             "report takes it first; discards, new runs and a lost report keep or lose it as builds do",
     "events": [
         {"at": T, "watch": True},
         {"at": T + 1000, "call": _call("a1", "claudeCode", OPUS, 0, 1000)},
         {"at": T + 2000, "build": 1, "activity": "leaving"}, {"at": T + 2000, "watch": False},
         {"at": T + 3000, "call": _call("a1", "claudeCode", OPUS, 2500, 3000)},
         {"at": T + 60_000, "watch": True},
         {"at": T + 61_000, "call": _call("a1", "claudeCode", OPUS, 60_000, 61_000)},
         {"at": T + 62_000, "discard": "quiet"}, {"at": T + 63_000, "discard": "gap"},
         {"at": T + 64_000, "newRun": "seq_conflict"},
         {"at": T + 65_000, "build": 1},
         {"at": T + 66_000, "call": _call("a1", "claudeCode", OPUS, 65_000, 66_000)},
         {"at": T + 66_500, "watch": False}, {"at": T + 67_000, "watch": True},
         {"at": T + 68_000, "call": _call("a1", "claudeCode", OPUS, 67_000, 68_000)},
         {"at": T + 69_000, "build": 2, "models": False},
         {"at": T + 70_000, "build": 3}],
     "expect": {"builds": [
         {"seq": 1, "perfStats": {OPUS: [_row("claudeCode", tools=(1, 6.907755, 0), active=1000)]}},
         {"seq": 1, "perfStats": {OPUS: [_row("claudeCode", tools=(1, 6.907755, 0), active=1000)]}},
         {"seq": 2, "perfStats": None}, {"seq": 3, "perfStats": {}}], "pending": {}}},
    {"name": "relaunch: nothing survives, the union starts over",
     "events": [
         {"at": T, "watch": True},
         {"at": T + 5000, "response": _resp("a1", "codex", GPT, 0, 5000, 100, inp=0)},
         {"at": T + 6000, "relaunch": True}, {"at": T + 7000, "watch": True},
         {"at": T + 8000, "response": _resp("a1", "codex", GPT, 2000, 8000, 100, inp=0)},
         {"at": T + 9000, "build": 1}],
     "expect": {"builds": [{"seq": 1, "perfStats": {GPT: [_row("codex", n=1, output=100, duration=6000,
                                                               active=6000)]}}], "pending": {}}},
]


def _example(name: str) -> dict:
    return copy.deepcopy(ref.load_examples()[name])


def _validation_cases() -> list:
    good = _example("report-perfscore")
    cases = [{"name": "the example body is valid", "body": ref.compact_json(good).decode("utf-8"), "expect": []}]

    def mutate(name, change, expect):
        body = copy.deepcopy(good)
        change(body)
        cases.append({"name": name, "body": ref.compact_json(body).decode("utf-8"), "expect": expect})

    def row(body, model=0, index=0):
        return body["models"][model]["perfStats"][index]

    mutate("a body without the extension is valid (an older client, or models not shared)",
           lambda b: [b.pop("perfScore")] + [(m.pop("perfScore"), m.pop("perfStats")) for m in b["models"]], [])
    mutate("models dropped for size: the top-level object alone is valid", lambda b: b.pop("models"), [])
    mutate("a score of 10 000 000 is valid", lambda b: b["perfScore"].update(score=10_000_000), [])
    mutate("a score above 10 000 000", lambda b: b["perfScore"].update(score=10_000_001), ["/perfScore/score"])
    mutate("form none needs a null score", lambda b: b["perfScore"].update(form="none"), ["/perfScore/score"])
    mutate("a null score needs form none",
           lambda b: b["models"][1]["perfScore"].update(score=None), ["/models/1/perfScore/score"])
    mutate("an unknown form", lambda b: b["perfScore"].update(form="sleeping"), ["/perfScore/form"])
    mutate("a model entry without perfStats while the top level has perfScore",
           lambda b: b["models"][1].pop("perfStats"), ["/models/1/perfStats"])
    mutate("per-model fields without the top-level object", lambda b: b.pop("perfScore"),
           ["/models/0/perfScore", "/models/0/perfStats", "/models/1/perfScore", "/models/1/perfStats",
            "/models/2/perfScore", "/models/2/perfStats"])
    mutate("a row whose source the model does not list", lambda b: row(b).update(source="codex"),
           ["/models/0/perfStats/0/source"])
    mutate("two rows for one source", lambda b: b["models"][0]["perfStats"].append(copy.deepcopy(row(b))),
           ["/models/0/perfStats/1/source"])
    mutate("a row with no response and no call",
           lambda b: b["models"][2]["perfStats"][0].update(toolN=0, toolLnSum=0.0, toolFailed=0, activeMs=0),
           ["/models/2/perfStats/0/n"])
    mutate("buckets out of order", lambda b: row(b).update(latencyBins=[[24, 2], [22, 1]]),
           ["/models/0/perfStats/0/latencyBins"])
    mutate("a bucket above 63", lambda b: row(b).update(latencyBins=[[22, 1], [64, 2]]),
           ["/models/0/perfStats/0/latencyBins"])
    mutate("more latencies than responses", lambda b: row(b).update(latencyBins=[[22, 2], [24, 2]]),
           ["/models/0/perfStats/0/latencyBins"])
    mutate("durations shorter than 500 ms per response", lambda b: row(b).update(durationMsSum=1499),
           ["/models/0/perfStats/0/durationMsSum"])
    mutate("more cached than input tokens", lambda b: row(b).update(cacheCached=800001),
           ["/models/0/perfStats/0/cacheCached"])
    mutate("a reasoning count above n", lambda b: row(b).update(reasonN=4, reasonOutput=4, reasonDurationMsSum=2000),
           ["/models/0/perfStats/0/reasonN"])
    mutate("a tool time sum below toolN · ln 50", lambda b: row(b).update(toolLnSum=15.6),
           ["/models/0/perfStats/0/toolLnSum"])
    mutate("more failed calls than calls", lambda b: row(b).update(toolFailed=5),
           ["/models/0/perfStats/0/toolFailed"])
    mutate("active time above the sum of the intervals", lambda b: row(b).update(activeMs=3_603_301),
           ["/models/0/perfStats/0/activeMs"])

    def flood(body):
        row(body).update(n=60_000, durationMsSum=60_000 * 1000)
        body["models"][1]["perfStats"] = [_row("codex", n=40_001, output=1, duration=40_001 * 1000)]
    mutate("more than 100 000 responses in one report", flood, ["/models"])
    return cases


def body_problems(body: dict, schema: dict) -> list:
    """The pointers a service that advertises perfScore answers with 422 payload_invalid (any one of them)."""
    found = [ref.json_pointer(error) for error in ref.schema_errors(body, schema["$defs"]["report"], schema)]
    if found:
        return found
    return [pointer for pointer, _ in errors(body)]


RECEIVER_DEVICES = ("6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b", "0b7e4d2c-9a1f-4c3e-b5d6-7e8f9a0b1c2d",
                    "5a6b7c8d-9e0f-4a1b-8c2d-3e4f5a6b7c8d")


def _body(top, models):
    return {"perfScore": top, "models": models}


def _entry(model_id, sources, value, rows):
    return {"id": model_id, "sources": sources, "perfScore": value, "perfStats": rows}


def _receiver_cases() -> list:
    d1, d2, d3 = RECEIVER_DEVICES
    live = lambda score, thinking=0.0: {"score": score, "form": "live", "thinkingTokS": thinking}  # noqa: E731
    resting = lambda score: {"score": score, "form": "resting", "thinkingTokS": 0.0}  # noqa: E731
    # Opus: 12 + 10 responses of 2 s and 100 tokens (T = 2200 / 44 s = 50 -> 1), latencies in bucket 21 (12 + 10:
    # the 11th value is in bucket 21 -> 5000 / (100 · 2^(43/8))), tools 4 + 0 of 400 ms (U = 400 -> 1, S = 1 ->
    # 1/0.95), activeMs 30 000 + 20 000 (N = 4 / (50 000 / 60 000) = 4.8 -> 1.6), cache 22 of 1000 with 950 (H -> 1).
    row_a = _row("claudeCode", n=12, output=1200, duration=24000, bins=[(21, 12)], cache=(12, 12000, 11400),
                 tools=(4, round_half_away(4 * _LN(400), LN_PLACES), 0), active=30000)
    row_b = _row("claudeDesktop", n=10, output=1000, duration=20000, bins=[(21, 10)], cache=(10, 10000, 9500),
                 active=20000)
    # GPT: 15 responses only (Σ n < 20): no score, not ranked.
    row_c = _row("codex", n=15, output=1500, duration=15000, bins=[(25, 15)], active=15000)
    reports = [
        {"device": d1, "body": _body(live(1286, 12.0), [_entry(OPUS, ["claudeCode"], live(1286, 12.0), [row_a])])},
        {"device": d1, "accepted": False, "note": "a duplicate (same run and seq) is never recorded again",
         "body": _body(live(1286, 12.0), [_entry(OPUS, ["claudeCode"], live(1286, 12.0), [row_a])])},
        {"device": d2, "body": _body(live(802, 30.25), [_entry(GPT, ["codex"], live(802, 30.25), [row_c]),
                                                        _entry(OPUS, ["claudeDesktop"], resting(2400), [row_b])])},
        {"device": d3, "body": _body(live(9999, 5.0), [_entry(OPUS, ["claudeCode"], live(9999, 5.0), [row_a])])},
        {"device": d3, "forget": True, "note": "forget this device: its rows and live values go"},
        {"device": d2, "body": {"models": [{"id": GPT, "sources": ["codex"]}]},
         "note": "a later report without the extension (models off or an older client) clears the live values, "
                 "keeps the sums"},
    ]
    l_measure = 100 * 2 ** (43 / 8)
    u = math.exp(round_half_away(4 * _LN(400), LN_PLACES) / 4)   # the sent toolLnSum has 6 decimals
    r = {"T": 1.0, "L": 5000 / l_measure, "N": 1.6, "H": 1.0, "U": 400 / u, "S": 1 / 0.95}
    w = {"T": 0.30, "L": 0.20, "N": 0.15, "H": 0.10, "U": 0.10, "S": 0.05}
    opus_score = 1000 * math.exp(sum(w[c] * math.log(r[c]) for c in w) / sum(w.values()))
    return [{
        "name": "live values from each online device's latest report (highest, live before resting; thinking "
                "summed); rows added once per accepted report and pooled per model with the 6.9 formula; a model "
                "below 20 responses has no score and is not ranked; a forgotten device leaves no trace",
        "reports": reports,
        "online": [d1, d2],
        "expect": {
            "live": {"score": 1286, "form": "live", "thinkingTokS": 12.0},
            "models": [
                {"id": OPUS, "ranked": True, "n": 22, "tools": 4, "score": opus_score,
                 "shown": round_half_away(opus_score),
                 "components": {"T": {"measure": 50.0, "ratio": 1.0},
                                "L": {"measure": l_measure, "ratio": 5000 / l_measure},
                                "N": {"measure": 4.8, "ratio": 1.6}, "R": None,
                                "H": {"measure": 0.95, "ratio": 1.0}, "U": {"measure": u, "ratio": 400 / u},
                                "S": {"measure": 1.0, "ratio": 1 / 0.95}},
                 "bySource": [{"source": "claudeCode", "n": 12, "tools": 4, "score": None, "shown": None},
                              {"source": "claudeDesktop", "n": 10, "tools": 0, "score": None, "shown": None}],
                 "live": {"score": 1286, "form": "live", "thinkingTokS": 12.0, "devices": 1}},
                {"id": GPT, "ranked": False, "n": 15, "tools": 0, "score": None, "shown": None,
                 "components": {"T": {"measure": 100.0, "ratio": 2.0},
                                "L": {"measure": 100 * 2 ** (51 / 8), "ratio": 5000 / (100 * 2 ** (51 / 8))},
                                "N": None, "R": None, "H": None, "U": None, "S": None},
                 "bySource": [{"source": "codex", "n": 15, "tools": 0, "score": None, "shown": None}],
                 "live": None},
            ]}}]


def build_vectors() -> dict:
    return {
        "_comment": "The perfScore extension (METRICS 11.10, REPORTING-EXTENSIONS 9), written by "
                    "spec/tools/report_reference.py --write from spec/tools/report_perf_score.py, whose expectations "
                    "are written by hand. capability: discovery and the models switch -> the extension a report "
                    "carries (perfScore, the retired taskScore with the thinking speed only, or none). assembly: "
                    "agents' values, per-model thinking, the models of 11.3 and the statistics ledger -> the "
                    "top-level object and the final models[] (`extension: taskScore` for the legacy form). ledger: "
                    "events in order -> the perfStats each build carries (null: models dropped for size) and what "
                    "is still pending. validation: a body (compact JSON text) -> the JSON pointers a service that "
                    "advertises perfScore may name in 422 payload_invalid (empty: accepted). receiver: accepted "
                    "reports -> the pooled model leaderboard (numbers within 1e-9).",
        "edges": EDGES,
        "capability": CAPABILITY_CASES,
        "assembly": ASSEMBLY_CASES,
        "ledger": LEDGER_CASES,
        "validation": _validation_cases(),
        "receiver": _receiver_cases(),
    }


def _close(a, b) -> bool:
    if isinstance(a, float) or isinstance(b, float):
        return isinstance(a, (int, float)) and isinstance(b, (int, float)) and \
            abs(a - b) <= 1e-9 * max(1.0, abs(a), abs(b))
    if isinstance(a, dict) and isinstance(b, dict):
        return a.keys() == b.keys() and all(_close(a[k], b[k]) for k in a)
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(_close(x, y) for x, y in zip(a, b))
    return a == b


def check_vectors(vectors: dict) -> list:
    failures = []
    schema = ref.load_schema()
    if vectors["edges"] != EDGES:
        failures.append("perfScore.edges differ")
    for case in vectors["capability"]:
        document = {"protocol": "agent-speed-report", "versions": [1],
                    "service": {"name": "x", "terms": "https://example.com/t", "privacy": "https://example.com/p"},
                    "api": "https://api.example.com", **case["document"]}
        got = extension_sent(ref.parse_discovery(document, ref.HTTPS_DISCOVERY), case["modelsSwitch"])
        if got != case["expect"]:
            failures.append(f"perfScore.capability: {case['name']}: {got}")
    for case in vectors["assembly"]:
        got = assemble(case["input"])
        if not _close(got, case["expect"]):
            failures.append(f"perfScore.assembly: {case['name']}: {got}")
    for case in vectors["ledger"]:
        got = simulate_ledger(case)
        if not _close(got, case["expect"]):
            failures.append(f"perfScore.ledger: {case['name']}: {got}")
    for case in vectors["validation"]:
        got = body_problems(ref.strict_json(case["body"].encode("utf-8")), schema)
        if got != case["expect"]:
            failures.append(f"perfScore.validation: {case['name']}: {got}")
    for case in vectors["receiver"]:
        got = simulate_receiver(case)
        if not _close(got, case["expect"]):
            failures.append(f"perfScore.receiver: {case['name']}: {got}")
    return failures


def hand_check() -> list:
    """The hand-written expectations against the reference, before --write stores them."""
    return check_vectors({"edges": EDGES, "capability": CAPABILITY_CASES, "assembly": ASSEMBLY_CASES,
                          "ledger": LEDGER_CASES, "validation": _validation_cases(), "receiver": _receiver_cases()})
