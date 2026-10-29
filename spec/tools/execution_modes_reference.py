#!/usr/bin/env python3
"""Independent execution-mode normalization and report-extension checks."""

from __future__ import annotations

import json
import re
from collections import defaultdict
from pathlib import Path

KINDS = ("serviceTier", "accelerator", "modelVariant", "routing", "workflow")
# `effort` is a fact kind and `speed` a derived breakdown; both stay local and are never reported (report kinds: KINDS).
LOCAL_KINDS = ("effort", "speed")
EFFORT_UNRESOLVED = ("auto", "default", "inherit", "inherited", "unknown")
EFFORT_LEVELS = {"off": 0, "minimal": 1, "low": 1, "medium": 2, "high": 3, "xhigh": 4, "max": 5, "ultra": 5,
                 "ultracode": 5}
EFFORT_TOP = ("ultra", "ultracode")
EFFORT_ORDER = ("ultracode", "ultra", "max", "xhigh", "high", "medium", "low", "minimal", "off")
SPEED_BOLTS = {"fast": 1, "highspeed": 1, "ultrafast": 2}
SPEED_ORDER = ("ultrafast", "fast", "highspeed", "standard")
UNKNOWN = {"mode": "unknown", "evidence": "unknown"}
EVIDENCE = ("configured", "selected", "recorded", "served")
SCOPES = ("configuration", "session", "turn", "request", "response")
FIELDS = ("requests", "output", "measuredOutput", "measuredMs")
EXTENSIONS = ("modelHarnessRequests", "executionModes", "profileAvatar", "sharedIdentity", "taskScore",
              "perfScore")
MAX_INTEGER = 9007199254740991
TOKEN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:-]*$", re.ASCII)


def identifier(value, limit=64):
    return isinstance(value, str) and 0 < len(value) <= limit and TOKEN.fullmatch(value) is not None


def supported(extensions, name, *, models_enabled=True):
    return (models_enabled and isinstance(extensions, dict) and type(extensions.get(name)) is int
            and extensions[name] == 1)


def normalize(adapter, raw, provider=None, evidence="served", scope="response"):
    if not identifier(raw) or (provider is not None and not identifier(provider, 32)):
        return None
    if evidence not in EVIDENCE or scope not in SCOPES:
        return None
    token = raw.lower()
    namespace = provider.lower() if provider else None
    if adapter == "serviceTier":
        if namespace == "anthropic":
            return None
        kind = "serviceTier"
        value = {"default": "standard", "priority": "fast"}.get(token, token) if namespace == "openai" else token
    elif adapter == "claudeSpeed":
        kind, value = "serviceTier", token
    elif adapter == "kimiModel":
        if raw not in ("k3.1-pro-highspeed", "kimi-for-coding-highspeed"):
            return None
        kind, value = "modelVariant", "highspeed"
    elif adapter == "routing":
        slots = {"fast-model": "fast", "balanced-model": "balanced", "deep-model": "deep", "auto": "auto"}
        if raw not in slots:
            return None
        kind, value = "routing", slots[raw]
    elif adapter == "workflow":
        if token not in ("fast", "planning"):
            return None
        kind, value = "workflow", token
    elif adapter == "effort":
        if token in EFFORT_UNRESOLVED:
            return None
        kind, value = "effort", "off" if token == "none" else token
    else:
        return None
    result = {"kind": kind, "value": value, "rawValue": raw, "evidence": evidence, "scope": scope}
    if namespace:
        result["provider"] = namespace
    return result


def attribution(facts, kind):
    eligible = [(index, fact) for index, fact in enumerate(facts)
                if fact.get("kind") == kind and fact.get("evidence") in EVIDENCE[1:]]
    if not eligible:
        return {"mode": "unknown", "evidence": "unknown"}
    _, chosen = max(eligible, key=lambda pair: (EVIDENCE.index(pair[1]["evidence"]),
                                               SCOPES.index(pair[1].get("scope", "request")), pair[0]))
    if chosen["value"] in ("auto", "inherit", "inherited", "unknown", "none"):
        return {"mode": "unknown", "evidence": "unknown"}
    result = {"mode": chosen["value"], "evidence": chosen["evidence"]}
    if chosen.get("provider"):
        result["provider"] = chosen["provider"]
    return result


def speed_attribution(facts):
    """The derived `speed` breakdown: service tier, else accelerator, else a highspeed model variant."""
    for kind in ("serviceTier", "accelerator", "modelVariant"):
        result = attribution(facts, kind)
        if result["mode"] != "unknown" and (kind != "modelVariant" or result["mode"] == "highspeed"):
            return result
    return dict(UNKNOWN)


def local_attribution(facts, kind):
    return speed_attribution(facts) if kind == "speed" else attribution(facts, kind)


def effort_display(value):
    """Meter level 0-5 (None: a token without a level, shown as its word only) and whether it is a top rung."""
    return {"level": EFFORT_LEVELS.get(value), "top": value in EFFORT_TOP}


def display_order(kind, values):
    order = EFFORT_ORDER if kind == "effort" else SPEED_ORDER
    return sorted(values, key=lambda v: (v == "unknown", order.index(v) if v in order else len(order), v))


def put(facts, fact):
    """Insert like the platform cores: a fact replaces the one with the same kind, evidence, provider and scope."""
    if fact is None:
        return facts
    same = (fact["kind"], fact["evidence"], fact.get("provider"), fact["scope"])
    return [f for f in facts if (f["kind"], f["evidence"], f.get("provider"), f["scope"]) != same] + [fact]


def model_extension_errors(model, *, extensions=None, pointer="/models/0"):
    errors = []
    enabled = lambda name: extensions is None or supported(extensions, name)
    if "harnessRequests" in model and enabled("modelHarnessRequests"):
        rows = model["harnessRequests"]
        path = pointer + "/harnessRequests"
        if not isinstance(rows, list) or len(rows) > 8:
            errors.append((path, "expected an array of at most 8 rows"))
        else:
            sources, total = set(), 0
            for index, row in enumerate(rows):
                p = path + "/" + str(index)
                if not isinstance(row, dict) or not {"source", "requests"}.issubset(row):
                    errors.append((p, "source and requests are required"))
                    continue
                source, count = row["source"], row["requests"]
                if not isinstance(source, str) or source not in model.get("sources", []) or source in sources:
                    errors.append((p + "/source", "source must be unique and belong to the model"))
                if type(count) is not int or not 0 <= count <= MAX_INTEGER:
                    errors.append((p + "/requests", "expected a nonnegative safe integer"))
                else:
                    total += count
                if isinstance(source, str):
                    sources.add(source)
            if total != model.get("requests"):
                errors.append((path, "requests do not equal the model total"))
    if "modeUsage" not in model or not enabled("executionModes"):
        return errors
    rows = model["modeUsage"]
    path = pointer + "/modeUsage"
    if not isinstance(rows, list) or len(rows) > 64:
        return errors + [(path, "expected an array of at most 64 rows")]
    seen, sums = set(), defaultdict(lambda: defaultdict(int))
    for index, row in enumerate(rows):
        p = path + "/" + str(index)
        required = {"source", "kind", "mode", "evidence", *FIELDS}
        if not isinstance(row, dict) or not required.issubset(row):
            errors.append((p, "missing mode fields"))
            continue
        source, kind, mode, evidence = (row[k] for k in ("source", "kind", "mode", "evidence"))
        provider = row.get("provider", "")
        valid_key = True
        if not isinstance(source, str) or source not in model.get("sources", []):
            errors.append((p + "/source", "source does not belong to the model"))
            valid_key = False
        if kind not in KINDS:
            errors.append((p + "/kind", "unknown kind"))
            valid_key = False
        if not identifier(mode) or ("provider" in row and not identifier(provider, 32)):
            errors.append((p, "invalid mode or provider identifier"))
            valid_key = False
        if evidence not in (*EVIDENCE[1:], "unknown"):
            errors.append((p + "/evidence", "invalid evidence"))
            valid_key = False
        if (mode == "unknown") != (evidence == "unknown"):
            errors.append((p, "unknown mode and evidence must agree"))
        if valid_key:
            key = (source, provider, kind, mode, evidence)
            if key in seen:
                errors.append((p, "duplicate mode row"))
            seen.add(key)
        for field in FIELDS:
            value = row[field]
            if type(value) is not int or not 0 <= value <= MAX_INTEGER:
                errors.append((p + "/" + field, "expected a nonnegative safe integer"))
            elif kind in KINDS:
                sums[kind][field] += value
        if (type(row["measuredOutput"]) is int and type(row["output"]) is int
                and row["measuredOutput"] > row["output"]):
            errors.append((p + "/measuredOutput", "exceeds output"))
    for kind, totals in sums.items():
        for field in FIELDS:
            if totals[field] != model.get(field):
                errors.append((path, kind + "." + field + " does not equal the model total"))
    return errors


def report_extension_errors(report, extensions=None):
    return [problem for index, model in enumerate(report.get("models", []))
            for problem in model_extension_errors(model, extensions=extensions, pointer="/models/" + str(index))]


def check_fixtures():
    path = Path(__file__).resolve().parent.parent / "fixtures/execution-mode-cases.json"
    data = json.loads(path.read_text())
    count = 0
    for case in data["normalization"]:
        actual = normalize(case["adapter"], case["rawValue"], case.get("provider"), case["evidence"], case["scope"])
        assert actual == case["expected"], (case["id"], actual, case["expected"])
        count += 1
    for case in data["attribution"]:
        assert attribution(case["facts"], case["kind"]) == case["expected"], case["id"]
        count += 1
    for case in data["capabilities"]:
        assert supported(case["extensions"], "executionModes", models_enabled=case["modelsEnabled"]) == case["expected"]
        count += 1
    for case in data["speed"]:
        assert speed_attribution(case["facts"]) == case["expected"], case["id"]
        count += 1
    for case in data["effortDisplay"]:
        assert effort_display(case["value"]) == case["expected"], case["value"]
        count += 1
    for case in data["speedBolts"]:
        assert SPEED_BOLTS.get(case["value"], 0) == case["bolts"], case["value"]
        count += 1
    for case in data["displayOrder"]:
        assert display_order(case["kind"], case["values"]) == case["expected"], case["id"]
        count += 1
    return count


if __name__ == "__main__":
    print(str(check_fixtures()) + " shared execution-mode cases passed")
